using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace DrivingRangeTheater
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid    = "cray.drivingrangetheater";
        public const string ModName    = "DrivingRangeTheater";
        public const string ModVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<float> VolumeConfig;
        internal static ConfigEntry<float> OverscanCompensationConfig;

        // Simple rate limit: one cycle per 200 ms. Vanilla uses cycleButtonCooldown (~0.3 s) —
        // we match the spirit without needing to mirror the exact value.
        private static float _lastServerCycleTime;

        private void Awake()
        {
            Log = Logger;

            VolumeConfig = Config.Bind("Theater", "Volume", 0.5f,
                new ConfigDescription(
                    "Client-local master volume for the driving-range theater (0.0 - 1.0).",
                    new AcceptableValueRange<float>(0f, 1f)));
            OverscanCompensationConfig = Config.Bind("Theater", "OverscanCompensation", 0.00f,
                new ConfigDescription(
                    "Shrinks the displayed video inside the theater screen RT to compensate for screen overscan/cropping (0.0 - 0.25).",
                    new AcceptableValueRange<float>(0f, 0.25f)));
            VolumeConfig.SettingChanged += (_, __) =>
                TheaterController.Current?.SetVolume(VolumeConfig.Value);
            OverscanCompensationConfig.SettingChanged += (_, __) =>
                TheaterController.Current?.RefreshDisplayComposition();
            PauseMenu.Paused += VolumeSliderUi.HandlePauseShown;
            PauseMenu.Unpaused += VolumeSliderUi.HandlePauseHidden;

            VideoLibrary.ScanAll(Paths.PluginPath, Log);

            new Harmony(ModGuid).PatchAll();

            // Handler registration has to happen whenever Mirror's client/server starts, because
            // BNetworkManager clears handlers on shutdown. We hook both events — idempotent
            // Register calls overwrite each other safely.
            NetworkClient.OnConnectedEvent += RegisterClientHandlers;

            Log.LogInfo($"{ModName} v{ModVersion} loaded ({VideoLibrary.Videos.Count} video(s)).");
        }

        private static void RegisterClientHandlers()
        {
            // No client→server handler registrations needed (client just sends, doesn't receive
            // our custom message type — index syncs via the vanilla SyncVar). Kept as a hook
            // for future expansion.
        }

        // Server-side handler: clamped cycle of the vanilla currentCameraIndex SyncVar.
        // Clients' SyncVar hook (patched below) picks it up and plays the matching video.
        internal static void OnClientCycle(NetworkConnectionToClient conn, TheaterCycleMsg msg)
        {
            if (Time.realtimeSinceStartup - _lastServerCycleTime < 0.2f) return;
            _lastServerCycleTime = Time.realtimeSinceStartup;

            var mgr = SingletonNetworkBehaviour<DrivingRangeStaticCameraManager>.Instance;
            if (mgr == null) return;

            // Video count is canonical (NOT cameras.Length). We still reuse the vanilla SyncVar
            // so Mirror replication + SyncVar hook invocation on every client is free.
            int count = Mathf.Max(VideoLibrary.Videos.Count, 1);
            int dir = msg.direction >= 0 ? 1 : -1;
            int current = mgr.NetworkcurrentCameraIndex;
            if (current < 0) current = 0;
            int next = ((current + dir) % count + count) % count;

            mgr.NetworkcurrentCameraIndex = next;
        }

        // Register the server handler once per server start. We patch the manager's OnStartServer
        // because that's guaranteed to fire when a host creates a lobby on the driving range.
        [HarmonyPatch(typeof(DrivingRangeStaticCameraManager), nameof(DrivingRangeStaticCameraManager.OnStartServer))]
        internal static class Patch_Manager_OnStartServer
        {
            private static void Postfix()
            {
                NetworkServer.ReplaceHandler<TheaterCycleMsg>(OnClientCycle);
                Log.LogInfo("Registered TheaterCycleMsg server handler.");
            }
        }

        // Replace vanilla camera activation with video playback. Vanilla cameras stay turned off
        // entirely — we handle the screen texture ourselves.
        [HarmonyPatch(typeof(DrivingRangeStaticCameraManager), "ApplyCurrentCameraIndex")]
        internal static class Patch_Manager_ApplyCurrentCameraIndex
        {
            private static bool Prefix(DrivingRangeStaticCameraManager __instance)
            {
                var screenRenderer = Traverse.Create(__instance).Field<Renderer>("screenRenderer").Value;
                if (screenRenderer == null) return true;

                // Lazy-create one TheaterController per scene, attached to the screen GameObject.
                if (TheaterController.Current == null)
                {
                    var controller = screenRenderer.gameObject.AddComponent<TheaterController>();
                    controller.Initialize(screenRenderer, __instance);
                }

                if (VideoLibrary.Videos.Count == 0)
                {
                    TheaterController.Current.ShowNoVideos();
                    return false;
                }

                TheaterController.Current.ApplyIndex(__instance.NetworkcurrentCameraIndex);
                BackButtonInstaller.TryInstall(__instance);
                return false; // skip original — don't activate vanilla cameras
            }
        }
    }
}
