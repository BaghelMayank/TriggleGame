using System;
using System.Collections.Generic;

namespace Triggle.Core
{
    /// <summary>
    /// Global, strongly-typed event bus. Systems never reference each other directly: producers call
    /// the <c>Raise*</c> helpers, consumers subscribe in <c>OnEnable</c> and unsubscribe in <c>OnDisable</c>.
    /// </summary>
    /// <remarks>
    /// These are static and therefore survive scene loads. Every subscriber in this project unsubscribes
    /// symmetrically; <see cref="ClearAllSubscribers"/> exists as a hard reset for editor iteration.
    /// </remarks>
    public static class GameEvents
    {
        /// <summary>Raised after <c>BoardManager</c> finished generating pegs, edges, cells and band slots.</summary>
        public static event Action OnBoardGenerated;

        /// <summary>Raised on every state machine transition.</summary>
        public static event Action<GamePhase> OnPhaseChanged;

        /// <summary>Raised when a player gains control (normal turn or bonus move).</summary>
        public static event Action<PlayerId> OnTurnStarted;

        /// <summary>Raised once a validated band has been committed to the board model.</summary>
        public static event Action<PlayerId, BandPlacement> OnBandPlaced;

        /// <summary>Raised once per newly enclosed cell, in resolve order.</summary>
        public static event Action<TriangleCell> OnCellClaimed;

        /// <summary>Raised whenever a player's score changes. Payload: player, new total.</summary>
        public static event Action<PlayerId, int> OnScoreChanged;

        /// <summary>Raised whenever the peg selection buffer changes (add, remove, clear).</summary>
        public static event Action<IReadOnlyList<Peg>> OnSelectionChanged;

        /// <summary>Raised when the pointer starts/stops hovering a peg. Payload may be null.</summary>
        public static event Action<Peg> OnPegHovered;

        /// <summary>Raised when a submitted selection failed validation. Payload: human readable reason.</summary>
        public static event Action<string> OnInvalidMove;

        /// <summary>Raised when a UI control is activated, so audio can react without a direct reference.</summary>
        public static event Action OnUiClick;

        /// <summary>
        /// Raised when one round (a single board) is exhausted. In a multi-round match this fires once
        /// per round; <see cref="OnMatchCompleted"/> fires only after the last one.
        /// </summary>
        public static event Action<GameResult> OnGameOver;

        /// <summary>Raised after a round's result has been folded into the series standings.</summary>
        public static event Action<RoundResult> OnRoundCompleted;

        /// <summary>Raised once when the whole best-of-N series is decided.</summary>
        public static event Action<MatchResult> OnMatchCompleted;

        /// <summary>Raised when a new round of an ongoing match begins. Payload: 1-based round number.</summary>
        public static event Action<int> OnRoundStarted;

        /// <summary>Raised immediately before the board and all runtime state are torn down.</summary>
        public static event Action OnGameReset;

        /// <summary>
        /// Raised after the camera rig has repositioned the camera to fit the board. Anything caching
        /// the camera's resting position must re-read it here.
        /// </summary>
        public static event Action OnCameraReframed;

        public static void RaiseBoardGenerated() => OnBoardGenerated?.Invoke();
        public static void RaisePhaseChanged(GamePhase phase) => OnPhaseChanged?.Invoke(phase);
        public static void RaiseTurnStarted(PlayerId player) => OnTurnStarted?.Invoke(player);
        public static void RaiseBandPlaced(PlayerId player, BandPlacement band) => OnBandPlaced?.Invoke(player, band);
        public static void RaiseCellClaimed(TriangleCell cell) => OnCellClaimed?.Invoke(cell);
        public static void RaiseScoreChanged(PlayerId player, int total) => OnScoreChanged?.Invoke(player, total);
        public static void RaiseSelectionChanged(IReadOnlyList<Peg> selection) => OnSelectionChanged?.Invoke(selection);
        public static void RaisePegHovered(Peg peg) => OnPegHovered?.Invoke(peg);
        public static void RaiseInvalidMove(string reason) => OnInvalidMove?.Invoke(reason);
        public static void RaiseUiClick() => OnUiClick?.Invoke();
        public static void RaiseGameOver(GameResult result) => OnGameOver?.Invoke(result);
        public static void RaiseRoundCompleted(RoundResult result) => OnRoundCompleted?.Invoke(result);
        public static void RaiseMatchCompleted(MatchResult result) => OnMatchCompleted?.Invoke(result);
        public static void RaiseRoundStarted(int roundNumber) => OnRoundStarted?.Invoke(roundNumber);
        public static void RaiseGameReset() => OnGameReset?.Invoke();
        public static void RaiseCameraReframed() => OnCameraReframed?.Invoke();

        /// <summary>
        /// Drops every subscriber. Only needed when domain reload is disabled ("Enter Play Mode Options"),
        /// where static delegates would otherwise leak destroyed MonoBehaviours between play sessions.
        /// </summary>
        public static void ClearAllSubscribers()
        {
            OnBoardGenerated = null;
            OnPhaseChanged = null;
            OnTurnStarted = null;
            OnBandPlaced = null;
            OnCellClaimed = null;
            OnScoreChanged = null;
            OnSelectionChanged = null;
            OnPegHovered = null;
            OnInvalidMove = null;
            OnUiClick = null;
            OnGameOver = null;
            OnRoundCompleted = null;
            OnMatchCompleted = null;
            OnRoundStarted = null;
            OnGameReset = null;
            OnCameraReframed = null;
        }
    }
}
