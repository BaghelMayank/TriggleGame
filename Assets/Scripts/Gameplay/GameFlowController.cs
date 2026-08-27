using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.Grid;
using UnityEngine;

namespace Triggle.Gameplay
{
    /// <summary>
    /// The match orchestrator and owner of the turn state machine:
    /// <c>WaitingForInput -> ValidatingMove -> PlacingBand -> ResolvingClaims -> CheckingEndGame -> NextTurn | GameOver</c>.
    /// </summary>
    /// <remarks>
    /// This is the only class allowed to mutate board occupancy or cell ownership. Everything else
    /// either asks it a question (via <see cref="Validator"/>) or reacts to <see cref="GameEvents"/>.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class GameFlowController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private BoardManager board;
        [SerializeField] private ScoreManager scoreManager;

        [Header("Rules")]
        [SerializeField] private GameSettings settings = new GameSettings();

        [Header("Lifecycle")]
        [Tooltip("Start a match automatically on Start(). Leave OFF when a main menu drives the game: " +
                 "the board is still generated so it can sit behind the menu as a backdrop.")]
        [SerializeField] private bool autoStart;

        [Tooltip("Log every state transition and claim to the console.")]
        [SerializeField] private bool verboseLogging;

        [Tooltip("Take the board radius from the player's Settings choice when starting a match. " +
                 "Board size can only change between matches, which is why it is read here and not " +
                 "watched continuously.")]
        [SerializeField] private bool applyBoardSizeFromPrefs = true;

        private readonly List<TriangleCell> _affectedCells = new List<TriangleCell>(16);
        private readonly HashSet<TriangleCell> _affectedCellSet = new HashSet<TriangleCell>();
        private Coroutine _moveRoutine;

        /// <summary>Runtime match state: current seat, phase and counters.</summary>
        public GameState State { get; private set; }

        /// <summary>Shared rules engine. Input and highlighting query this for legality.</summary>
        public MoveValidator Validator { get; private set; }

        public BoardManager Board => board;
        public ScoreManager Scores => scoreManager;
        public GameSettings Settings => settings;

        /// <summary>
        /// True while the game is paused. Board input is refused, but the match state is untouched, so
        /// resuming continues exactly where it left off.
        /// </summary>
        public bool IsPaused { get; private set; }

        /// <summary>True when a person at this device plays the seat on the clock.</summary>
        public bool IsCurrentSeatLocal => State != null && SeatRoster.IsLocalHuman(State.CurrentPlayer);

        /// <summary>
        /// True while peg clicks should be accepted. A seat that is not yours - the computer's, or a
        /// player on another device - closes this without changing the phase, so
        /// <see cref="AiController"/> and the network layer can still submit through
        /// <see cref="SubmitBandSelection"/> and <see cref="SubmitBandById"/>, which gate on the phase
        /// alone, while the pointer stays locked out.
        /// </summary>
        public bool AcceptsInput =>
            State != null && State.AcceptsInput && !IsPaused && IsCurrentSeatLocal;

        /// <summary>
        /// Pauses or resumes. Deliberately does not touch the state machine or stop the resolve
        /// coroutine: pausing mid-animation and resuming should just carry on.
        /// </summary>
        public void SetPaused(bool paused) => IsPaused = paused;

        private void Awake()
        {
            if (board == null) board = FindObjectOfType<BoardManager>();
            if (scoreManager == null) scoreManager = FindObjectOfType<ScoreManager>();

            settings.Validate();
            State = new GameState(settings);
        }

        private void Start()
        {
            // Generate the lattice even when a menu will start the match, so the board is visible
            // behind the menu instead of showing an empty scene.
            if (board != null && !board.IsBuilt) board.Build();

            if (autoStart) StartNewGame();
        }

        /// <summary>
        /// Sets the number of seats for the next match. Takes effect on the following
        /// <see cref="StartNewGame"/>; call it from the menu before starting.
        /// </summary>
        public void ConfigurePlayerCount(int count)
        {
            settings.playerCount = Mathf.Clamp(count, 2, 4);
            settings.Validate();
        }

        /// <summary>
        /// Abandons the match in progress and parks the state machine so no input is accepted.
        /// Used when the player returns to the main menu mid-game.
        /// </summary>
        public void AbortToMenu()
        {
            if (_moveRoutine != null)
            {
                StopCoroutine(_moveRoutine);
                _moveRoutine = null;
            }

            IsPaused = false;
            if (State != null) State.Phase = GamePhase.Uninitialized;
        }

        /// <summary>
        /// Builds the board (first call) or resets it in place (subsequent calls), zeroes the scores
        /// and hands the first turn to Player 1.
        /// </summary>
        public void StartNewGame()
        {
            if (board == null)
            {
                Debug.LogError($"{nameof(GameFlowController)}: no {nameof(BoardManager)} assigned.", this);
                return;
            }

            if (_moveRoutine != null)
            {
                StopCoroutine(_moveRoutine);
                _moveRoutine = null;
            }

            // Refresh state before broadcasting, so listeners that read seat counts see the new setup.
            settings.Validate();
            State.Reset();
            IsPaused = false;

            GameEvents.RaiseGameReset();

            // Board size is a between-matches choice: read it here, and regenerate the lattice only
            // when it actually changed. Otherwise a reset is enough and costs no allocations.
            bool sizeChanged = applyBoardSizeFromPrefs && board.SetRadius(TrigglePrefs.BoardRadius);

            if (board.IsBuilt && !sizeChanged) board.ResetRuntimeState();
            else board.Build();

            Validator = new MoveValidator(board, settings);

            if (scoreManager != null) scoreManager.Initialize(State.ActivePlayers);

            Log($"New game: {settings.playerCount} players, R={board.Radius}, " +
                $"{board.Pegs.Count} pegs, {board.Edges.Count} edges, {board.TotalCells} cells, " +
                $"{board.Bands.Count} band slots.");

            BeginTurn();
        }

        /// <summary>Alias used by the UI restart button.</summary>
        public void RestartGame() => StartNewGame();

        /// <summary>
        /// Entry point for the input layer. Validates immediately so the caller gets synchronous
        /// feedback, then runs placement, claim resolution and the end-game check over several frames.
        /// </summary>
        /// <returns>True when the selection was accepted and the move is now resolving.</returns>
        public bool SubmitBandSelection(IReadOnlyList<Peg> selection)
        {
            if (State == null || Validator == null) return false;

            if (!State.AcceptsInput)
            {
                GameEvents.RaiseInvalidMove("Not your moment - the board is still resolving.");
                return false;
            }

            State.Phase = GamePhase.ValidatingMove;

            if (!Validator.TryResolveBand(selection, out BandPlacement band, out string reason))
            {
                GameEvents.RaiseInvalidMove(reason);
                State.Phase = GamePhase.WaitingForInput;
                return false;
            }

            _moveRoutine = StartCoroutine(ResolveMoveRoutine(band));
            return true;
        }

        /// <summary>
        /// Plays a band by its index in the board's catalogue. This is how a move arrives from another
        /// device, and how the AI could submit one too.
        /// </summary>
        /// <remarks>
        /// The catalogue is a pure function of radius and band length, generated in a fixed order, so
        /// index 47 is the same three edges on every device in the match. That is what lets a whole turn
        /// travel as one integer instead of four peg coordinates - and why the index is validated here
        /// rather than trusted: it arrives from another machine.
        /// </remarks>
        /// <returns>True when the move was accepted and is now resolving.</returns>
        public bool SubmitBandById(int bandId)
        {
            if (State == null || Validator == null || board == null) return false;

            if (!State.AcceptsInput)
            {
                GameEvents.RaiseInvalidMove("Not your moment - the board is still resolving.");
                return false;
            }

            IReadOnlyList<BandPlacement> bands = board.Bands;
            if (bandId < 0 || bandId >= bands.Count)
            {
                Debug.LogError($"[Triggle] Band {bandId} is outside this board's catalogue of " +
                               $"{bands.Count}. The two devices are not playing the same board.", this);
                return false;
            }

            State.Phase = GamePhase.ValidatingMove;

            BandPlacement band = bands[bandId];
            if (!Validator.IsBandLegal(band, out string reason))
            {
                GameEvents.RaiseInvalidMove(reason);
                State.Phase = GamePhase.WaitingForInput;
                return false;
            }

            _moveRoutine = StartCoroutine(ResolveMoveRoutine(band));
            return true;
        }

        private void BeginTurn()
        {
            // A player with no legal band available ends the match; the catalogue is shared, so this
            // is a global condition rather than a per-seat one.
            if (!Validator.HasAnyLegalMove() || board.CountClaimedCells() >= board.TotalCells)
            {
                FinishGame();
                return;
            }

            State.Phase = GamePhase.WaitingForInput;
            GameEvents.RaiseTurnStarted(State.CurrentPlayer);
            Log($"Turn: {State.CurrentPlayer} (turn cycle {State.TurnCycleNumber}, " +
                $"{Validator.CountLegalMoves()} legal bands remain).");
        }

        private IEnumerator ResolveMoveRoutine(BandPlacement band)
        {
            PlayerId player = State.CurrentPlayer;

            // --- PlacingBand -------------------------------------------------
            State.Phase = GamePhase.PlacingBand;
            ApplyBand(band, player);
            State.RegisterMove();
            band.StackIndex = ComputeStackIndex(band);
            GameEvents.RaiseBandPlaced(player, band);
            Log($"{player} placed band #{band.Id}.");

            if (settings.bandPlacementDuration > 0f)
                yield return new WaitForSeconds(settings.bandPlacementDuration);

            // --- ResolvingClaims ---------------------------------------------
            State.Phase = GamePhase.ResolvingClaims;
            CollectAffectedCells(band, _affectedCells);

            int claimed = 0;
            for (int i = 0; i < _affectedCells.Count; i++)
            {
                TriangleCell cell = _affectedCells[i];
                if (cell.IsClaimed || !cell.IsFullyEnclosed) continue;

                cell.Owner = player;
                claimed++;
                GameEvents.RaiseCellClaimed(cell);
                Log($"{player} claimed cell #{cell.Id}.");

                if (settings.claimResolveDelay > 0f)
                    yield return new WaitForSeconds(settings.claimResolveDelay);
            }

            // --- CheckingEndGame ---------------------------------------------
            State.Phase = GamePhase.CheckingEndGame;

            bool boardExhausted = board.CountClaimedCells() >= board.TotalCells || !Validator.HasAnyLegalMove();
            if (boardExhausted)
            {
                _moveRoutine = null;
                FinishGame();
                yield break;
            }

            // --- NextTurn ----------------------------------------------------
            State.Phase = GamePhase.NextTurn;

            // The turn always passes, scoring or not. A single band can complete several triangles at
            // once, so granting an extra move on a claim let one player chain through most of the
            // board while the others never got a turn.
            State.AdvanceToNextPlayer();

            if (settings.turnHandoverDelay > 0f)
                yield return new WaitForSeconds(settings.turnHandoverDelay);

            _moveRoutine = null;
            BeginTurn();
        }

        /// <summary>Commits the band to the board model: marks it placed and activates its six edges.</summary>
        private void ApplyBand(BandPlacement band, PlayerId player)
        {
            band.IsPlaced = true;
            band.PlacedBy = player;

            for (int i = 0; i < band.Edges.Length; i++)
            {
                BoardEdge edge = band.Edges[i];
                edge.BandCount++;

                if (!edge.IsOccupied)
                {
                    edge.IsOccupied = true;
                    edge.FirstCoveredBy = player;
                }
            }
        }

        /// <summary>
        /// Highest existing stack depth among the band's edges, used by the renderer to lift
        /// overlapping bands and avoid Z-fighting. Computed after occupancy is applied, so the
        /// band's own contribution is excluded.
        /// </summary>
        private static int ComputeStackIndex(BandPlacement band)
        {
            int deepest = 0;
            for (int i = 0; i < band.Edges.Length; i++)
                deepest = Mathf.Max(deepest, band.Edges[i].BandCount - 1);

            return deepest;
        }

        /// <summary>
        /// Every cell whose enclosure status could have changed: those bordering one of the edges the
        /// band just activated. A straight band encloses no area of its own, so this is the complete
        /// set - at most two cells per covered edge, walked in run order so cascades read left to right.
        /// </summary>
        private void CollectAffectedCells(BandPlacement band, List<TriangleCell> result)
        {
            result.Clear();
            _affectedCellSet.Clear();

            for (int i = 0; i < band.Edges.Length; i++)
            {
                List<TriangleCell> neighbours = band.Edges[i].Cells;
                for (int c = 0; c < neighbours.Count; c++)
                {
                    TriangleCell cell = neighbours[c];
                    if (cell != null && _affectedCellSet.Add(cell)) result.Add(cell);
                }
            }
        }

        private void FinishGame()
        {
            State.Phase = GamePhase.GameOver;

            GameResult result = scoreManager != null
                ? scoreManager.BuildResult(State.TurnCycleNumber, board.CountClaimedCells(), board.TotalCells)
                : new GameResult(new List<PlayerId>(), new Dictionary<PlayerId, int>(),
                                 State.TurnCycleNumber, board.CountClaimedCells(), board.TotalCells);

            GameEvents.RaiseGameOver(result);

            if (result.Winners.Count == 0) Log("Game over - no winner recorded.");
            else if (result.IsDraw) Log($"Game over - draw between {result.Winners.Count} players.");
            else Log($"Game over - {result.Winners[0]} wins with {result.Scores[result.Winners[0]]} points.");
        }

        private void Log(string message)
        {
            if (verboseLogging) Debug.Log($"[Triggle] {message}", this);
        }

        private void OnValidate()
        {
            settings.Validate();
        }
    }
}
