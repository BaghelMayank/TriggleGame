using Triggle.Core;
using Triggle.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Triggle.UI
{
    /// <summary>
    /// Pause overlay: Resume, Settings, and Main Menu behind a confirmation step.
    /// </summary>
    /// <remarks>
    /// The pause button used to abandon the match straight to the main menu, which threw the game away
    /// with no way back. Now it pauses, and quitting to the menu asks first - so the only way to lose a
    /// match in progress is to say yes to a question that spells it out.
    /// <para>
    /// Pausing sets <see cref="GameFlowController.SetPaused"/>, which refuses board input while leaving
    /// the match state and any in-flight resolve animation untouched, so resuming carries straight on.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class PausePanelController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;
        [SerializeField] private MatchController matchController;
        [SerializeField] private MainMenuController mainMenu;
        [SerializeField] private SettingsPanelController settings;

        [Header("Panel")]
        [SerializeField] private CanvasGroup panel;

        [Tooltip("The HUD pause button. Owned here rather than by GameUIController so only one handler " +
                 "is ever attached to it.")]
        [SerializeField] private Button pauseButton;

        [Header("Main Buttons")]
        [SerializeField] private Button resumeButton;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button restartRoundButton;
        [SerializeField] private Button mainMenuButton;

        [Header("Quit Confirmation")]
        [Tooltip("Sub-panel asking whether to abandon the match. Hidden until Main Menu is pressed.")]
        [SerializeField] private GameObject confirmGroup;

        [SerializeField] private GameObject mainButtonGroup;
        [SerializeField] private Button confirmYesButton;
        [SerializeField] private Button confirmNoButton;
        [SerializeField] private TMP_Text confirmLabel;

        [Header("Status")]
        [Tooltip("Shows which round is in progress while paused.")]
        [SerializeField] private TMP_Text contextLabel;

        /// <summary>True while the pause overlay is up.</summary>
        public bool IsPaused => panel != null && panel.gameObject.activeSelf;

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
            if (matchController == null) matchController = FindObjectOfType<MatchController>();
            if (mainMenu == null) mainMenu = FindObjectOfType<MainMenuController>();
            if (settings == null) settings = FindObjectOfType<SettingsPanelController>();

            if (pauseButton != null) pauseButton.onClick.AddListener(Pause);
            if (resumeButton != null) resumeButton.onClick.AddListener(Resume);
            if (settingsButton != null) settingsButton.onClick.AddListener(HandleSettingsClicked);
            if (restartRoundButton != null) restartRoundButton.onClick.AddListener(HandleRestartClicked);
            if (mainMenuButton != null) mainMenuButton.onClick.AddListener(AskToQuit);
            if (confirmYesButton != null) confirmYesButton.onClick.AddListener(ConfirmQuit);
            if (confirmNoButton != null) confirmNoButton.onClick.AddListener(CancelQuit);

            UITween.SetHidden(panel);
        }

        private void OnDestroy()
        {
            if (pauseButton != null) pauseButton.onClick.RemoveAllListeners();
            if (resumeButton != null) resumeButton.onClick.RemoveAllListeners();
            if (settingsButton != null) settingsButton.onClick.RemoveAllListeners();
            if (restartRoundButton != null) restartRoundButton.onClick.RemoveAllListeners();
            if (mainMenuButton != null) mainMenuButton.onClick.RemoveAllListeners();
            if (confirmYesButton != null) confirmYesButton.onClick.RemoveAllListeners();
            if (confirmNoButton != null) confirmNoButton.onClick.RemoveAllListeners();
        }

        private void OnEnable()
        {
            // A finished match must not leave the pause overlay reachable.
            GameEvents.OnMatchCompleted += HandleMatchCompleted;
            GameEvents.OnGameReset += HandleGameReset;
        }

        private void OnDisable()
        {
            GameEvents.OnMatchCompleted -= HandleMatchCompleted;
            GameEvents.OnGameReset -= HandleGameReset;
        }

        private void Update()
        {
            // Escape toggles pause, but only while a match is actually running.
            if (!Input.GetKeyDown(KeyCode.Escape)) return;
            if (matchController == null || !matchController.IsMatchRunning) return;

            if (IsPaused) Resume();
            else Pause();
        }

        // ------------------------------------------------------------------ open / close

        /// <summary>Pauses the game and shows the overlay.</summary>
        public void Pause()
        {
            if (flowController == null) return;
            if (matchController != null && !matchController.IsMatchRunning) return;

            flowController.SetPaused(true);
            CancelQuit();
            RefreshContext();

            if (isActiveAndEnabled) StartCoroutine(UITween.FadeIn(panel, 0.18f));
            else UITween.SetVisible(panel);
        }

        /// <summary>Hides the overlay and resumes play exactly where it left off.</summary>
        public void Resume()
        {
            if (flowController != null) flowController.SetPaused(false);
            if (settings != null) settings.Close();

            UITween.SetHidden(panel);
        }

        private void RefreshContext()
        {
            if (contextLabel == null) return;

            MatchState match = matchController != null ? matchController.State : null;

            contextLabel.text = match != null && match.ShowRoundCounter
                ? $"Round {match.CurrentRound} of {match.TotalRounds}"
                : "Match in progress";
        }

        // ------------------------------------------------------------------ actions

        private void HandleSettingsClicked()
        {
            // Stay paused underneath; Settings closes back onto this overlay.
            if (settings != null) settings.Open();
        }

        private void HandleRestartClicked()
        {
            Resume();
            if (matchController != null) matchController.RestartMatch();
            else if (flowController != null) flowController.RestartGame();
        }

        /// <summary>Swaps the button list for the "are you sure" prompt.</summary>
        private void AskToQuit()
        {
            if (mainButtonGroup != null) mainButtonGroup.SetActive(false);
            if (confirmGroup != null) confirmGroup.SetActive(true);

            if (confirmLabel != null)
                confirmLabel.text = "Leave the match? Progress in this match will be lost.";
        }

        private void CancelQuit()
        {
            if (confirmGroup != null) confirmGroup.SetActive(false);
            if (mainButtonGroup != null) mainButtonGroup.SetActive(true);
        }

        private void ConfirmQuit()
        {
            Resume();

            if (mainMenu != null) mainMenu.ShowRootMenu();
            else if (matchController != null) matchController.AbortMatch();
        }

        private void HandleMatchCompleted(MatchResult result) => ForceClose();

        private void HandleGameReset() => ForceClose();

        private void ForceClose()
        {
            if (flowController != null) flowController.SetPaused(false);
            CancelQuit();
            UITween.SetHidden(panel);
        }
    }
}
