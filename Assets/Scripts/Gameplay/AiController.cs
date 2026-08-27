using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using UnityEngine;

namespace Triggle.Gameplay
{
    /// <summary>
    /// Plays the seats <see cref="SeatRoster"/> marks as computer-controlled, by driving the same
    /// <see cref="GameFlowController.SubmitBandSelection"/> entry point the human input layer uses.
    /// </summary>
    /// <remarks>
    /// Going through the public submit path rather than reaching into the board is deliberate: the AI is
    /// held to the same rules as the player, and any move it produces that the validator would reject
    /// surfaces immediately as a failed submission instead of a quietly illegal board state.
    /// <para>
    /// The move is revealed peg by peg through <see cref="GameEvents.OnSelectionChanged"/>, which is the
    /// event the human selection also raises - so the peg highlighting the player already knows shows the
    /// computer building its band, rather than a band simply appearing.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class AiController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;

        [Header("Pacing (seconds)")]
        [Tooltip("Pause before the computer starts picking, randomised between the two values so it " +
                 "does not feel metronomic.")]
        [SerializeField] private Vector2 thinkDelayRange = new Vector2(0.45f, 0.95f);

        [Tooltip("Gap between the computer's peg picks. Zero submits the whole band at once.")]
        [SerializeField, Min(0f)] private float pegPickInterval = 0.14f;

        [Tooltip("Log the computer's choice and its reasoning counts to the console.")]
        [SerializeField] private bool verboseLogging;

        /// <summary>The pegs revealed so far this turn, shared with the highlighting layer.</summary>
        private readonly List<Peg> _revealed = new List<Peg>(5);

        private BandEvaluator _evaluator;
        private Coroutine _turnRoutine;

        /// <summary>True while a computer seat is choosing or revealing a move.</summary>
        public bool IsThinking => _turnRoutine != null;

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
        }

        private void OnEnable()
        {
            GameEvents.OnTurnStarted += HandleTurnStarted;
            GameEvents.OnGameReset += HandleGameReset;
            GameEvents.OnGameOver += HandleGameOver;
        }

        private void OnDisable()
        {
            GameEvents.OnTurnStarted -= HandleTurnStarted;
            GameEvents.OnGameReset -= HandleGameReset;
            GameEvents.OnGameOver -= HandleGameOver;

            CancelTurn();
        }

        private void HandleTurnStarted(PlayerId player)
        {
            CancelTurn();

            if (!SeatRoster.IsComputer(player)) return;
            if (flowController == null || flowController.Board == null) return;

            _turnRoutine = StartCoroutine(TakeTurnRoutine(player));
        }

        private void HandleGameReset() => CancelTurn();

        private void HandleGameOver(GameResult result) => CancelTurn();

        /// <summary>
        /// Stops any move in progress and clears the revealed pegs. Called whenever the turn this
        /// coroutine was started for can no longer be valid.
        /// </summary>
        private void CancelTurn()
        {
            if (_turnRoutine != null)
            {
                StopCoroutine(_turnRoutine);
                _turnRoutine = null;
            }

            // A cancelled reveal would otherwise leave the computer's half-built band highlighted.
            if (_revealed.Count == 0) return;

            _revealed.Clear();
            GameEvents.RaiseSelectionChanged(_revealed);
        }

        private IEnumerator TakeTurnRoutine(PlayerId player)
        {
            // Built once and kept: the evaluator holds no board snapshot, only the BoardManager, whose
            // band catalogue it re-reads on every call. A radius change regenerates that catalogue in
            // place, so the cached evaluator keeps working across matches.
            EnsureEvaluator();

            float think = Random.Range(thinkDelayRange.x, thinkDelayRange.y);
            while (think > 0f)
            {
                if (!IsStillOurTurn(player)) { _turnRoutine = null; yield break; }
                if (!flowController.IsPaused) think -= Time.deltaTime;
                yield return null;
            }

            BandPlacement band = _evaluator.ChooseBand(SeatRoster.Difficulty);
            if (band == null)
            {
                // No legal band left. The flow controller reaches the same conclusion at the top of the
                // next turn and ends the round, so there is nothing to do here but stand down.
                Log($"{player}: no legal band available.");
                _turnRoutine = null;
                yield break;
            }

            Log($"{player} plays band #{band.Id} ({SeatRoster.DifficultyName(SeatRoster.Difficulty)}).");

            // --- reveal the picks one at a time ------------------------------
            _revealed.Clear();

            for (int i = 0; i < band.Pegs.Length; i++)
            {
                if (!IsStillOurTurn(player)) { CancelTurn(); yield break; }

                _revealed.Add(band.Pegs[i]);
                GameEvents.RaiseSelectionChanged(_revealed);

                // No pause after the final peg: the band placement animation follows straight on.
                if (i == band.Pegs.Length - 1) break;

                float step = pegPickInterval;
                while (step > 0f)
                {
                    if (!IsStillOurTurn(player)) { CancelTurn(); yield break; }
                    if (!flowController.IsPaused) step -= Time.deltaTime;
                    yield return null;
                }
            }

            _revealed.Clear();
            GameEvents.RaiseSelectionChanged(_revealed);

            // Cleared before submitting: SubmitBandSelection resolves synchronously up to the first
            // yield, and a stale routine handle would make the next CancelTurn stop the wrong coroutine.
            _turnRoutine = null;

            if (!IsStillOurTurn(player)) yield break;

            if (!flowController.SubmitBandSelection(band.Pegs))
            {
                // The evaluator and the validator disagreed about legality - a real bug rather than a
                // recoverable state, so it is logged loudly instead of being retried into a loop.
                Debug.LogError($"[Triggle] {nameof(AiController)}: {player} chose band #{band.Id}, " +
                               "which the validator rejected. The AI and the rules engine are out of step.",
                               this);
            }
        }

        /// <summary>
        /// True while the seat this routine was started for is still the one on the clock and the board
        /// is still accepting a move. Guards against the player abandoning to the menu mid-think, which
        /// parks the state machine without raising an event.
        /// </summary>
        private bool IsStillOurTurn(PlayerId player) =>
            flowController != null &&
            flowController.State != null &&
            flowController.State.AcceptsInput &&
            flowController.State.CurrentPlayer == player;

        private void EnsureEvaluator()
        {
            if (_evaluator == null)
                _evaluator = new BandEvaluator(flowController.Board, flowController.Settings);
        }

        private void Log(string message)
        {
            if (verboseLogging) Debug.Log($"[Triggle] AI: {message}", this);
        }

        private void OnValidate()
        {
            thinkDelayRange.x = Mathf.Max(0f, thinkDelayRange.x);
            thinkDelayRange.y = Mathf.Max(thinkDelayRange.x, thinkDelayRange.y);
        }
    }
}
