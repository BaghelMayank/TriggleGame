using System;
using Triggle.Core;
using Triggle.Gameplay;
using Triggle.Visuals;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Triggle.UI
{
    /// <summary>
    /// Settings panel: Audio and Board tabs, wired to <see cref="TrigglePrefs"/>.
    /// </summary>
    /// <remarks>
    /// Board theme and board size are locked once a match is running. Size genuinely cannot change
    /// mid-match - a different radius means regenerating the lattice, which would invalidate every
    /// placed band - and theme is locked alongside it so the two controls behave consistently rather
    /// than one working and its neighbour silently not.
    /// <para>
    /// Colourblind Mode and a Particle Effects toggle were in the reference design but are deliberately
    /// omitted per the brief.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class SettingsPanelController : MonoBehaviour
    {
        /// <summary>One entry in the board theme picker.</summary>
        [Serializable]
        public sealed class ThemeChip
        {
            public GameObject root;
            public Button button;

            [Tooltip("Swatch tinted to the theme's board colour.")]
            public Image swatch;

            [Tooltip("Accent stripe tinted to the theme's rim colour.")]
            public Image accent;

            [Tooltip("Selection ring, shown only on the active theme.")]
            public GameObject selectionMarker;

            public TMP_Text label;
        }

        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;
        [SerializeField] private MatchController matchController;
        [SerializeField] private BoardThemeLibrary themeLibrary;

        [Header("Panel")]
        [SerializeField] private CanvasGroup panel;
        [SerializeField] private Button closeButton;

        [Header("Tabs")]
        [SerializeField] private Button audioTabButton;
        [SerializeField] private Button boardTabButton;
        [SerializeField] private GameObject audioTabContent;
        [SerializeField] private GameObject boardTabContent;
        [SerializeField] private Image audioTabUnderline;
        [SerializeField] private Image boardTabUnderline;
        [SerializeField] private TMP_Text audioTabLabel;
        [SerializeField] private TMP_Text boardTabLabel;

        [Header("Audio")]
        [SerializeField] private Slider masterSlider;
        [SerializeField] private Slider musicSlider;
        [SerializeField] private Slider sfxSlider;
        [SerializeField] private TMP_Text masterValueLabel;
        [SerializeField] private TMP_Text musicValueLabel;
        [SerializeField] private TMP_Text sfxValueLabel;

        [Header("Board Theme")]
        [SerializeField] private ThemeChip[] themeChips = new ThemeChip[6];

        [Header("Board Size")]
        [SerializeField] private Button sizeDownButton;
        [SerializeField] private Button sizeUpButton;
        [SerializeField] private TMP_Text sizeValueLabel;
        [SerializeField] private TMP_Text sizeCaptionLabel;

        [Header("Pre-Game Lock")]
        [Tooltip("Shown over the board controls while a match is running.")]
        [SerializeField] private GameObject lockedNotice;

        [SerializeField] private TMP_Text lockedNoticeLabel;

        [Header("Appearance")]
        [SerializeField] private Color activeTabColor = new Color(0.20f, 0.95f, 0.90f);
        [SerializeField] private Color inactiveTabColor = new Color(0.42f, 0.46f, 0.55f);
        [SerializeField] private Color lockedControlColor = new Color(1f, 1f, 1f, 0.35f);

        private bool _suppressCallbacks;

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
            if (matchController == null) matchController = FindObjectOfType<MatchController>();
            if (themeLibrary == null) themeLibrary = FindObjectOfType<BoardThemeLibrary>();

            if (closeButton != null) closeButton.onClick.AddListener(Close);
            if (audioTabButton != null) audioTabButton.onClick.AddListener(() => ShowTab(true));
            if (boardTabButton != null) boardTabButton.onClick.AddListener(() => ShowTab(false));

            if (masterSlider != null) masterSlider.onValueChanged.AddListener(HandleMasterChanged);
            if (musicSlider != null) musicSlider.onValueChanged.AddListener(HandleMusicChanged);
            if (sfxSlider != null) sfxSlider.onValueChanged.AddListener(HandleSfxChanged);

            if (sizeDownButton != null) sizeDownButton.onClick.AddListener(() => StepBoardSize(-1));
            if (sizeUpButton != null) sizeUpButton.onClick.AddListener(() => StepBoardSize(+1));

            for (int i = 0; i < themeChips.Length; i++)
            {
                int slot = i;
                if (themeChips[i]?.button != null)
                    themeChips[i].button.onClick.AddListener(() => ChooseTheme(slot));
            }

            UITween.SetHidden(panel);
        }

        private void OnDestroy()
        {
            if (closeButton != null) closeButton.onClick.RemoveAllListeners();
            if (audioTabButton != null) audioTabButton.onClick.RemoveAllListeners();
            if (boardTabButton != null) boardTabButton.onClick.RemoveAllListeners();

            if (masterSlider != null) masterSlider.onValueChanged.RemoveListener(HandleMasterChanged);
            if (musicSlider != null) musicSlider.onValueChanged.RemoveListener(HandleMusicChanged);
            if (sfxSlider != null) sfxSlider.onValueChanged.RemoveListener(HandleSfxChanged);

            if (sizeDownButton != null) sizeDownButton.onClick.RemoveAllListeners();
            if (sizeUpButton != null) sizeUpButton.onClick.RemoveAllListeners();

            for (int i = 0; i < themeChips.Length; i++)
                if (themeChips[i]?.button != null) themeChips[i].button.onClick.RemoveAllListeners();
        }

        /// <summary>True while a match is in progress, which locks the board controls.</summary>
        private bool BoardLocked =>
            matchController != null && matchController.IsMatchRunning;

        // ------------------------------------------------------------------ open / close

        /// <summary>Opens the panel and refreshes every control from stored preferences.</summary>
        public void Open()
        {
            TrigglePrefs.Load();
            RefreshAll();
            ShowTab(true);

            if (isActiveAndEnabled) StartCoroutine(UITween.FadeIn(panel, 0.24f));
            else UITween.SetVisible(panel);
        }

        /// <summary>Closes the panel.</summary>
        public void Close()
        {
            if (isActiveAndEnabled) StartCoroutine(UITween.FadeOut(panel, 0.2f));
            else UITween.SetHidden(panel);
        }

        public bool IsOpen => panel != null && panel.gameObject.activeSelf;

        private void ShowTab(bool audio)
        {
            if (audioTabContent != null) audioTabContent.SetActive(audio);
            if (boardTabContent != null) boardTabContent.SetActive(!audio);

            if (audioTabUnderline != null) audioTabUnderline.color = audio ? activeTabColor : Color.clear;
            if (boardTabUnderline != null) boardTabUnderline.color = audio ? Color.clear : activeTabColor;
            if (audioTabLabel != null) audioTabLabel.color = audio ? activeTabColor : inactiveTabColor;
            if (boardTabLabel != null) boardTabLabel.color = audio ? inactiveTabColor : activeTabColor;
        }

        // ------------------------------------------------------------------ refresh

        private void RefreshAll()
        {
            // Sliders raise onValueChanged when set from code, which would write straight back to prefs.
            _suppressCallbacks = true;

            if (masterSlider != null) masterSlider.SetValueWithoutNotify(TrigglePrefs.MasterVolume);
            if (musicSlider != null) musicSlider.SetValueWithoutNotify(TrigglePrefs.MusicVolume);
            if (sfxSlider != null) sfxSlider.SetValueWithoutNotify(TrigglePrefs.SfxVolume);

            _suppressCallbacks = false;

            RefreshVolumeLabels();
            RefreshThemeChips();
            RefreshBoardSize();
            RefreshLockState();
        }

        private void RefreshVolumeLabels()
        {
            SetPercent(masterValueLabel, TrigglePrefs.MasterVolume);
            SetPercent(musicValueLabel, TrigglePrefs.MusicVolume);
            SetPercent(sfxValueLabel, TrigglePrefs.SfxVolume);
        }

        private static void SetPercent(TMP_Text label, float value01)
        {
            if (label != null) label.text = $"{Mathf.RoundToInt(value01 * 100f)}%";
        }

        private void RefreshThemeChips()
        {
            int selected = TrigglePrefs.BoardThemeIndex;
            int available = themeLibrary != null ? themeLibrary.ThemeCount : 0;

            for (int i = 0; i < themeChips.Length; i++)
            {
                ThemeChip chip = themeChips[i];
                if (chip == null) continue;

                bool exists = i < available;
                if (chip.root != null) chip.root.SetActive(exists);
                if (!exists) continue;

                BoardTheme theme = themeLibrary.GetTheme(i);
                if (theme == null) continue;

                if (chip.swatch != null) chip.swatch.color = theme.slabColor;
                if (chip.accent != null) chip.accent.color = theme.rimColor;
                if (chip.label != null) chip.label.text = theme.displayName;
                if (chip.selectionMarker != null) chip.selectionMarker.SetActive(i == selected);
                if (chip.button != null) chip.button.interactable = !BoardLocked;
            }
        }

        private void RefreshBoardSize()
        {
            int radius = TrigglePrefs.BoardRadius;

            if (sizeValueLabel != null) sizeValueLabel.text = radius.ToString();

            if (sizeCaptionLabel != null)
            {
                // Peg and triangle counts follow directly from the radius.
                int pegs = 3 * radius * radius + 3 * radius + 1;
                int triangles = 6 * radius * radius;
                sizeCaptionLabel.text = $"{pegs} pegs  -  {triangles} triangles";
            }

            bool locked = BoardLocked;
            if (sizeDownButton != null)
                sizeDownButton.interactable = !locked && radius > TrigglePrefs.MinBoardRadius;

            if (sizeUpButton != null)
                sizeUpButton.interactable = !locked && radius < TrigglePrefs.MaxBoardRadius;
        }

        /// <summary>Shows the explanation banner and dims the board controls while a match runs.</summary>
        private void RefreshLockState()
        {
            bool locked = BoardLocked;

            if (lockedNotice != null) lockedNotice.SetActive(locked);

            if (lockedNoticeLabel != null)
                lockedNoticeLabel.text = "Board theme and size can only be changed before a match starts.";

            if (sizeValueLabel != null)
                sizeValueLabel.color = locked ? lockedControlColor : Color.white;
        }

        // ------------------------------------------------------------------ handlers

        private void HandleMasterChanged(float value)
        {
            if (_suppressCallbacks) return;

            TrigglePrefs.MasterVolume = value;
            RefreshVolumeLabels();
        }

        private void HandleMusicChanged(float value)
        {
            if (_suppressCallbacks) return;

            TrigglePrefs.MusicVolume = value;
            RefreshVolumeLabels();
        }

        private void HandleSfxChanged(float value)
        {
            if (_suppressCallbacks) return;

            TrigglePrefs.SfxVolume = value;
            RefreshVolumeLabels();

            // Audible confirmation, which is the whole point of an SFX slider.
            if (Audio.SoundManager.Instance != null) Audio.SoundManager.Instance.PlayUiClick();
        }

        private void ChooseTheme(int index)
        {
            if (BoardLocked) return;

            TrigglePrefs.BoardThemeIndex = index;
            RefreshThemeChips();
        }

        private void StepBoardSize(int delta)
        {
            if (BoardLocked) return;

            TrigglePrefs.BoardRadius += delta;
            RefreshBoardSize();
        }

        private void OnValidate()
        {
            if (themeChips != null && themeChips.Length == 6) return;

            var resized = new ThemeChip[6];
            for (int i = 0; i < 6; i++)
                resized[i] = themeChips != null && i < themeChips.Length ? themeChips[i] : new ThemeChip();

            themeChips = resized;
        }
    }
}
