using System.Collections.Generic;
using Triggle.Core;
using UnityEngine;

namespace Triggle.Gameplay
{
    /// <summary>
    /// Single source of truth for scores. Listens for claimed cells rather than being called by the
    /// flow controller, so scoring stays decoupled from turn orchestration.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScoreManager : MonoBehaviour
    {
        [Header("Scoring")]
        [Tooltip("Points awarded per enclosed unit triangle.")]
        [SerializeField, Min(1)] private int pointsPerCell = 1;

        private readonly Dictionary<PlayerId, int> _scores = new Dictionary<PlayerId, int>();
        private readonly List<PlayerId> _leaders = new List<PlayerId>(4);

        /// <summary>Read-only view of every seat's running total.</summary>
        public IReadOnlyDictionary<PlayerId, int> Scores => _scores;

        private void OnEnable()
        {
            GameEvents.OnCellClaimed += HandleCellClaimed;
        }

        private void OnDisable()
        {
            GameEvents.OnCellClaimed -= HandleCellClaimed;
        }

        /// <summary>Zeroes every seat listed and broadcasts the reset totals.</summary>
        public void Initialize(IReadOnlyList<PlayerId> players)
        {
            _scores.Clear();

            for (int i = 0; i < players.Count; i++)
            {
                _scores[players[i]] = 0;
                GameEvents.RaiseScoreChanged(players[i], 0);
            }
        }

        public int GetScore(PlayerId player) => _scores.TryGetValue(player, out int score) ? score : 0;

        /// <summary>Adds (or subtracts) points and broadcasts the new total.</summary>
        public void AddPoints(PlayerId player, int amount)
        {
            if (player == PlayerId.None || amount == 0) return;

            _scores.TryGetValue(player, out int current);
            int updated = current + amount;
            _scores[player] = updated;

            GameEvents.RaiseScoreChanged(player, updated);
        }

        /// <summary>Every seat tied for the highest score. One entry means an outright winner.</summary>
        public IReadOnlyList<PlayerId> GetLeaders()
        {
            _leaders.Clear();

            int best = int.MinValue;
            foreach (KeyValuePair<PlayerId, int> entry in _scores)
                if (entry.Value > best) best = entry.Value;

            if (best == int.MinValue) return _leaders;

            // Iterate the enum order so the result is deterministic regardless of dictionary layout.
            for (int seat = 1; seat <= 4; seat++)
            {
                var player = (PlayerId)seat;
                if (_scores.TryGetValue(player, out int score) && score == best) _leaders.Add(player);
            }

            return _leaders;
        }

        public int HighestScore()
        {
            int best = 0;
            foreach (KeyValuePair<PlayerId, int> entry in _scores)
                if (entry.Value > best) best = entry.Value;

            return best;
        }

        /// <summary>Packages the current standings into an immutable result snapshot.</summary>
        public GameResult BuildResult(int totalTurns, int claimedCells, int totalCells)
        {
            var winners = new List<PlayerId>(GetLeaders());
            var snapshot = new Dictionary<PlayerId, int>(_scores);
            return new GameResult(winners, snapshot, totalTurns, claimedCells, totalCells);
        }

        private void HandleCellClaimed(TriangleCell cell)
        {
            if (cell == null) return;
            AddPoints(cell.Owner, pointsPerCell);
        }
    }
}
