using System.IO;
using FMOD;
using FMODUnity;
using TMPro;
using UnityEngine;
using UnityEngine.Video;

namespace DrivingRangeTheater
{
    // One instance per driving-range scene. Rotates through a small ring of VideoPlayers so
    // adjacent videos are already Prepared by the time the player cycles, and plays audio via
    // FMOD sidecars since the game has Unity's native audio disabled.
    //
    // Layout:
    //   _slots[0..2] — three VideoPlayer + RenderTexture pairs. On each ApplyIndex(N), the
    //                  "center" slot shows video N, and the other two slots Prepare N-1 / N+1
    //                  in the background so cycling lands on an already-prepared player.
    //   _activeSlot  — which of the three slots is currently displayed. Cycling rotates this
    //                  pointer rather than reallocating the players.
    internal class TheaterController : MonoBehaviour
    {
        public static TheaterController Current;

        private struct ScreenTextureBinding
        {
            public string Name;
            public Texture Texture;
            public Vector2 Scale;
            public Vector2 Offset;
        }

        private sealed class Slot
        {
            public string Label;
            public VideoPlayer Player;
            public RenderTexture RenderTexture;
            public int VideoIndex = -1;  // which entry this slot is currently bound to
            public bool Prepared;
        }

        private Slot[] _slots;
        private int _activeSlot;
        private Renderer _screenRenderer;
        private Material _screenMaterial;
        private ScreenTextureBinding[] _screenBindings = System.Array.Empty<ScreenTextureBinding>();
        private RenderTexture _screenRenderTexture;
        private TextMeshPro _statusText;
        private DrivingRangeStaticCameraManager _vanillaManager;

        // FMOD audio — one loaded Sound per video entry, created lazily on first prepare.
        // The active Channel is replaced each cycle.
        private FMOD.Sound[] _sounds;
        private FMOD.Channel _activeChannel;
        private bool _activeChannelValid;
        private float _driftCheckTimer;
        private const float DriftCheckInterval = 2.0f;   // seconds
        private const int DriftToleranceMs = 250;
        private Vector3 _lastAudioWorldPos;
        private const float AudioMinDistance = 6f;
        private const float AudioMaxDistance = 32f;
        private int _activeAudioIndex = -1;

        public void Initialize(Renderer screenRenderer, DrivingRangeStaticCameraManager vanillaMgr)
        {
            Current = this;
            _screenRenderer = screenRenderer;
            _screenMaterial = screenRenderer.material;
            _vanillaManager = vanillaMgr;

            // Grab the vanilla screen RT so we can composite into the exact texture the in-scene
            // display already samples. Decode itself still happens into our own 16:9 RTs.
            try
            {
                var camerasField = HarmonyLib.Traverse.Create(vanillaMgr).Field("cameras");
                var cams = camerasField.GetValue() as DrivingRangeStaticCamera[];
                if (cams != null && cams.Length > 0)
                {
                    var camTrav = HarmonyLib.Traverse.Create(cams[0]).Field("thisCamera");
                    var cam = camTrav.GetValue() as Camera;
                    if (cam != null && cam.targetTexture != null)
                    {
                        _screenRenderTexture = cam.targetTexture;
                        ApplyConfiguredScreenResolution();
                        Plugin.Log?.LogInfo($"Theater RT sized from vanilla camera: {_screenRenderTexture.width}x{_screenRenderTexture.height} ({_screenRenderTexture.name})");
                    }
                }
            }
            catch (System.Exception ex) { Plugin.Log?.LogWarning($"RT size probe failed: {ex.Message}"); }

            // Three slots — prev / current / next. We don't need more because cycling only
            // moves one step at a time.
            _slots = new Slot[3];
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = CreateSlot("SBG-TheaterSlot-" + i, 1920, 1080);

            // Identify the screen's color texture slot(s) to bind on each cycle. Swapping every
            // texture property is too blunt — masks / normals should stay as-authored.
            var shader = _screenMaterial.shader;
            int propCount = shader.GetPropertyCount();
            var swapped = new System.Collections.Generic.List<string>();
            for (int i = 0; i < propCount; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture)
                    continue;

                string prop = shader.GetPropertyName(i);
                string lower = prop.ToLowerInvariant();
                if (lower.Contains("maintex") || lower.Contains("basemap") || lower.Contains("emission"))
                    swapped.Add(prop);
            }
            if (swapped.Count == 0)
            {
                if (_screenMaterial.HasProperty("_MainTex")) swapped.Add("_MainTex");
                if (_screenMaterial.HasProperty("_BaseMap")) swapped.Add("_BaseMap");
                if (_screenMaterial.HasProperty("_EmissionMap")) swapped.Add("_EmissionMap");
            }
            _screenTextureSlots = swapped.ToArray();
            _screenBindings = new ScreenTextureBinding[_screenTextureSlots.Length];
            for (int i = 0; i < _screenTextureSlots.Length; i++)
            {
                string slot = _screenTextureSlots[i];
                _screenBindings[i] = new ScreenTextureBinding
                {
                    Name = slot,
                    Texture = _screenMaterial.GetTexture(slot),
                    Scale = _screenMaterial.GetTextureScale(slot),
                    Offset = _screenMaterial.GetTextureOffset(slot),
                };

                // Neutralize any overscan-ish texture transform so the full video frame lands on
                // the visible UV island instead of inheriting the camera feed's crop.
                _screenMaterial.SetTextureScale(slot, Vector2.one);
                _screenMaterial.SetTextureOffset(slot, Vector2.zero);
            }
            if (_screenMaterial.HasProperty("_EmissionColor"))
                _screenMaterial.SetColor("_EmissionColor", Color.white * 1.2f);
            _screenMaterial.EnableKeyword("_EMISSION");

            CreateStatusText();
            _sounds = new FMOD.Sound[VideoLibrary.Entries.Count];
        }

        private string[] _screenTextureSlots = System.Array.Empty<string>();

        private Slot CreateSlot(string label, int rtW, int rtH)
        {
            var rt = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32)
            {
                name = label + "-RT",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
            };
            rt.Create();

            // Each slot lives on its own child GameObject — multiple VideoPlayer components on
            // the same GameObject gets into weird territory (shared target/audio routing).
            var child = new GameObject(label);
            child.transform.SetParent(transform, false);
            var vp = child.AddComponent<VideoPlayer>();
            vp.playOnAwake = false;
            vp.waitForFirstFrame = true;
            vp.isLooping = true;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.targetTexture = rt;
            vp.source = VideoSource.Url;
            vp.audioOutputMode = VideoAudioOutputMode.None; // Unity audio is disabled; use FMOD
            // Stretch fills the RT completely without letterboxing. The TV mesh's visible
            // display area is UV-sampled from the full RT; if the mesh is close to the video's
            // aspect, the stretch is imperceptible. If the mesh is a very different aspect (e.g.
            // square), switch to FitInside for letterboxing instead of distortion.
            vp.aspectRatio = VideoAspectRatio.Stretch;
            var slot = new Slot
            {
                Label = label,
                Player = vp,
                RenderTexture = rt
            };
            vp.errorReceived += (VideoPlayer v, string msg) =>
                Plugin.Log?.LogError($"[{label}] {msg}");
            vp.prepareCompleted += (VideoPlayer v) => HandleSlotPrepared(slot);
            vp.started += (VideoPlayer v) => HandleSlotStarted(slot);

            return slot;
        }

        // Apply the canonical "which video is showing" index. Plays it on the center slot and
        // Prepares the adjacent ones.
        public void ApplyIndex(int index)
        {
            if (VideoLibrary.Entries.Count == 0) return;
            index = Wrap(index, VideoLibrary.Entries.Count);
            StopActiveChannel();

            // If any slot is already bound to this index, make it active. Otherwise commandeer
            // the current center slot for the new video and leave the others to Prepare around it.
            int slotWithIndex = FindSlotFor(index);
            if (slotWithIndex < 0)
            {
                slotWithIndex = _activeSlot;
                BindSlotToVideo(_slots[slotWithIndex], index);
            }
            _activeSlot = slotWithIndex;

            AssignRenderTargets(_activeSlot);

            // Play it. If already prepared (preloaded), playback starts quickly; otherwise Prepare
            // fires off async decode and Play picks up when it's ready.
            var slot = _slots[_activeSlot];
            if (slot.Player.url != BuildUrl(index))
            {
                BindSlotToVideo(slot, index);
            }
            ShowStatus("Loading...");
            slot.Player.Prepare();
            slot.Player.Play();
            Plugin.Log?.LogInfo($"Theater playing [{index}] {Path.GetFileName(VideoLibrary.Entries[index].VideoPath)} (slot {_activeSlot})");

            // Preload neighbors.
            PreloadNeighbors(index);
        }

        private void HandleSlotPrepared(Slot slot)
        {
            slot.Prepared = true;
            Plugin.Log?.LogInfo($"[{slot.Label}] prepared: {Path.GetFileName(slot.Player.url)} ({slot.Player.width}x{slot.Player.height}x{slot.Player.frameRate:F1}fps)");

            if (_slots != null && _slots[_activeSlot] == slot)
            {
                AssignRenderTargets(_activeSlot);
                CompositeToScreen(slot.RenderTexture);
            }
        }

        private void HandleSlotStarted(Slot slot)
        {
            Plugin.Log?.LogInfo($"[{slot.Label}] started: {Path.GetFileName(slot.Player.url)}");
            if (_slots == null || _slots[_activeSlot] != slot)
                return;

            AssignRenderTargets(_activeSlot);
            CompositeToScreen(slot.RenderTexture);
            HideStatus();
            if (slot.VideoIndex >= 0 && slot.VideoIndex != _activeAudioIndex)
                StartAudioFor(slot.VideoIndex);
        }

        private void PreloadNeighbors(int centerIndex)
        {
            int count = VideoLibrary.Entries.Count;
            if (count <= 1) return;
            int nextIdx = Wrap(centerIndex + 1, count);
            int prevIdx = Wrap(centerIndex - 1, count);

            // Find the two non-active slots and bind them to the neighbors (if not already).
            for (int i = 0; i < _slots.Length; i++)
            {
                if (i == _activeSlot) continue;
                int want = (nextIdx != centerIndex && FindSlotFor(nextIdx) < 0) ? nextIdx
                         : (prevIdx != centerIndex && FindSlotFor(prevIdx) < 0) ? prevIdx
                         : -1;
                if (want < 0) break;
                BindSlotToVideo(_slots[i], want);
                _slots[i].Player.Prepare(); // async; ready by next cycle
            }
        }

        private void BindSlotToVideo(Slot slot, int videoIndex)
        {
            if (slot.VideoIndex == videoIndex) return;
            slot.Player.Stop();
            slot.Player.url = VideoLibrary.Entries[videoIndex].VideoPath;
            slot.VideoIndex = videoIndex;
            slot.Prepared = false;
        }

        private int FindSlotFor(int videoIndex)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].VideoIndex == videoIndex) return i;
            return -1;
        }

        private void BindScreenTo(RenderTexture rt)
        {
            for (int i = 0; i < _screenTextureSlots.Length; i++)
                _screenMaterial.SetTexture(_screenTextureSlots[i], rt);
            _screenMaterial.mainTexture = rt;
        }

        private static void UpgradeVanillaRenderTextureInPlace(
            DrivingRangeStaticCameraManager vanillaMgr,
            RenderTexture sharedTarget,
            int desiredSize)
        {
            if (sharedTarget == null || desiredSize <= 0)
                return;

            if (sharedTarget.width == desiredSize && sharedTarget.height == desiredSize)
                return;

            try
            {
                Plugin.Log?.LogInfo(
                    $"Upgrading shared theater RT in place: '{sharedTarget.name}' {sharedTarget.width}x{sharedTarget.height} -> {desiredSize}x{desiredSize}");

                sharedTarget.Release();
                sharedTarget.width = desiredSize;
                sharedTarget.height = desiredSize;
                sharedTarget.Create();

                var camerasField = HarmonyLib.Traverse.Create(vanillaMgr).Field("cameras");
                var cams = camerasField.GetValue() as DrivingRangeStaticCamera[];
                if (cams == null)
                    return;

                for (int i = 0; i < cams.Length; i++)
                {
                    var cameraController = cams[i];
                    if (cameraController == null)
                        continue;

                    var trav = HarmonyLib.Traverse.Create(cameraController);
                    var cam = trav.Field("thisCamera").GetValue() as Camera;
                    if (cam == null)
                        continue;

                    // All six match-setup cameras share one display RT instance. After resizing
                    // that RT in place, rebuild each camera's cached static/depth textures and
                    // command buffer chain so they pick up the larger descriptor too.
                    cam.targetTexture = sharedTarget;
                    cam.RemoveAllCommandBuffers();

                    var oldStatic = trav.Field("staticRenderTexture").GetValue() as RenderTexture;
                    if (oldStatic != null)
                    {
                        oldStatic.Release();
                        Destroy(oldStatic);
                    }

                    var oldDepth = trav.Field("staticDepthTexture").GetValue() as RenderTexture;
                    if (oldDepth != null)
                    {
                        oldDepth.Release();
                        Destroy(oldDepth);
                    }

                    trav.Field("staticRenderTexture").SetValue(null);
                    trav.Field("staticDepthTexture").SetValue(null);
                    trav.Field("hasTextures").SetValue(false);

                    cameraController.RenderStaticTexture();
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"In-place theater RT upgrade failed: {ex.Message}");
            }
        }

        public void ApplyConfiguredScreenResolution()
        {
            if (_screenRenderTexture == null || _vanillaManager == null)
                return;

            UpgradeVanillaRenderTextureInPlace(_vanillaManager, _screenRenderTexture, Plugin.GetConfiguredScreenResolution());
            RefreshDisplayComposition();
        }

        private void AssignRenderTargets(int activeSlot)
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].Player.targetTexture != _slots[i].RenderTexture)
                    _slots[i].Player.targetTexture = _slots[i].RenderTexture;

            // Fallback for cases where the screen is not actually sampling the vanilla RT.
            if (_screenRenderTexture == null)
                BindScreenTo(_slots[activeSlot].RenderTexture);
            else
                BindScreenTo(_screenRenderTexture);
        }

        private void CompositeToScreen(RenderTexture source)
        {
            if (_screenRenderTexture == null || source == null) return;

            float overscan = Plugin.OverscanCompensationConfig != null
                ? Mathf.Clamp01(Plugin.OverscanCompensationConfig.Value)
                : 0.12f;

            float targetW = _screenRenderTexture.width;
            float targetH = _screenRenderTexture.height;
            float safeW = targetW * (1f - overscan * 2f);
            float safeH = targetH * (1f - overscan * 2f);
            float sourceAspect = source.height > 0 ? (float)source.width / source.height : 16f / 9f;
            float safeAspect = safeH > 0f ? safeW / safeH : sourceAspect;

            float drawW = safeW;
            float drawH = safeH;
            if (safeAspect > sourceAspect)
                drawW = drawH * sourceAspect;
            else
                drawH = drawW / sourceAspect;

            float drawX = (targetW - drawW) * 0.5f;
            float drawY = (targetH - drawH) * 0.5f;

            var prev = RenderTexture.active;
            RenderTexture.active = _screenRenderTexture;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, targetW, targetH, 0f);
            GL.Clear(true, true, Color.black);
            Graphics.DrawTexture(new Rect(drawX, drawY, drawW, drawH), source);
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        private string BuildUrl(int index) => VideoLibrary.Entries[index].VideoPath;

        // ---- audio ----

        private void StartAudioFor(int index)
        {
            var entry = VideoLibrary.Entries[index];
            if (string.IsNullOrEmpty(entry.AudioPath)) return;

            // Lazy-load the sound (CREATESTREAM so large files don't sit decoded in memory).
            if (_sounds[index].handle == System.IntPtr.Zero)
            {
                var r = RuntimeManager.CoreSystem.createSound(entry.AudioPath,
                    FMOD.MODE.CREATESTREAM | FMOD.MODE.LOOP_NORMAL | FMOD.MODE._3D | FMOD.MODE._3D_LINEARROLLOFF,
                    out _sounds[index]);
                if (r != FMOD.RESULT.OK)
                {
                    Plugin.Log?.LogError($"FMOD createSound failed for {entry.AudioPath}: {r}");
                    return;
                }
            }

            var sys = RuntimeManager.CoreSystem;
            var res = sys.playSound(_sounds[index], default(FMOD.ChannelGroup), false, out _activeChannel);
            if (res != FMOD.RESULT.OK)
            {
                Plugin.Log?.LogError($"FMOD playSound failed: {res}");
                return;
            }
            _activeChannel.setMode(FMOD.MODE._3D | FMOD.MODE._3D_LINEARROLLOFF);
            _activeChannel.set3DMinMaxDistance(AudioMinDistance, AudioMaxDistance);
            _activeChannel.setVolume(Plugin.VolumeConfig.Value);
            _activeChannelValid = true;
            _activeAudioIndex = index;
            _driftCheckTimer = DriftCheckInterval;
            UpdateAudioSpatialization(force: true);
        }

        private void StopActiveChannel()
        {
            if (!_activeChannelValid) return;
            _activeChannel.stop();
            _activeChannelValid = false;
            _activeAudioIndex = -1;
        }

        public void SetVolume(float v)
        {
            v = Mathf.Clamp01(v);
            if (_activeChannelValid) _activeChannel.setVolume(v);
        }

        public void RefreshDisplayComposition()
        {
            if (_slots == null || _activeSlot < 0 || _activeSlot >= _slots.Length) return;

            var player = _slots[_activeSlot].Player;
            if (_screenRenderTexture != null && player != null && (player.isPlaying || player.isPrepared))
                CompositeToScreen(_slots[_activeSlot].RenderTexture);
        }

        public void ShowNoVideos()
        {
            StopActiveChannel();
            ShowStatus("No video files added");
        }

        private void CreateStatusText()
        {
            if (_statusText != null || _screenRenderer == null) return;

            var go = new GameObject("SBG-TheaterStatus");
            go.transform.SetParent(transform, false);
            _statusText = go.AddComponent<TextMeshPro>();
            _statusText.text = string.Empty;
            _statusText.fontSize = 8f;
            _statusText.alignment = TextAlignmentOptions.Center;
            _statusText.color = Color.white;
            _statusText.textWrappingMode = TextWrappingModes.Normal;
            _statusText.outlineWidth = 0.2f;
            _statusText.rectTransform.sizeDelta = new Vector2(12f, 4f);
            _statusText.gameObject.SetActive(false);
            UpdateStatusTransform();
        }

        private void UpdateStatusTransform()
        {
            if (_statusText == null || _screenRenderer == null) return;

            var t = _statusText.transform;
            t.position = _screenRenderer.bounds.center + _screenRenderer.transform.forward * 0.03f;
            t.rotation = _screenRenderer.transform.rotation * Quaternion.Euler(0f, 180f, 0f);
            t.localScale = Vector3.one * 0.45f;
        }

        private void ShowStatus(string text)
        {
            if (_statusText == null) CreateStatusText();
            if (_statusText == null) return;

            UpdateStatusTransform();
            _statusText.text = text;
            _statusText.gameObject.SetActive(true);
        }

        private void HideStatus()
        {
            if (_statusText != null)
                _statusText.gameObject.SetActive(false);
        }

        private Vector3 GetAudioWorldPosition()
        {
            if (_screenRenderer != null)
                return _screenRenderer.bounds.center;
            return transform.position;
        }

        private void UpdateAudioSpatialization(bool force = false)
        {
            if (!_activeChannelValid) return;

            Vector3 worldPos = GetAudioWorldPosition();
            if (!force && (worldPos - _lastAudioWorldPos).sqrMagnitude < 0.0001f)
                return;

            var pos = worldPos.ToFMODVector();
            var vel = Vector3.zero.ToFMODVector();
            _activeChannel.set3DAttributes(ref pos, ref vel);
            _lastAudioWorldPos = worldPos;
        }

        private void Update()
        {
            var player = (_slots != null && _activeSlot >= 0 && _activeSlot < _slots.Length) ? _slots[_activeSlot].Player : null;
            if (_screenRenderTexture != null && player != null && (player.isPlaying || player.isPrepared))
                CompositeToScreen(_slots[_activeSlot].RenderTexture);
            if (_statusText != null && _statusText.gameObject.activeSelf)
                UpdateStatusTransform();

            if (!_activeChannelValid) return;

            UpdateAudioSpatialization();

            _driftCheckTimer -= Time.unscaledDeltaTime;
            if (_driftCheckTimer > 0f) return;
            _driftCheckTimer = DriftCheckInterval;

            if (player == null || !player.isPlaying) return;

            _activeChannel.getPosition(out uint audioMs, FMOD.TIMEUNIT.MS);
            double videoMs = player.time * 1000.0;
            double diff = videoMs - audioMs;
            if (System.Math.Abs(diff) > DriftToleranceMs)
            {
                // Keep audio authoritative if video is catching up after a seek; otherwise push
                // audio to the video clock. Video is the visible artifact — audio is what we sync.
                _activeChannel.setPosition((uint)videoMs, FMOD.TIMEUNIT.MS);
                Plugin.Log?.LogDebug($"Theater drift correction: {diff:F1}ms → setPosition({videoMs:F0})");
            }
        }

        private void OnDestroy()
        {
            if (Current == this) Current = null;
            StopActiveChannel();
            if (_screenMaterial != null)
            {
                for (int i = 0; i < _screenBindings.Length; i++)
                {
                    _screenMaterial.SetTexture(_screenBindings[i].Name, _screenBindings[i].Texture);
                    _screenMaterial.SetTextureScale(_screenBindings[i].Name, _screenBindings[i].Scale);
                    _screenMaterial.SetTextureOffset(_screenBindings[i].Name, _screenBindings[i].Offset);
                }
            }
            if (_slots != null)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    _slots[i].Player?.Stop();
                    if (_slots[i].RenderTexture != null)
                    {
                        _slots[i].RenderTexture.Release();
                        Destroy(_slots[i].RenderTexture);
                    }
                }
            }
            if (_sounds != null)
            {
                for (int i = 0; i < _sounds.Length; i++)
                    if (_sounds[i].handle != System.IntPtr.Zero) _sounds[i].release();
            }
        }

        private static int Wrap(int v, int n) => ((v % n) + n) % n;
    }
}
