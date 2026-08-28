using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Triggle.Core;
using Triggle.Gameplay;
using Triggle.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Triggle.UI
{
    /// <summary>
    /// In-game HUD plus the two end-of-play panels: a per-round summary (multi-round matches only) and
    /// the final match panel, which reads as a win, a loss or a tie.
    /// </summary>
    /// <remarks>
    /// Built on TextMeshPro. Every field is optional - leave a reference empty and that widget is
    /// skipped, which keeps the controller usable for partial or custom layouts.
    /// <para>
    /// Round counter visibility follows <see cref="MatchState.ShowRoundCounter"/>: a single-round match
    /// hides it entirely rather than displaying "1/1".
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class GameUIController : MonoBehaviour
    {
        /// <summary>One scoreboard row. All members are optional.</summary>
        [Serializable]
        public sealed class ScoreSlot
        {
            [Tooltip("Row container, hidden for seats that are not in play.")]
            public GameObject root;

            [Tooltip("Background image, tinted to highlight the active player's row.")]
            public Image background;

            [Tooltip("Colour swatch tinted with the seat colour.")]
            public Image swatch;

            public TMP_Text nameLabel;
            public TMP_Text scoreLabel;

            [Tooltip("Optional label showing rounds won, e.g. \"2 rounds\". Hidden in single-round matches.")]
            public TMP_Text roundsWonLabel;

            [Tooltip("Optional marker shown only on the active player's row.")]
            public GameObject activeMarker;
        }

        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;
        [SerializeField] private MatchController matchController;
        [SerializeField] private PlayerColorPalette palette;

        [Tooltip("Supplies the names remote players chose. Optional - absent in a local-only scene.")]
        [SerializeField] private NetworkMatch networkMatch;
        [SerializeField] private MainMenuController mainMenu;

        [Header("Turn Indicator")]
        [SerializeField] private TMP_Text turnLabel;
        [SerializeField] private Image turnColorSwatch;

        [Tooltip("Optional banner background, recoloured to the active player.")]
        [SerializeField] private Image turnBanner;

        [Tooltip("Punched on every turn change for a bit of life.")]
        [SerializeField] private Transform turnBannerPunchTarget;

        [Header("Round Counter")]
        [Tooltip("Shows \"ROUND 3/10\". Its whole container is hidden in a single-round match.")]
        [SerializeField] private TMP_Text roundLabel;

        [Tooltip("Container hidden when the match is only one round long.")]
        [SerializeField] private GameObject roundCounterRoot;

        [Tooltip("Optional label showing how many band placements are still legal.")]
        [SerializeField] private TMP_Text movesRemainingLabel;

        [Header("Scoreboard")]
        [SerializeField] private ScoreSlot[] scoreSlots = new ScoreSlot[4];
        [SerializeField] private Color activeRowColor = new Color(1f, 1f, 1f, 0.13f);
        [SerializeField] private Color inactiveRowColor = new Color(1f, 1f, 1f, 0.05f);

        [Header("Status Toast")]
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private CanvasGroup statusGroup;
        [SerializeField, Min(0.1f)] private float statusDuration = 2.2f;

        [Header("Round Summary Panel")]
        [Tooltip("Shown between rounds of a multi-round match.")]
        [SerializeField] private CanvasGroup roundPanel;
        [SerializeField] private TMP_Text roundPanelTitle;
        [SerializeField] private TMP_Text roundPanelSubtitle;
        [SerializeField] private TMP_Text roundPanelStandings;
        [SerializeField] private Button roundPanelContinueButton;
        [SerializeField] private TMP_Text roundPanelContinueLabel;

        [Header("Match Panel")]
        [SerializeField] private CanvasGroup matchPanel;
        [SerializeField] private TMP_Text matchTitle;
        [SerializeField] private TMP_Text matchSubtitle;
        [SerializeField] private TMP_Text matchStandings;
        [SerializeField] private Button rematchButton;
        [SerializeField] private Button matchMenuButton;

        [Tooltip("Accent image on the match panel, recoloured per outcome.")]
        [SerializeField] private Image matchAccent;

        [Header("Outcome Colours")]
        [SerializeField] private Color winColor = new Color(1f, 0.80f, 0.28f);
        [SerializeField] private Color loseColor = new Color(0.96f, 0.32f, 0.34f);
        [SerializeField] private Color tieColor = new Color(0.62f, 0.68f, 0.78f);

        private readonly Dictionary<PlayerId, int> _scores = new Dictionary<PlayerId, int>();
        private readonly StringBuilder _builder = new StringBuilder(192);
        private Coroutine _statusRoutine;

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
            if (matchController == null) matchController = FindObjectOfType<MatchController>();
            if (palette == null) palette = PlayerColorPalette.Fallback;
            if (mainMenu == null) mainMenu = FindObjectOfType<MainMenuController>();
            if (networkMatch == null) networkMatch = FindObjectOfType<NetworkMatch>();

            if (rematchButton != null) rematchButton.onClick.AddListener(HandleRematchClicked);
            if (matchMenuButton != null) matchMenuButton.onClick.AddListener(HandleMenuClicked);
            if (roundPanelContinueButton != null) roundPanelContinueButton.onClick.AddListener(HandleContinueClicked);

            UITween.SetHidden(matchPanel);
            UITween.SetHidden(roundPanel);
            SetStatus(string.Empty, Color.white, 0f);
        }

        private void OnDestroy()
        {
            if (rematchButton != null) rematchButton.onClick.RemoveListener(HandleRematchClicked);
            if (matchMenuButton != null) matchMenuButton.onClick.RemoveListener(HandleMenuClicked);
            if (roundPanelContinueButton != null) roundPanelContinueButton.onClick.RemoveListener(HandleContinueClicked);
        }

        private void OnEnable()
        {
            GameEvents.OnTurnStarted += HandleTurnStarted;
            GameEvents.OnScoreChanged += HandleScoreChanged;
            GameEvents.OnInvalidMove += HandleInvalidMove;
            GameEvents.OnCellClaimed += HandleCellClaimed;
            GameEvents.OnGameReset += HandleGameReset;
            GameEvents.OnRoundStarted += HandleRoundStarted;
            GameEvents.OnRoundCompleted += HandleRoundCompleted;
            GameEvents.OnMatchCompleted += HandleMatchCompleted;
        }

        private void OnDisable()
        {
            GameEvents.OnTurnStarted -= HandleTurnStarted;
            GameEvents.OnScoreChanged -= HandleScoreChanged;
            GameEvents.OnInvalidMove -= HandleInvalidMove;
            GameEvents.OnCellClaimed -= HandleCellClaimed;
            GameEvents.OnGameReset -= HandleGameReset;
            GameEvents.OnRoundStarted -= HandleRoundStarted;
            GameEvents.OnRoundCompleted -= HandleRoundCompleted;
            GameEvents.OnMatchCompleted -= HandleMatchCompleted;
        }

        /// <summary>
        /// The seat's typed name, or its palette default. Computer seats ignore any stored name and are
        /// always shown as their colour plus a "(CPU)" tag, so the scoreboard makes it obvious at a
        /// glance which of the totals is yours.
        /// </summary>
        /// <remarks>
        /// Online, the name comes from the room roster - what that player called themselves on their own
        /// device. <see cref="PlayerProfiles"/> holds the names typed into <i>this</i> device's hot-seat
        /// lobby, which describe whoever is sharing this phone and have nothing to do with the person on
        /// the other end of the session; using them made every remote player show up under a local name
        /// or a bare colour.
        /// </remarks>
        private string NameOf(PlayerId player)
        {
            if (networkMatch != null && networkMatch.TryGetSeatName((int)player, out string online))
                return online;

            return SeatRoster.IsComputer(player)
                ? SeatRoster.Decorate(player, palette.GetDisplayName(player))
                : PlayerProfiles.GetName(player, palette.GetDisplayName(player));
        }

        private MatchState Match => matchController != null ? matchController.State : null;

        // ------------------------------------------------------------------ HUD

        private void HandleGameReset()
        {
            _scores.Clear();

            UITween.SetHidden(matchPanel);
            UITween.SetHidden(roundPanel);
            SetStatus(string.Empty, Color.white, 0f);

            ConfigureScoreboard();
            RefreshScoreboard(PlayerId.None);
            RefreshRoundCounter();
        }

        private void HandleRoundStarted(int roundNumber) => RefreshRoundCounter();

        /// <summary>Shows "ROUND 3/10", or hides the whole widget for a single-round match.</summary>
        private void RefreshRoundCounter()
        {
            MatchState match = Match;
            bool show = match != null && match.ShowRoundCounter;

            if (roundCounterRoot != null) roundCounterRoot.SetActive(show);

            if (show && roundLabel != null)
                roundLabel.text = $"ROUND {match.CurrentRound}/{match.TotalRounds}";
        }

        private void HandleTurnStarted(PlayerId player)
        {
            string name = NameOf(player);
            Color color = palette.GetColor(player);

            if (turnLabel != null)
            {
                turnLabel.text = $"{name}'s Turn - Stretch {BandPegCount()} Pegs";
                turnLabel.color = color;
            }

            if (turnColorSwatch != null) turnColorSwatch.color = color;
            if (turnBanner != null) turnBanner.color = new Color(color.r, color.g, color.b, 0.16f);

            if (movesRemainingLabel != null && flowController?.Validator != null)
                movesRemainingLabel.text = $"{flowController.Validator.CountLegalMoves()} bands left";

            RefreshScoreboard(player);
            RefreshRoundCounter();

            if (turnBannerPunchTarget != null && isActiveAndEnabled)
                StartCoroutine(UITween.Punch(turnBannerPunchTarget, 0.06f, 0.26f));
        }

        private int BandPegCount() =>
            flowController?.Validator != null ? flowController.Validator.RequiredPegCount : 4;

        private void HandleScoreChanged(PlayerId player, int total)
        {
            _scores[player] = total;
            RefreshScoreboard(CurrentPlayer());

            int index = (int)player - 1;
            if (index < 0 || index >= scoreSlots.Length) return;

            ScoreSlot slot = scoreSlots[index];
            if (slot?.scoreLabel != null && isActiveAndEnabled)
                StartCoroutine(UITween.Punch(slot.scoreLabel.transform, 0.35f, 0.26f));
        }

        private void HandleCellClaimed(TriangleCell cell)
        {
            if (cell == null) return;
            SetStatus($"{NameOf(cell.Owner)} claimed a triangle!", palette.GetColor(cell.Owner), statusDuration);
        }

        private void HandleInvalidMove(string reason) =>
            SetStatus(reason, new Color(1f, 0.55f, 0.5f), statusDuration);

        // ------------------------------------------------------------------ round summary

        private void HandleRoundCompleted(RoundResult result)
        {
            RefreshScoreboard(PlayerId.None);

            // The final round rolls straight into the match panel, so no summary is shown for it.
            if (result.IsFinalRound) return;

            if (roundPanelTitle != null)
            {
                if (result.IsDrawnRound)
                {
                    roundPanelTitle.text = $"ROUND {result.RoundNumber} DRAWN";
                    roundPanelTitle.color = tieColor;
                }
                else
                {
                    PlayerId winner = result.Winners[0];
                    roundPanelTitle.text = $"{NameOf(winner).ToUpperInvariant()} WINS ROUND {result.RoundNumber}";
                    roundPanelTitle.color = palette.GetColor(winner);
                }
            }

            if (roundPanelSubtitle != null)
                roundPanelSubtitle.text = $"Round {result.RoundNumber} of {result.TotalRounds} complete";

            if (roundPanelStandings != null)
                roundPanelStandings.text = BuildStandings(result.Scores, result.RoundsWon, "pts");

            if (roundPanelContinueLabel != null)
                roundPanelContinueLabel.text = $"START ROUND {result.RoundNumber + 1}";

            ShowPanel(roundPanel);
        }

        private void HandleContinueClicked()
        {
            UITween.SetHidden(roundPanel);
            if (matchController != null) matchController.ContinueToNextRound();
        }

        // ------------------------------------------------------------------ match panel

        private void HandleMatchCompleted(MatchResult result)
        {
            UITween.SetHidden(roundPanel);

            Color accent = result.Outcome switch
            {
                MatchOutcome.Win => winColor,
                MatchOutcome.Lose => loseColor,
                _ => tieColor
            };

            if (matchTitle != null)
            {
                matchTitle.text = result.Outcome switch
                {
                    MatchOutcome.Win => $"{NameOf(result.Winners[0]).ToUpperInvariant()} WINS!",
                    MatchOutcome.Lose => $"{NameOf(result.Winners[0]).ToUpperInvariant()} WINS!",
                    _ => "MATCH TIED"
                };
                matchTitle.color = accent;
            }

            if (matchAccent != null) matchAccent.color = accent;

            if (matchSubtitle != null)
            {
                if (result.TotalRounds <= 1)
                {
                    matchSubtitle.text = result.IsTie ? "Nobody could be separated" : "Single round";
                }
                else if (result.IsTie)
                {
                    var names = new List<string>(result.Winners.Count);
                    for (int i = 0; i < result.Winners.Count; i++) names.Add(NameOf(result.Winners[i]));
                    matchSubtitle.text = $"Level on rounds: {string.Join("  &  ", names)}";
                }
                else
                {
                    PlayerId winner = result.Winners[0];
                    int won = result.RoundsWon.TryGetValue(winner, out int w) ? w : 0;
                    matchSubtitle.text = $"{won} of {result.TotalRounds} rounds won";
                }
            }

            if (matchStandings != null)
                matchStandings.text = BuildStandings(result.TotalScores, result.RoundsWon, "total",
                                                      result.TotalRounds > 1);

            SetText(turnLabel, result.IsTie ? "Match tied" : "Match over");
            SetStatus(string.Empty, Color.white, 0f);

            ShowPanel(matchPanel);
        }

        private void HandleRematchClicked()
        {
            UITween.SetHidden(matchPanel);
            if (matchController != null) matchController.RestartMatch();
            else flowController?.RestartGame();
        }

        private void HandleMenuClicked()
        {
            UITween.SetHidden(matchPanel);
            UITween.SetHidden(roundPanel);

            if (matchController != null) matchController.AbortMatch();

            if (mainMenu != null) mainMenu.ShowMenu();
            else flowController?.AbortToMenu();
        }

        /// <summary>
        /// Standings table, highest first. Rounds won are appended only for multi-round matches, where
        /// they are the figure that actually decides the winner.
        /// </summary>
        private string BuildStandings(IReadOnlyDictionary<PlayerId, int> scores,
                                       IReadOnlyDictionary<PlayerId, int> roundsWon,
                                       string scoreSuffix, bool includeRounds = true)
        {
            _builder.Clear();

            var standings = new List<KeyValuePair<PlayerId, int>>(scores);
            standings.Sort((a, b) => b.Value.CompareTo(a.Value));

            bool showRounds = includeRounds && roundsWon != null && Match != null && Match.TotalRounds > 1;

            for (int i = 0; i < standings.Count; i++)
            {
                PlayerId player = standings[i].Key;
                string hex = ColorUtility.ToHtmlStringRGB(palette.GetColor(player));

                _builder.Append($"<color=#{hex}>{i + 1}.  {NameOf(player)}</color>");
                _builder.Append($"   <b>{standings[i].Value}</b> {scoreSuffix}");

                if (showRounds && roundsWon.TryGetValue(player, out int won))
                    _builder.Append($"   <color=#{hex}>({won} rd)</color>");

                _builder.AppendLine();
            }

            return _builder.ToString().TrimEnd();
        }

        private void ShowPanel(CanvasGroup group)
        {
            if (group == null) return;

            if (isActiveAndEnabled) StartCoroutine(UITween.FadeIn(group, 0.32f));
            else UITween.SetVisible(group);
        }

        // ------------------------------------------------------------------ scoreboard

        /// <summary>Shows one row per seat in play and hides the rest.</summary>
        private void ConfigureScoreboard()
        {
            int seatsInPlay = flowController?.State != null
                ? flowController.State.ActivePlayers.Count
                : 2;

            bool showRounds = Match != null && Match.TotalRounds > 1;

            for (int i = 0; i < scoreSlots.Length; i++)
            {
                ScoreSlot slot = scoreSlots[i];
                if (slot == null) continue;

                bool active = i < seatsInPlay;
                if (slot.root != null) slot.root.SetActive(active);
                if (!active) continue;

                var player = (PlayerId)(i + 1);
                Color color = palette.GetColor(player);

                if (slot.swatch != null) slot.swatch.color = color;
                if (slot.nameLabel != null)
                {
                    slot.nameLabel.text = NameOf(player);
                    slot.nameLabel.color = color;
                }

                if (slot.roundsWonLabel != null) slot.roundsWonLabel.gameObject.SetActive(showRounds);
            }
        }

        private void RefreshScoreboard(PlayerId activePlayer)
        {
            MatchState match = Match;

            for (int i = 0; i < scoreSlots.Length; i++)
            {
                ScoreSlot slot = scoreSlots[i];
                if (slot == null) continue;

                var player = (PlayerId)(i + 1);
                bool isActive = player == activePlayer;

                if (slot.scoreLabel != null)
                {
                    _scores.TryGetValue(player, out int score);
                    slot.scoreLabel.text = score.ToString();
                }

                if (slot.roundsWonLabel != null && match != null && match.TotalRounds > 1)
                {
                    int won = match.GetRoundsWon(player);
                    slot.roundsWonLabel.text = won == 1 ? "1 round" : $"{won} rounds";
                }

                if (slot.background != null) slot.background.color = isActive ? activeRowColor : inactiveRowColor;
                if (slot.activeMarker != null) slot.activeMarker.SetActive(isActive);
            }
        }

        private PlayerId CurrentPlayer() =>
            flowController?.State != null ? flowController.State.CurrentPlayer : PlayerId.None;

        // ------------------------------------------------------------------ status toast

        /// <summary>
        /// Shows a transient message, replacing any message still on screen. A
        /// <paramref name="duration"/> of 0 clears it immediately.
        /// </summary>
        public void SetStatus(string message, Color color, float duration)
        {
            if (statusLabel == null) return;

            statusLabel.text = message;
            statusLabel.color = color;

            if (_statusRoutine != null)
            {
                StopCoroutine(_statusRoutine);
                _statusRoutine = null;
            }

            bool visible = duration > 0f && !string.IsNullOrEmpty(message);

            if (statusGroup != null)
            {
                statusGroup.alpha = visible ? 1f : 0f;
                statusGroup.blocksRaycasts = false;
            }

            if (visible && isActiveAndEnabled) _statusRoutine = StartCoroutine(ClearStatusAfterDelay(duration));
        }

        private IEnumerator ClearStatusAfterDelay(float duration)
        {
            yield return new WaitForSeconds(duration);

            if (statusGroup != null) yield return UITween.FadeOut(statusGroup, 0.22f, false);
            if (statusLabel != null) statusLabel.text = string.Empty;

            _statusRoutine = null;
        }

        private static void SetText(TMP_Text label, string value)
        {
            if (label != null) label.text = value;
        }

        private void OnValidate()
        {
            if (scoreSlots != null && scoreSlots.Length == 4) return;

            var resized = new ScoreSlot[4];
            for (int i = 0; i < 4; i++)
                resized[i] = scoreSlots != null && i < scoreSlots.Length ? scoreSlots[i] : new ScoreSlot();

            scoreSlots = resized;
        }
    }
}
