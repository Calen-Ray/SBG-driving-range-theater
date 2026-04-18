using System.IO;
using FMOD;
using FMODUnity;
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

        private sealed class Slot
        {
            public VideoPlayer Player;
            public RenderTexture RenderTexture;
            public int VideoIndex = -1;  // which entry this slot is currently bound to
            public bool Prepared;
        }

        private Slot[] _slots;
        private int _activeSlot;
        private Renderer _screenRenderer;
        private Material _screenMaterial;

        // FMOD audio — one loaded Sound per video entry, created lazily on first prepare.
        // The active Channel is replaced each cycle.
        private FMOD.Sound[] _sounds;
        private FMOD.Channel _activeChannel;
        private bool _activeChannelValid;
        private float _driftCheckTimer;
        private const float DriftCheckInterval = 2.0f;   // seconds
        private const int DriftToleranceMs = 250;

        public void Initialize(Renderer screenRenderer, DrivingRangeStaticCameraManager vanillaMgr)
        {
            Current = this;
            _screenRenderer = screenRenderer;
            _screenMaterial = screenRenderer.material;

            // Grab the dimensions the screen mesh was tuned to from a vanilla camera's target
            // texture. Using a mismatched RT size causes visible top/bottom (or side) crop
            // because the UV mapping on the TV bezel is baked to these exact dimensions.
            int rtW = 1920, rtH = 1080;
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
                        rtW = cam.targetTexture.width;
                        rtH = cam.targetTexture.height;
                        Plugin.Log?.LogInfo($"Theater RT sized from vanilla camera: {rtW}x{rtH}");
                    }
                }
            }
            catch (System.Exception ex) { Plugin.Log?.LogWarning($"RT size probe failed: {ex.Message}"); }

            // Three slots — prev / current / next. We don't need more because cycling only
            // moves one step at a time.
            _slots = new Slot[3];
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = CreateSlot("SBG-TheaterSlot-" + i, rtW, rtH);

            // Identify the shader texture slot to bind on each cycle.
            var shader = _screenMaterial.shader;
            int propCount = shader.GetPropertyCount();
            var swapped = new System.Collections.Generic.List<string>();
            for (int i = 0; i < propCount; i++)
            {
                if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                {
                    swapped.Add(shader.GetPropertyName(i));
                }
            }
            _screenTextureSlots = swapped.ToArray();
            if (_screenMaterial.HasProperty("_EmissionColor"))
                _screenMaterial.SetColor("_EmissionColor", Color.white * 1.2f);
            _screenMaterial.EnableKeyword("_EMISSION");
            Plugin.Log?.LogInfo($"Screen shader '{shader.name}' — {swapped.Count} texture slot(s): {string.Join(", ", swapped)}");

            // Diagnostics: dump mesh UV bounds + texture-scale/offset so we can see what portion
            // of the RT the screen actually samples. If the mesh's UVs only span a slice of the
            // 0-1 range, the RT content outside that slice never makes it to the screen.
            try
            {
                Mesh probeMesh = null;
                string rendererKind = screenRenderer.GetType().Name;
                var mf = screenRenderer.GetComponent<MeshFilter>();
                if (mf != null) probeMesh = mf.sharedMesh;
                if (probeMesh == null)
                {
                    var smr = screenRenderer as UnityEngine.SkinnedMeshRenderer;
                    if (smr != null) probeMesh = smr.sharedMesh;
                }
                Plugin.Log?.LogInfo($"Screen renderer kind: {rendererKind}, mesh: {(probeMesh != null ? probeMesh.name : "<null>")}");

                if (probeMesh != null)
                {
                    var uvs = probeMesh.uv;
                    if (uvs != null && uvs.Length > 0)
                    {
                        float uMin = float.PositiveInfinity, uMax = float.NegativeInfinity;
                        float vMin = float.PositiveInfinity, vMax = float.NegativeInfinity;
                        for (int i = 0; i < uvs.Length; i++)
                        {
                            if (uvs[i].x < uMin) uMin = uvs[i].x;
                            if (uvs[i].x > uMax) uMax = uvs[i].x;
                            if (uvs[i].y < vMin) vMin = uvs[i].y;
                            if (uvs[i].y > vMax) vMax = uvs[i].y;
                        }
                        Plugin.Log?.LogInfo($"Screen mesh UV range: u=[{uMin:F3}..{uMax:F3}], v=[{vMin:F3}..{vMax:F3}] (verts={uvs.Length})");
                    }
                    else Plugin.Log?.LogWarning("Screen mesh has no UVs.");
                }

                var b = screenRenderer.bounds;
                Plugin.Log?.LogInfo($"Screen renderer world bounds size: {b.size.x:F2} x {b.size.y:F2} x {b.size.z:F2} (aspect x/y={b.size.x/Mathf.Max(0.0001f, b.size.y):F3})");
                foreach (var slot in _screenTextureSlots)
                {
                    var scl = _screenMaterial.GetTextureScale(slot);
                    var off = _screenMaterial.GetTextureOffset(slot);
                    Plugin.Log?.LogInfo($"  mat scale/offset for '{slot}': scale=({scl.x:F3},{scl.y:F3}) offset=({off.x:F3},{off.y:F3})");
                }
            }
            catch (System.Exception ex) { Plugin.Log?.LogWarning($"UV probe failed: {ex.Message}"); }

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
            vp.errorReceived += (VideoPlayer v, string msg) =>
                Plugin.Log?.LogError($"[{label}] {msg}");
            vp.prepareCompleted += (VideoPlayer v) =>
                Plugin.Log?.LogInfo($"[{label}] prepared: {Path.GetFileName(v.url)} ({v.width}x{v.height}, {v.frameRate:F1}fps)");
            vp.started += (VideoPlayer v) =>
                Plugin.Log?.LogInfo($"[{label}] started: {Path.GetFileName(v.url)}");

            return new Slot { Player = vp, RenderTexture = rt };
        }

        // Apply the canonical "which video is showing" index. Plays it on the center slot and
        // Prepares the adjacent ones.
        public void ApplyIndex(int index)
        {
            if (VideoLibrary.Entries.Count == 0) return;
            index = Wrap(index, VideoLibrary.Entries.Count);

            // If any slot is already bound to this index, make it active. Otherwise commandeer
            // the current center slot for the new video and leave the others to Prepare around it.
            int slotWithIndex = FindSlotFor(index);
            if (slotWithIndex < 0)
            {
                slotWithIndex = _activeSlot;
                BindSlotToVideo(_slots[slotWithIndex], index);
            }
            _activeSlot = slotWithIndex;

            // Show this slot's RT on the screen.
            BindScreenTo(_slots[_activeSlot].RenderTexture);

            // Play it. If already prepared (preloaded), playback starts quickly; otherwise Prepare
            // fires off async decode and Play picks up when it's ready.
            var slot = _slots[_activeSlot];
            if (slot.Player.url != BuildUrl(index))
            {
                BindSlotToVideo(slot, index);
            }
            slot.Player.Prepare();
            slot.Player.Play();
            Plugin.Log?.LogInfo($"Theater playing [{index}] {Path.GetFileName(VideoLibrary.Entries[index].VideoPath)} (slot {_activeSlot})");

            StartAudioFor(index);

            // Preload neighbors.
            PreloadNeighbors(index);
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

        private string BuildUrl(int index) => VideoLibrary.Entries[index].VideoPath;

        // ---- audio ----

        private void StartAudioFor(int index)
        {
            StopActiveChannel();

            var entry = VideoLibrary.Entries[index];
            if (string.IsNullOrEmpty(entry.AudioPath)) return;

            // Lazy-load the sound (CREATESTREAM so large files don't sit decoded in memory).
            if (_sounds[index].handle == System.IntPtr.Zero)
            {
                var r = RuntimeManager.CoreSystem.createSound(entry.AudioPath,
                    FMOD.MODE.CREATESTREAM | FMOD.MODE.LOOP_NORMAL, out _sounds[index]);
                if (r != FMOD.RESULT.OK)
                {
                    Plugin.Log?.LogError($"FMOD createSound failed for {entry.AudioPath}: {r}");
                    return;
                }
            }

            var sys = RuntimeManager.CoreSystem;
            sys.getMasterChannelGroup(out FMOD.ChannelGroup group);
            var res = sys.playSound(_sounds[index], group, false, out _activeChannel);
            if (res != FMOD.RESULT.OK)
            {
                Plugin.Log?.LogError($"FMOD playSound failed: {res}");
                return;
            }
            _activeChannel.setVolume(Plugin.VolumeConfig.Value);
            _activeChannelValid = true;
            _driftCheckTimer = DriftCheckInterval;
        }

        private void StopActiveChannel()
        {
            if (!_activeChannelValid) return;
            _activeChannel.stop();
            _activeChannelValid = false;
        }

        public void SetVolume(float v)
        {
            v = Mathf.Clamp01(v);
            if (_activeChannelValid) _activeChannel.setVolume(v);
        }

        private void Update()
        {
            if (!_activeChannelValid) return;

            _driftCheckTimer -= Time.unscaledDeltaTime;
            if (_driftCheckTimer > 0f) return;
            _driftCheckTimer = DriftCheckInterval;

            var player = _slots[_activeSlot].Player;
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
