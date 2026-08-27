using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.Gameplay;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Triggle.UI
{
    /// <summary>
    /// Front-end screen manager. Guarantees that exactly one screen is on show at any moment.
    /// </summary>
    /// <remarks>
    /// The important rule here is <b>hide-then-show, never fade-then-fade</b>. An earlier version faded
    /// the outgoing screens out one after another and cancelled the coroutine when a new navigation
    /// started, which left an abandoned panel stuck at partial alpha and still active - two screens
    /// visible at once, with the board showing through both. Now every non-target screen is switched off
    /// instantly and only the incoming one animates, so an interrupted transition cannot leave residue.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MainMenuController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;
        [SerializeField] private MatchController matchController;
        [SerializeField] private LobbyController lobby;
        [SerializeField] private SettingsPanelController settings;

        [Header("Screens")]
        [SerializeField] private CanvasGroup rootMenuPanel;
        [SerializeField] private CanvasGroup lobbyPanel;
        [SerializeField] private CanvasGroup howToPlayPanel;
        [SerializeField] private CanvasGroup hudPanel;

        [Header("Root Menu Buttons")]
        [SerializeField] private Button playLocalButton;
        [SerializeField] private Button playAiButton;
        [SerializeField] private Button howToPlayButton;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button quitButton;

        [Tooltip("Label on the Play vs AI button.")]
        [SerializeField] private TMP_Text playAiLabel;

        [Tooltip("Caption under the Play vs AI button, showing the current difficulty.")]
        [SerializeField] private TMP_Text playAiSubLabel;

        [Header("How To Play")]
        [SerializeField] private Button howToPlayCloseButton;
        [SerializeField] private TMP_Text howToPlayBody;

        [Header("Transitions")]
        [SerializeField, Min(0.05f)] private float fadeDuration = 0.22f;

        /// <summary>Every screen this controller owns, used to force-hide all but the target.</summary>
        private readonly List<CanvasGroup> _screens = new List<CanvasGroup>(4);
        private Coroutine _transition;

        /// <summary>True while a front-end screen is showing rather than the HUD.</summary>
        public bool IsFrontEndOpen =>
            IsShowing(rootMenuPanel) || IsShowing(lobbyPanel) || IsShowing(howToPlayPanel);

        private static bool IsShowing(CanvasGroup group) => group != null && group.gameObject.activeSelf;

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
            if (matchController == null) matchController = FindObjectOfType<MatchController>();
            if (lobby == null) lobby = FindObjectOfType<LobbyController>();
            if (settings == null) settings = FindObjectOfType<SettingsPanelController>();

            _screens.Clear();
            _screens.Add(rootMenuPanel);
            _screens.Add(lobbyPanel);
            _screens.Add(howToPlayPanel);
            _screens.Add(hudPanel);

            if (playLocalButton != null) playLocalButton.onClick.AddListener(HandleLocalClicked);
            if (playAiButton != null) playAiButton.onClick.AddListener(HandleAiClicked);
            if (howToPlayButton != null) howToPlayButton.onClick.AddListener(ShowHowToPlay);
            if (settingsButton != null) settingsButton.onClick.AddListener(OpenSettings);
            if (quitButton != null) quitButton.onClick.AddListener(HandleQuitClicked);
            if (howToPlayCloseButton != null) howToPlayCloseButton.onClick.AddListener(ShowRootMenu);

            RefreshAiCaption();
            FillHowToPlayText();
        }

        private void OnEnable()
        {
            SeatRoster.OnRosterChanged += RefreshAiCaption;
        }

        private void OnDisable()
        {
            SeatRoster.OnRosterChanged -= RefreshAiCaption;
        }

        private void OnDestroy()
        {
            if (playLocalButton != null) playLocalButton.onClick.RemoveAllListeners();
            if (playAiButton != null) playAiButton.onClick.RemoveAllListeners();
            if (howToPlayButton != null) howToPlayButton.onClick.RemoveAllListeners();
            if (settingsButton != null) settingsButton.onClick.RemoveAllListeners();
            if (quitButton != null) quitButton.onClick.RemoveAllListeners();
            if (howToPlayCloseButton != null) howToPlayCloseButton.onClick.RemoveAllListeners();
        }

        private void Start()
        {
            PlayerProfiles.Load();
            TrigglePrefs.Load();

            ShowScreenImmediate(rootMenuPanel);
        }

        /// <summary>
        /// Shows the level the computer will play at under the button, so the choice is visible from the
        /// root menu rather than only once the lobby is open.
        /// </summary>
        private void RefreshAiCaption()
        {
            if (playAiSubLabel == null) return;

            playAiSubLabel.gameObject.SetActive(true);
            playAiSubLabel.text = $"Difficulty: {SeatRoster.DifficultyName(SeatRoster.Difficulty)}";
        }

        private void FillHowToPlayText()
        {
            if (howToPlayBody == null) return;

            int pegs = flowController?.Board != null ? flowController.Board.PegsPerBand : 4;

            howToPlayBody.text =
                $"<b>Place a band.</b>  Tap {pegs} pegs in a <b>straight line</b> - a rubber band cannot " +
                "be bent. The band covers the segments between those pegs.\n\n" +
                "<b>Claim triangles.</b>  When all three sides of a small triangle are covered, whoever " +
                "covered the last side claims it and scores a point.\n\n" +
                "<b>The catch.</b>  A triangle's three sides all run in different directions, so one " +
                "band can only ever cover one side of it. Every triangle needs three separate bands - " +
                "set one up and your opponent can take it with the third.\n\n" +
                "<b>Winning.</b>  A round ends when no legal band is left. Most triangles wins the round; " +
                "most rounds wins the match.";
        }

        // ------------------------------------------------------------------ navigation

        /// <summary>Returns to the root menu, abandoning any match in progress.</summary>
        public void ShowRootMenu()
        {
            if (matchController != null) matchController.AbortMatch();
            else if (flowController != null) flowController.AbortToMenu();

            if (settings != null) settings.Close();
            ShowScreen(rootMenuPanel);
        }

        /// <summary>Opens the lobby, refreshed from stored names and colours.</summary>
        public void ShowLobby()
        {
            if (lobby != null) lobby.Refresh();
            ShowScreen(lobbyPanel);
        }

        public void ShowHowToPlay()
        {
            FillHowToPlayText();
            ShowScreen(howToPlayPanel);
        }

        /// <summary>
        /// Hides the front end and reveals the HUD. Switched instantly rather than faded: the match has
        /// already started by this point, so any lingering front-end panel would sit over a live board.
        /// </summary>
        public void EnterGame() => ShowScreenImmediate(hudPanel);

        /// <summary>Alias kept for the HUD and match-panel call sites, where the intent reads better.</summary>
        public void ShowMenu() => ShowRootMenu();

        public void OpenSettings()
        {
            if (settings != null) settings.Open();
        }

        /// <summary>
        /// Hot-seat: every seat is a person. Both entry points land in the same lobby, where the lineup
        /// can still be changed seat by seat - the two buttons only choose which starting point is
        /// nearer to what the player asked for.
        /// </summary>
        private void HandleLocalClicked()
        {
            SeatRoster.SetAllHuman();
            ShowLobby();
        }

        /// <summary>Single player: seat 1 is the person, the rest are the computer.</summary>
        private void HandleAiClicked()
        {
            SeatRoster.SetSinglePlayerLineup();
            ShowLobby();
        }

        private void HandleQuitClicked()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ------------------------------------------------------------------ screen switching

        /// <summary>
        /// Makes <paramref name="target"/> the only visible screen, fading it in. Every other screen is
        /// switched off in the same frame, so nothing can be left half-faded.
        /// </summary>
        private void ShowScreen(CanvasGroup target)
        {
            HideAllExcept(target);
            if (target == null) return;

            if (_transition != null) StopCoroutine(_transition);
            _transition = StartCoroutine(FadeInRoutine(target));
        }

        /// <summary>Same as <see cref="ShowScreen"/> but with no animation at all.</summary>
        private void ShowScreenImmediate(CanvasGroup target)
        {
            HideAllExcept(target);

            if (_transition != null)
            {
                StopCoroutine(_transition);
                _transition = null;
            }

            UITween.SetVisible(target);
        }

        /// <summary>
        /// Hard-hides every registered screen except one. This is what makes an interrupted transition
        /// safe: a cancelled fade can never leave a panel active at partial alpha.
        /// </summary>
        private void HideAllExcept(CanvasGroup target)
        {
            for (int i = 0; i < _screens.Count; i++)
            {
                CanvasGroup screen = _screens[i];
                if (screen == null || screen == target) continue;

                UITween.SetHidden(screen);
            }
        }

        private IEnumerator FadeInRoutine(CanvasGroup target)
        {
            yield return UITween.FadeIn(target, fadeDuration);
            _transition = null;
        }
    }
}
