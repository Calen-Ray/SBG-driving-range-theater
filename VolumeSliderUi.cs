using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DrivingRangeTheater
{
    // Small pause-menu child panel carrying a single slider. We attach it under the menu's
    // existing hierarchy so it appears exactly when the real pause UI does and uses the same
    // scaling / input system as the rest of the menu.
    internal class VolumeSliderUi : MonoBehaviour
    {
        public static VolumeSliderUi Instance;

        private Slider _volumeSlider;
        private Slider _overscanSlider;

        public static void EnsureExists(PauseMenu pauseMenu)
        {
            if (Instance != null) return;
            if (pauseMenu == null || pauseMenu.menuContainer == null) return;

            var go = new GameObject("SBG-TheaterVolumeSlider", typeof(RectTransform));
            go.transform.SetParent(pauseMenu.menuContainer.transform, false);
            Instance = go.AddComponent<VolumeSliderUi>();
        }

        public static void HandlePauseShown()
        {
            EnsureExists(PauseMenu.Instance);
            if (Instance == null) return;

            Instance.SyncFromConfig();
            Instance.SetVisible(TheaterController.Current != null);
        }

        public static void HandlePauseHidden()
        {
            Instance?.SetVisible(false);
        }

        private void Awake()
        {
            var rootRT = (RectTransform)transform;
            rootRT.anchorMin = new Vector2(0.5f, 0f);
            rootRT.anchorMax = new Vector2(0.5f, 0f);
            rootRT.pivot = new Vector2(0.5f, 0f);
            rootRT.anchoredPosition = new Vector2(0f, 64f);
            rootRT.sizeDelta = new Vector2(420f, 96f);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(transform, false);
            var panelRT = panel.GetComponent<RectTransform>();
            panelRT.anchorMin = Vector2.zero;
            panelRT.anchorMax = Vector2.one;
            panelRT.offsetMin = Vector2.zero;
            panelRT.offsetMax = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.65f);

            _volumeSlider = CreateSliderRow(panel.transform, "Theater volume", 24f, 0f, 1f, Plugin.VolumeConfig.Value, v =>
            {
                Plugin.VolumeConfig.Value = v;
                TheaterController.Current?.SetVolume(v);
            });

            _overscanSlider = CreateSliderRow(panel.transform, "Screen fit", -24f, 0f, 0.25f, Plugin.OverscanCompensationConfig.Value, v =>
            {
                Plugin.OverscanCompensationConfig.Value = v;
                TheaterController.Current?.RefreshDisplayComposition();
            });

            SetVisible(false);
        }

        private static Slider CreateSliderRow(Transform parent, string labelTextValue, float y, float min, float max, float value, UnityEngine.Events.UnityAction<float> onChanged)
        {
            var label = new GameObject(labelTextValue + " Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            label.transform.SetParent(parent, false);
            var labelRT = label.GetComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0f, 0.5f);
            labelRT.anchorMax = new Vector2(0f, 0.5f);
            labelRT.pivot = new Vector2(0f, 0.5f);
            labelRT.anchoredPosition = new Vector2(14f, y);
            labelRT.sizeDelta = new Vector2(120f, 24f);
            var text = label.GetComponent<TextMeshProUGUI>();
            text.text = labelTextValue;
            text.fontSize = 16;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.color = Color.white;

            var sliderGo = new GameObject(labelTextValue + " Slider", typeof(RectTransform), typeof(Slider));
            sliderGo.transform.SetParent(parent, false);
            var sliderRT = sliderGo.GetComponent<RectTransform>();
            sliderRT.anchorMin = new Vector2(0f, 0.5f);
            sliderRT.anchorMax = new Vector2(1f, 0.5f);
            sliderRT.pivot = new Vector2(0f, 0.5f);
            sliderRT.anchoredPosition = new Vector2(140f, y);
            sliderRT.sizeDelta = new Vector2(-160f, 16f);
            var slider = sliderGo.GetComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = value;

            var bg = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(sliderGo.transform, false);
            var bgRT = bg.GetComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0f, 0.25f);
            bgRT.anchorMax = new Vector2(1f, 0.75f);
            bgRT.sizeDelta = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.2f);

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(sliderGo.transform, false);
            var fillAreaRT = fillArea.GetComponent<RectTransform>();
            fillAreaRT.anchorMin = new Vector2(0f, 0.25f);
            fillAreaRT.anchorMax = new Vector2(1f, 0.75f);
            fillAreaRT.sizeDelta = new Vector2(-20f, 0f);
            fillAreaRT.anchoredPosition = new Vector2(-5f, 0f);

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            var fillRT = fill.GetComponent<RectTransform>();
            fillRT.sizeDelta = Vector2.zero;
            fill.GetComponent<Image>().color = new Color(0.4f, 0.85f, 1f, 1f);

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(sliderGo.transform, false);
            var handleAreaRT = handleArea.GetComponent<RectTransform>();
            handleAreaRT.anchorMin = new Vector2(0f, 0f);
            handleAreaRT.anchorMax = new Vector2(1f, 1f);
            handleAreaRT.sizeDelta = new Vector2(-20f, 0f);
            handleAreaRT.anchoredPosition = new Vector2(-5f, 0f);

            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(handleArea.transform, false);
            var handleRT = handle.GetComponent<RectTransform>();
            handleRT.sizeDelta = new Vector2(20f, 20f);
            var handleImg = handle.GetComponent<Image>();
            handleImg.color = Color.white;

            slider.targetGraphic = handleImg;
            slider.fillRect = fillRT;
            slider.handleRect = handleRT;
            slider.direction = Slider.Direction.LeftToRight;
            slider.onValueChanged.AddListener(onChanged);
            return slider;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void SyncFromConfig()
        {
            _volumeSlider?.SetValueWithoutNotify(Plugin.VolumeConfig.Value);
            _overscanSlider?.SetValueWithoutNotify(Plugin.OverscanCompensationConfig.Value);
        }

        public void SetVisible(bool visible)
        {
            gameObject.SetActive(visible);
        }
    }
}
