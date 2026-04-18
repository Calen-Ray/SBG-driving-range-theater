using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DrivingRangeTheater
{
    // Minimal self-contained overlay Canvas carrying a single slider. Appears whenever the
    // pause menu is open AND the driving range theater is active. Writes back to the BepInEx
    // ConfigEntry so the setting persists across runs; UpdateVolume on change routes through
    // TheaterController so playback volume responds immediately.
    internal class VolumeSliderUi : MonoBehaviour
    {
        public static VolumeSliderUi Instance;

        private Canvas _canvas;
        private Slider _slider;

        public static void EnsureExists()
        {
            if (Instance != null) return;
            var go = new GameObject("SBG-TheaterVolumeSlider");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<VolumeSliderUi>();
        }

        private void Awake()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 900; // below the pause menu panel so it composes cleanly
            gameObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            gameObject.AddComponent<GraphicRaycaster>();

            // Background panel
            var panel = new GameObject("Panel");
            panel.transform.SetParent(transform, false);
            var panelRT = panel.AddComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0f);
            panelRT.anchorMax = new Vector2(0.5f, 0f);
            panelRT.pivot     = new Vector2(0.5f, 0f);
            panelRT.anchoredPosition = new Vector2(0f, 80f);
            panelRT.sizeDelta = new Vector2(420f, 48f);
            var panelImg = panel.AddComponent<Image>();
            panelImg.color = new Color(0f, 0f, 0f, 0.65f);

            // Label
            var label = new GameObject("Label");
            label.transform.SetParent(panel.transform, false);
            var labelRT = label.AddComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0f, 0f);
            labelRT.anchorMax = new Vector2(0f, 1f);
            labelRT.pivot     = new Vector2(0f, 0.5f);
            labelRT.anchoredPosition = new Vector2(14f, 0f);
            labelRT.sizeDelta = new Vector2(120f, 0f);
            var labelText = label.AddComponent<Text>();
            labelText.text = "Theater volume";
            labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            labelText.fontSize = 16;
            labelText.alignment = TextAnchor.MiddleLeft;
            labelText.color = Color.white;

            // Slider
            var sliderGo = new GameObject("Slider");
            sliderGo.transform.SetParent(panel.transform, false);
            var sRT = sliderGo.AddComponent<RectTransform>();
            sRT.anchorMin = new Vector2(0f, 0.5f);
            sRT.anchorMax = new Vector2(1f, 0.5f);
            sRT.pivot     = new Vector2(0f, 0.5f);
            sRT.anchoredPosition = new Vector2(140f, 0f);
            sRT.sizeDelta = new Vector2(-160f, 16f);
            _slider = sliderGo.AddComponent<Slider>();
            _slider.minValue = 0f;
            _slider.maxValue = 1f;
            _slider.value    = Plugin.VolumeConfig.Value;

            // Slider background
            var bg = new GameObject("Bg");
            bg.transform.SetParent(sliderGo.transform, false);
            var bgRT = bg.AddComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0f, 0.25f);
            bgRT.anchorMax = new Vector2(1f, 0.75f);
            bgRT.sizeDelta = Vector2.zero;
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(1f, 1f, 1f, 0.2f);

            // Fill area + fill
            var fillArea = new GameObject("Fill Area");
            fillArea.transform.SetParent(sliderGo.transform, false);
            var fillAreaRT = fillArea.AddComponent<RectTransform>();
            fillAreaRT.anchorMin = new Vector2(0f, 0.25f);
            fillAreaRT.anchorMax = new Vector2(1f, 0.75f);
            fillAreaRT.sizeDelta = new Vector2(-20f, 0f);
            fillAreaRT.anchoredPosition = new Vector2(-5f, 0f);

            var fill = new GameObject("Fill");
            fill.transform.SetParent(fillArea.transform, false);
            var fillRT = fill.AddComponent<RectTransform>();
            fillRT.sizeDelta = Vector2.zero;
            var fillImg = fill.AddComponent<Image>();
            fillImg.color = new Color(0.4f, 0.85f, 1f, 1f);

            // Handle area + handle
            var handleArea = new GameObject("Handle Slide Area");
            handleArea.transform.SetParent(sliderGo.transform, false);
            var handleAreaRT = handleArea.AddComponent<RectTransform>();
            handleAreaRT.anchorMin = new Vector2(0f, 0f);
            handleAreaRT.anchorMax = new Vector2(1f, 1f);
            handleAreaRT.sizeDelta = new Vector2(-20f, 0f);
            handleAreaRT.anchoredPosition = new Vector2(-5f, 0f);

            var handle = new GameObject("Handle");
            handle.transform.SetParent(handleArea.transform, false);
            var handleRT = handle.AddComponent<RectTransform>();
            handleRT.sizeDelta = new Vector2(20f, 20f);
            var handleImg = handle.AddComponent<Image>();
            handleImg.color = Color.white;

            _slider.targetGraphic = handleImg;
            _slider.fillRect      = fillRT;
            _slider.handleRect    = handleRT;
            _slider.direction     = Slider.Direction.LeftToRight;

            _slider.onValueChanged.AddListener(v =>
            {
                Plugin.VolumeConfig.Value = v;
                TheaterController.Current?.SetVolume(v);
            });

            SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            if (_canvas != null) _canvas.enabled = visible;
        }

        // Show only when the pause menu is open AND we're in the theater's scene.
        [HarmonyPatch(typeof(PauseMenu), "OnEnable")]
        internal static class Patch_PauseMenu_OnEnable
        {
            private static void Postfix()
            {
                EnsureExists();
                bool isDrivingRange = TheaterController.Current != null;
                Instance?.SetVisible(isDrivingRange);
            }
        }

        [HarmonyPatch(typeof(PauseMenu), "OnDisable")]
        internal static class Patch_PauseMenu_OnDisable
        {
            private static void Postfix()
            {
                Instance?.SetVisible(false);
            }
        }
    }
}
