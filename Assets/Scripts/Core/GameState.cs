using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triggle.Core
{
    /// <summary>
    /// Inspector-authored rules bundle. Everything that changes how a match plays lives here so the
    /// flow controller stays free of magic numbers.
    /// </summary>
    [Serializable]
    public sealed class GameSettings
    {
        [Header("Players")]
        [Tooltip("Number of seats in play. Colours come from the PlayerColorPalette asset.")]
        [Range(2, 4)] public int playerCount = 2;

        [Header("Rules")]
        // The turn always passes to the next player, including on a scoring move. There is
        // deliberately no bonus/extra-turn rule: because a single band can complete several triangles
        // at once, chaining bonus moves let one player run the whole board in a single turn.
        //
        // Note: the number of pegs per band lives on BoardManager (it drives lattice generation),
        // not here. MoveValidator reads BoardManager.PegsPerBand.

        [Tooltip("A band is only legal if it covers at least one edge no other band covers yet. " +
                 "Prevents wasting turns on fully redundant placements and guarantees the game terminates.")]
        public bool requireAtLeastOneNewEdge = true;

        [Header("Pacing (seconds)")]
        [Tooltip("Time the band stretch animation is given before claims are resolved.")]
        [Min(0f)] public float bandPlacementDuration = 0.28f;

        [Tooltip("Delay between two consecutive triangle claims, so cascades read clearly.")]
        [Min(0f)] public float claimResolveDelay = 0.12f;

        [Tooltip("Delay before handing control to the next player.")]
        [Min(0f)] public float turnHandoverDelay = 0.15f;

        /// <summary>Clamps inspector values into a self-consistent range.</summary>
        public void Validate()
        {
            playerCount = Mathf.Clamp(playerCount, 2, 4);
            bandPlacementDuration = Mathf.Max(0f, bandPlacementDuration);
            claimResolveDelay = Mathf.Max(0f, claimResolveDelay);
            turnHandoverDelay = Mathf.Max(0f, turnHandoverDelay);
        }
    }

    /// <summary>
    /// Mutable runtime state of a match: whose turn it is, which phase the state machine sits in and
    /// the turn/move counters. Deliberately a plain C# object so it can be unit tested without a scene.
    /// </summary>
    public sealed class GameState
    {
        private readonly List<PlayerId> _activePlayers = new List<PlayerId>(4);
        private GamePhase _phase = GamePhase.Uninitialized;
        private int _seatIndex;

        public GameSettings Settings { get; }

        /// <summary>The seats in play, in turn order.</summary>
        public IReadOnlyList<PlayerId> ActivePlayers => _activePlayers;

        public PlayerId CurrentPlayer => _activePlayers.Count == 0 ? PlayerId.None : _activePlayers[_seatIndex];

        /// <summary>
        /// Full seat rotations completed plus one; increments when the seat wraps back to the first
        /// player. Distinct from a match "round" in a best-of series, which <c>MatchState</c> owns.
        /// </summary>
        public int TurnCycleNumber { get; private set; } = 1;

        /// <summary>Total number of bands successfully placed this match.</summary>
        public int MoveNumber { get; private set; }

        public bool IsGameOver => _phase == GamePhase.GameOver;

        /// <summary>Only true while the board is accepting peg clicks.</summary>
        public bool AcceptsInput => _phase == GamePhase.WaitingForInput;

        public GameState(GameSettings settings)
        {
            Settings = settings ?? new GameSettings();
            Settings.Validate();
            RebuildSeats();
        }

        /// <summary>Current phase. Assigning a new value raises <see cref="GameEvents.OnPhaseChanged"/>.</summary>
        public GamePhase Phase
        {
            get => _phase;
            set
            {
                if (_phase == value) return;
                _phase = value;
                GameEvents.RaisePhaseChanged(_phase);
            }
        }

        /// <summary>Rebuilds the seat list from <see cref="GameSettings.playerCount"/> and resets counters.</summary>
        public void Reset()
        {
            Settings.Validate();
            RebuildSeats();
            _seatIndex = 0;
            TurnCycleNumber = 1;
            MoveNumber = 0;
            _phase = GamePhase.Uninitialized;
        }

        private void RebuildSeats()
        {
            _activePlayers.Clear();
            for (int i = 0; i < Settings.playerCount; i++)
                _activePlayers.Add((PlayerId)(i + 1));
        }

        public void RegisterMove() => MoveNumber++;

        /// <summary>
        /// Hands control to the next seat. Called after every placement without exception - scoring a
        /// triangle never keeps the turn.
        /// </summary>
        public void AdvanceToNextPlayer()
        {
            if (_activePlayers.Count == 0) return;

            _seatIndex = (_seatIndex + 1) % _activePlayers.Count;
            if (_seatIndex == 0) TurnCycleNumber++;
        }

        public int SeatIndexOf(PlayerId player) => _activePlayers.IndexOf(player);
    }
}
