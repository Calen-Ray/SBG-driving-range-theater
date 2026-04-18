using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.Localization;

namespace DrivingRangeTheater
{
    // A custom IInteractable that sends TheaterCycleMsg{direction=-1}. Attached to a runtime
    // clone of the existing next-camera button so the scene visual + prompt text are reused.
    public class DrivingRangeBackCameraButton : MonoBehaviour, IInteractable
    {
        public Entity AsEntity { get; private set; }

        // Gated on the same SyncVar as the next button — when vanilla throttles one, we throttle
        // both, which feels consistent to the player.
        public bool IsInteractionEnabled => DrivingRangeStaticCameraManager.IsCycleNextButtonEnabled;

        // Reuse the "Next" localized prompt — there isn't a vanilla "Previous" string and shipping
        // a custom LocalizedString needs asset-bundle glue. Players recognize the button by position.
        public LocalizedString InteractString => Localization.UI.SPECTATOR_Prompt_Next_Ref;

        public void LocalPlayerInteract()
        {
            if (!NetworkClient.active) return;
            NetworkClient.Send(new TheaterCycleMsg { direction = -1 });
        }

        private void Awake()
        {
            AsEntity = GetComponent<Entity>();
        }
    }

    // Installed once the theater's first ApplyCurrentCameraIndex runs (driving range is fully
    // loaded and the serialized nextCameraButton reference is populated). Memoized so repeated
    // ApplyCurrentCameraIndex calls don't re-clone.
    internal static class BackButtonInstaller
    {
        private const float OffsetMeters = -0.6f; // nudge left of the original along its right-axis
        private static bool _installed;

        public static void ResetForNewScene() => _installed = false;

        public static void TryInstall(DrivingRangeStaticCameraManager mgr)
        {
            if (_installed) return;

            // First try the serialized reference on the manager.
            var nextButton = Traverse.Create(mgr)
                .Field<DrivingRangeNextCameraButton>("nextCameraButton").Value;
            // Fallback: scan the scene — works if Mirror spawned the singleton in a weird state.
            if (nextButton == null)
                nextButton = Object.FindObjectOfType<DrivingRangeNextCameraButton>();
            if (nextButton == null)
            {
                Plugin.Log?.LogWarning("Back button: no DrivingRangeNextCameraButton found yet — will retry.");
                return;
            }

            if (nextButton.transform.parent != null &&
                nextButton.transform.parent.Find("SBG-BackCameraButton") != null)
            {
                _installed = true;
                return;
            }

            var clone = Object.Instantiate(nextButton.gameObject, nextButton.transform.parent);
            clone.name = "SBG-BackCameraButton";
            clone.transform.localPosition = nextButton.transform.localPosition +
                                            nextButton.transform.right * OffsetMeters;
            clone.transform.localRotation = nextButton.transform.localRotation;
            clone.transform.localScale    = nextButton.transform.localScale;

            var vanilla = clone.GetComponent<DrivingRangeNextCameraButton>();
            if (vanilla != null) Object.Destroy(vanilla);
            clone.AddComponent<DrivingRangeBackCameraButton>();

            _installed = true;
            Plugin.Log?.LogInfo($"Back button installed at {clone.transform.position}.");
        }
    }
}
