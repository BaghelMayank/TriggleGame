using Triggle.Core;
using UnityEngine;

namespace Triggle.Gameplay
{
    /// <summary>
    /// Runs a best-of-N series on top of <see cref="GameFlowController"/>, which only knows how to play
    /// one board.
    /// </summary>
    /// <remarks>
    /// Terminology, because the two are easy to confuse:
    /// <list type="bullet">
    /// <item>A <b>round</b> is one complete board, played until no legal band remains. Triangle scores
    /// reset each round.</item>
    /// <item>A <b>match</b> is the best-of-N series the player picked in the lobby (1-10 rounds). Rounds
    /// won accumulate and decide the winner.</item>
    /// <item>A <b>turn cycle</b> (<see cref="GameState.TurnCycleNumber"/>) is one pass around the
    /// seats - an internal counter, not shown as "round" anywhere.</item>
    /// </list>
    /// With a single-round match this controller is almost transparent: the round ends, the match ends
    /// immediately, and the HUD hides the round counter.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MatchController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;

        [Header("Series")]
        [Tooltip("Rounds for the next match. The lobby overwrites this; 1 means a single board.")]
        [SerializeField, Range(MatchState.MinRounds, MatchState.MaxRounds)] private int roundCount = 1;

        [Tooltip("Which seat the win/lose panel is written from. Player 1 by convention for hot-seat play.")]
        [SerializeField] private PlayerId localPlayer = PlayerId.Player1;

        [Tooltip("Log round and match transitions to the console.")]
        [SerializeField] private bool verboseLogging;

        private bool _matchRunning;

        /// <summary>Series state: rounds played, rounds won, totals.</summary>
        public MatchState State { get; } = new MatchState();

        /// <summary>True between <see cref="StartMatch"/> and the match completing.</summary>
        public bool IsMatchRunning => _matchRunning;

        /// <summary>True while a finished round is waiting for the player to continue.</summary>
        public bool IsAwaitingContinue { get; private set; }

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
        }

        private void OnEnable()
        {
            GameEvents.OnGameOver += HandleRoundOver;
        }

        private void OnDisable()
        {
            GameEvents.OnGameOver -= HandleRoundOver;
        }

        /// <summary>Sets the series length for the next match. Called by the lobby.</summary>
        public void ConfigureRounds(int rounds)
        {
            roundCount = Mathf.Clamp(rounds, MatchState.MinRounds, MatchState.MaxRounds);
        }

        /// <summary>Begins a new series and starts its first round.</summary>
        public void StartMatch()
        {
            if (flowController == null)
            {
                Debug.LogError($"{nameof(MatchController)}: no {nameof(GameFlowController)} assigned.", this);
                return;
            }

            State.StartMatch(roundCount, flowController.State.ActivePlayers);

            _matchRunning = true;
            IsAwaitingContinue = false;

            Log($"Match start: best of {State.TotalRounds}.");

            flowController.StartNewGame();
            GameEvents.RaiseRoundStarted(State.CurrentRound);
        }

        /// <summary>
        /// Abandons the series. Used when the player returns to the menu mid-match.
        /// </summary>
        public void AbortMatch()
        {
            _matchRunning = false;
            IsAwaitingContinue = false;

            if (flowController != null) flowController.AbortToMenu();
        }

        /// <summary>
        /// Plays the next round. Call this from the round-summary panel's Continue button. Does nothing
        /// unless a round is actually waiting.
        /// </summary>
        public void ContinueToNextRound()
        {
            if (!IsAwaitingContinue) return;

            IsAwaitingContinue = false;

            if (!State.AdvanceRound())
            {
                // Defensive: the final round should have completed the match rather than waiting.
                CompleteMatch();
                return;
            }

            Log($"Round {State.CurrentRound} of {State.TotalRounds} starting.");

            flowController.StartNewGame();
            GameEvents.RaiseRoundStarted(State.CurrentRound);
        }

        /// <summary>Restarts the whole series with the same settings.</summary>
        public void RestartMatch() => StartMatch();

        private void HandleRoundOver(GameResult roundScores)
        {
            // A round can also end because the player hit "Play Again" outside a running match.
            if (!_matchRunning) return;

            State.RegisterRoundResult(roundScores);

            var result = new RoundResult(
                State.CurrentRound,
                State.TotalRounds,
                roundScores.Winners,
                roundScores.Scores,
                State.RoundsWon);

            GameEvents.RaiseRoundCompleted(result);

            if (result.IsDrawnRound) Log($"Round {State.CurrentRound} drawn.");
            else Log($"Round {State.CurrentRound} won by {roundScores.Winners[0]}.");

            if (State.IsFinalRound)
            {
                CompleteMatch();
                return;
            }

            // More rounds to play: park until the summary panel calls ContinueToNextRound.
            IsAwaitingContinue = true;
        }

        private void CompleteMatch()
        {
            _matchRunning = false;
            IsAwaitingContinue = false;

            MatchResult result = State.BuildResult(localPlayer);
            GameEvents.RaiseMatchCompleted(result);

            if (result.IsTie) Log($"Match tied between {result.Winners.Count} players.");
            else Log($"Match won by {result.Winners[0]} ({result.Outcome}).");
        }

        private void Log(string message)
        {
            if (verboseLogging) Debug.Log($"[Triggle] {message}", this);
        }
    }
}
