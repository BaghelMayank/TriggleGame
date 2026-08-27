using System.Collections.Generic;
using UnityEngine;

namespace Triggle.Core
{
    /// <summary>How a finished match reads to the local player.</summary>
    public enum MatchOutcome
    {
        /// <summary>The local player is the outright winner.</summary>
        Win,

        /// <summary>Someone else won outright.</summary>
        Lose,

        /// <summary>Two or more players are level on rounds won.</summary>
        Tie
    }

    /// <summary>Result of a single round: one board played until no legal band remained.</summary>
    public sealed class RoundResult
    {
        /// <summary>1-based index of the round that just finished.</summary>
        public readonly int RoundNumber;

        public readonly int TotalRounds;

        /// <summary>
        /// Players tied for the highest triangle count this round. More than one entry means the round
        /// was drawn, and no player is credited with winning it.
        /// </summary>
        public readonly IReadOnlyList<PlayerId> Winners;

        /// <summary>Triangle counts for this round only.</summary>
        public readonly IReadOnlyDictionary<PlayerId, int> Scores;

        /// <summary>Rounds won so far in the series, after this round was counted.</summary>
        public readonly IReadOnlyDictionary<PlayerId, int> RoundsWon;

        public bool IsDrawnRound => Winners.Count != 1;
        public bool IsFinalRound => RoundNumber >= TotalRounds;

        public RoundResult(int roundNumber, int totalRounds, IReadOnlyList<PlayerId> winners,
                           IReadOnlyDictionary<PlayerId, int> scores,
                           IReadOnlyDictionary<PlayerId, int> roundsWon)
        {
            RoundNumber = roundNumber;
            TotalRounds = totalRounds;
            Winners = winners;
            Scores = scores;
            RoundsWon = roundsWon;
        }
    }

    /// <summary>Final standings of a best-of-N series.</summary>
    public sealed class MatchResult
    {
        public readonly int TotalRounds;

        /// <summary>Players tied on rounds won. More than one entry means the match is tied.</summary>
        public readonly IReadOnlyList<PlayerId> Winners;

        /// <summary>Rounds won per player.</summary>
        public readonly IReadOnlyDictionary<PlayerId, int> RoundsWon;

        /// <summary>Total triangles claimed across every round, used as the display score.</summary>
        public readonly IReadOnlyDictionary<PlayerId, int> TotalScores;

        /// <summary>How the panel should present this: win, lose or tie.</summary>
        public readonly MatchOutcome Outcome;

        public bool IsTie => Outcome == MatchOutcome.Tie;

        public MatchResult(int totalRounds, IReadOnlyList<PlayerId> winners,
                           IReadOnlyDictionary<PlayerId, int> roundsWon,
                           IReadOnlyDictionary<PlayerId, int> totalScores,
                           MatchOutcome outcome)
        {
            TotalRounds = totalRounds;
            Winners = winners;
            RoundsWon = roundsWon;
            TotalScores = totalScores;
            Outcome = outcome;
        }
    }

    /// <summary>
    /// Tracks a best-of-N series: how many rounds were chosen, which round is being played, and how
    /// many rounds each player has won.
    /// </summary>
    /// <remarks>
    /// A "round" is one complete board, played until no legal band remains. Triangle scores reset every
    /// round; rounds won accumulate across the series and decide the match.
    /// <para>
    /// A drawn round credits nobody. That keeps the series score honest - awarding the point to every
    /// tied player would let a 2-player draw advance both, which cannot break a tie.
    /// </para>
    /// </remarks>
    public sealed class MatchState
    {
        private readonly Dictionary<PlayerId, int> _roundsWon = new Dictionary<PlayerId, int>();
        private readonly Dictionary<PlayerId, int> _totalScores = new Dictionary<PlayerId, int>();
        private readonly List<PlayerId> _leaders = new List<PlayerId>(4);

        /// <summary>Smallest and largest series length offered in the lobby.</summary>
        public const int MinRounds = 1;
        public const int MaxRounds = 10;

        public int TotalRounds { get; private set; } = 1;

        /// <summary>1-based index of the round in progress.</summary>
        public int CurrentRound { get; private set; } = 1;

        public IReadOnlyDictionary<PlayerId, int> RoundsWon => _roundsWon;
        public IReadOnlyDictionary<PlayerId, int> TotalScores => _totalScores;

        /// <summary>True when the round in progress is the last of the series.</summary>
        public bool IsFinalRound => CurrentRound >= TotalRounds;

        /// <summary>
        /// False for a single-round match, so the HUD can hide the round counter entirely rather than
        /// showing a pointless "1/1".
        /// </summary>
        public bool ShowRoundCounter => TotalRounds > 1;

        /// <summary>Starts a fresh series.</summary>
        public void StartMatch(int totalRounds, IReadOnlyList<PlayerId> players)
        {
            TotalRounds = Mathf.Clamp(totalRounds, MinRounds, MaxRounds);
            CurrentRound = 1;

            _roundsWon.Clear();
            _totalScores.Clear();

            for (int i = 0; i < players.Count; i++)
            {
                _roundsWon[players[i]] = 0;
                _totalScores[players[i]] = 0;
            }
        }

        /// <summary>
        /// Records the outcome of the round just played. Credits a round win only when there is a single
        /// leader, and accumulates triangle scores into the series totals.
        /// </summary>
        public void RegisterRoundResult(GameResult roundScores)
        {
            if (roundScores == null) return;

            foreach (KeyValuePair<PlayerId, int> entry in roundScores.Scores)
            {
                _totalScores.TryGetValue(entry.Key, out int running);
                _totalScores[entry.Key] = running + entry.Value;
            }

            if (roundScores.Winners.Count != 1) return;   // drawn round credits nobody

            PlayerId winner = roundScores.Winners[0];
            _roundsWon.TryGetValue(winner, out int won);
            _roundsWon[winner] = won + 1;
        }

        /// <summary>Advances to the next round. Returns false when the series is already complete.</summary>
        public bool AdvanceRound()
        {
            if (IsFinalRound) return false;

            CurrentRound++;
            return true;
        }

        /// <summary>Players tied on rounds won. Deterministic order (seat order).</summary>
        public IReadOnlyList<PlayerId> GetMatchLeaders()
        {
            _leaders.Clear();

            int best = int.MinValue;
            foreach (KeyValuePair<PlayerId, int> entry in _roundsWon)
                if (entry.Value > best) best = entry.Value;

            if (best == int.MinValue) return _leaders;

            for (int seat = 1; seat <= 4; seat++)
            {
                var player = (PlayerId)seat;
                if (_roundsWon.TryGetValue(player, out int won) && won == best) _leaders.Add(player);
            }

            return _leaders;
        }

        /// <summary>
        /// Packages the final standings. <paramref name="localPlayer"/> decides whether the panel reads
        /// as a win or a loss; for hot-seat play that is Player 1 by convention.
        /// </summary>
        public MatchResult BuildResult(PlayerId localPlayer)
        {
            var winners = new List<PlayerId>(GetMatchLeaders());

            MatchOutcome outcome;
            if (winners.Count != 1) outcome = MatchOutcome.Tie;
            else if (winners[0] == localPlayer) outcome = MatchOutcome.Win;
            else outcome = MatchOutcome.Lose;

            return new MatchResult(
                TotalRounds,
                winners,
                new Dictionary<PlayerId, int>(_roundsWon),
                new Dictionary<PlayerId, int>(_totalScores),
                outcome);
        }

        public int GetRoundsWon(PlayerId player) => _roundsWon.TryGetValue(player, out int won) ? won : 0;

        public int GetTotalScore(PlayerId player) => _totalScores.TryGetValue(player, out int s) ? s : 0;
    }
}
