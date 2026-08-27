using System.Collections.Generic;
using Triggle.Core;
using Triggle.Grid;
using UnityEngine;

namespace Triggle.Gameplay
{
    /// <summary>
    /// Picks a band for a computer seat. Pure C# - no scene, no coroutines - so it can be exercised
    /// from a test harness as well as from <see cref="AiController"/>.
    /// </summary>
    /// <remarks>
    /// <b>The shape of the game.</b> A triangle's three edges each run in a different lattice direction,
    /// so one straight band can only ever cover one of them. Every triangle therefore needs three
    /// separate bands, and the third one scores. That makes Triggle a game about <i>who is forced to
    /// play the second edge</i>: covering a triangle's second edge leaves it with one edge open, and the
    /// next player takes it for free. Everything below is built around counting those gifts.
    /// <para>
    /// <b>Simulation without mutation.</b> Scoring a candidate means asking what the board would look
    /// like with its edges covered. Rather than writing to <see cref="BoardEdge.IsOccupied"/> and undoing
    /// it - which would corrupt the live board if an exception unwound mid-search - a scratch set of
    /// "virtually covered" edges is layered over the real occupancy, and every query consults both. The
    /// board is never touched.
    /// </para>
    /// </remarks>
    public sealed class BandEvaluator
    {
        /// <summary>Candidates carried from the 1-ply pass into the (more expensive) 2-ply pass.</summary>
        private const int HardCandidateCount = 10;

        /// <summary>Chance an Easy seat ignores its own analysis and plays at random.</summary>
        private const float EasyBlunderChance = 0.6f;

        private readonly BoardManager _board;
        private readonly GameSettings _settings;

        /// <summary>Edges treated as covered for the duration of a hypothetical move.</summary>
        private readonly HashSet<BoardEdge> _virtual = new HashSet<BoardEdge>();

        // Two levels of scratch state: the outer candidate, and the opponent's reply to it.
        private readonly List<BoardEdge> _addedOuter = new List<BoardEdge>(4);
        private readonly List<BoardEdge> _addedInner = new List<BoardEdge>(4);
        private readonly HashSet<TriangleCell> _touchedOuter = new HashSet<TriangleCell>();
        private readonly HashSet<TriangleCell> _touchedInner = new HashSet<TriangleCell>();

        private readonly List<BandPlacement> _legal = new List<BandPlacement>(200);
        private readonly List<Candidate> _scored = new List<Candidate>(200);

        /// <summary>A band with its heuristic score and the raw counts behind it.</summary>
        private struct Candidate
        {
            public BandPlacement Band;
            public float Score;
            public int Gain;
            public int Gifts;
        }

        public BandEvaluator(BoardManager board, GameSettings settings)
        {
            _board = board;
            _settings = settings ?? new GameSettings();
        }

        /// <summary>
        /// Chooses a band to play, or null when the board has no legal move left (in which case the
        /// flow controller is about to end the round anyway).
        /// </summary>
        public BandPlacement ChooseBand(AiDifficulty difficulty)
        {
            if (_board == null || !_board.IsBuilt) return null;

            _virtual.Clear();
            CollectLegalBands(_legal);
            if (_legal.Count == 0) return null;

            // Easy throws most moves away. It still runs the analysis the rest of the time, so it takes
            // a triangle that is sitting there rather than looking broken.
            if (difficulty == AiDifficulty.Easy && Random.value < EasyBlunderChance)
                return _legal[Random.Range(0, _legal.Count)];

            ScoreCandidates(_legal, _scored);
            if (_scored.Count == 0) return _legal[Random.Range(0, _legal.Count)];

            return difficulty == AiDifficulty.Hard
                ? PickWithLookahead(_scored)
                : PickBest(_scored);
        }

        // ------------------------------------------------------------------ scoring

        /// <summary>
        /// One-ply score for every legal band.
        /// </summary>
        /// <remarks>
        /// The weights encode a single trade: a triangle you take is worth slightly more than a triangle
        /// you hand over. Taking has to stay attractive, because late in a round every remaining move
        /// gives something away and an AI that valued them equally would dither instead of cashing in.
        /// </remarks>
        private void ScoreCandidates(List<BandPlacement> bands, List<Candidate> result)
        {
            result.Clear();

            for (int i = 0; i < bands.Count; i++)
            {
                BandPlacement band = bands[i];
                Simulate(band, out int gain, out int gifts, out int setups);

                // Jitter breaks ties randomly, so the AI does not replay the same opening every match.
                float score = gain * 10f - gifts * 3f - setups * 0.4f + Random.value * 0.05f;

                result.Add(new Candidate { Band = band, Score = score, Gain = gain, Gifts = gifts });
            }
        }

        private static BandPlacement PickBest(List<Candidate> candidates)
        {
            int best = 0;
            for (int i = 1; i < candidates.Count; i++)
                if (candidates[i].Score > candidates[best].Score) best = i;

            return candidates[best].Band;
        }

        /// <summary>
        /// Re-scores the strongest one-ply candidates by what the opponent could take in reply.
        /// </summary>
        /// <remarks>
        /// Only the top few are examined. The reply search is O(bands) on its own, so re-running it for
        /// all 174 candidates at radius 5 would be 30,000 simulations for a decision that the one-ply
        /// pass has already narrowed to a handful of genuinely different moves.
        /// <para>
        /// The reply term is weighted just below the gain term for the same reason as above: when every
        /// move concedes something, the AI should still prefer the one that concedes least rather than
        /// treating them all as equally lost.
        /// </para>
        /// </remarks>
        private BandPlacement PickWithLookahead(List<Candidate> candidates)
        {
            candidates.Sort(CompareByScoreDescending);

            int examined = Mathf.Min(HardCandidateCount, candidates.Count);
            BandPlacement best = candidates[0].Band;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < examined; i++)
            {
                Candidate candidate = candidates[i];

                ApplyVirtual(candidate.Band, _addedOuter);
                int replyGain = BestReplyGain(candidate.Band);
                RevertVirtual(_addedOuter);

                float score = candidate.Gain * 10f - replyGain * 9f - candidate.Gifts * 0.5f
                              + Random.value * 0.05f;

                if (score <= bestScore) continue;

                bestScore = score;
                best = candidate.Band;
            }

            return best;
        }

        private static int CompareByScoreDescending(Candidate a, Candidate b) => b.Score.CompareTo(a.Score);

        /// <summary>
        /// Most triangles the opponent could claim in a single move, given the current virtual state.
        /// </summary>
        /// <param name="justPlayed">
        /// The candidate whose edges are already in the virtual set. It is excluded because the flow
        /// controller marks a band placed the moment it resolves, so it can never be replayed.
        /// </param>
        private int BestReplyGain(BandPlacement justPlayed)
        {
            IReadOnlyList<BandPlacement> all = _board.Bands;
            int best = 0;

            for (int i = 0; i < all.Count; i++)
            {
                BandPlacement reply = all[i];
                if (reply == justPlayed || !IsPlayable(reply)) continue;

                ApplyVirtual(reply, _addedInner);
                int gain = CountNewlyEnclosed(_addedInner, _touchedInner);
                RevertVirtual(_addedInner);

                if (gain > best) best = gain;
            }

            return best;
        }

        /// <summary>
        /// Counts what a band would do to the board: triangles it closes, triangles it leaves one edge
        /// from closing (a gift to the next player), and triangles it leaves two edges from closing.
        /// </summary>
        private void Simulate(BandPlacement band, out int gain, out int gifts, out int setups)
        {
            gain = 0;
            gifts = 0;
            setups = 0;

            ApplyVirtual(band, _addedOuter);

            CollectTouchedCells(_addedOuter, _touchedOuter);
            foreach (TriangleCell cell in _touchedOuter)
            {
                if (cell.IsClaimed) continue;

                switch (OpenEdgeCount(cell))
                {
                    case 0: gain++; break;
                    case 1: gifts++; break;
                    case 2: setups++; break;
                }
            }

            RevertVirtual(_addedOuter);
        }

        private int CountNewlyEnclosed(List<BoardEdge> covered, HashSet<TriangleCell> scratch)
        {
            CollectTouchedCells(covered, scratch);

            int enclosed = 0;
            foreach (TriangleCell cell in scratch)
                if (!cell.IsClaimed && OpenEdgeCount(cell) == 0) enclosed++;

            return enclosed;
        }

        // ------------------------------------------------------------------ virtual board state

        /// <summary>
        /// Marks the band's still-open edges as covered, recording exactly which ones were added.
        /// </summary>
        /// <remarks>
        /// The record matters for the nested case: a reply band can share an edge with the candidate
        /// already in the virtual set. Reverting by iterating the band's own edges would then remove an
        /// edge the outer level still owns, quietly un-covering it mid-search.
        /// </remarks>
        private void ApplyVirtual(BandPlacement band, List<BoardEdge> added)
        {
            added.Clear();

            for (int i = 0; i < band.Edges.Length; i++)
            {
                BoardEdge edge = band.Edges[i];
                if (!edge.IsOccupied && _virtual.Add(edge)) added.Add(edge);
            }
        }

        private void RevertVirtual(List<BoardEdge> added)
        {
            for (int i = 0; i < added.Count; i++) _virtual.Remove(added[i]);
            added.Clear();
        }

        /// <summary>Boundary edges of a cell that no band - real or hypothetical - covers yet.</summary>
        private int OpenEdgeCount(TriangleCell cell)
        {
            int open = 0;

            for (int i = 0; i < 3; i++)
            {
                BoardEdge edge = cell.Edges[i];
                if (!edge.IsOccupied && !_virtual.Contains(edge)) open++;
            }

            return open;
        }

        /// <summary>
        /// Cells whose enclosure status could have changed: those bordering a newly covered edge.
        /// A straight band encloses no area of its own, so this is the complete set.
        /// </summary>
        private static void CollectTouchedCells(List<BoardEdge> covered, HashSet<TriangleCell> result)
        {
            result.Clear();

            for (int i = 0; i < covered.Count; i++)
            {
                List<TriangleCell> cells = covered[i].Cells;
                for (int c = 0; c < cells.Count; c++)
                    if (cells[c] != null) result.Add(cells[c]);
            }
        }

        // ------------------------------------------------------------------ legality

        private void CollectLegalBands(List<BandPlacement> result)
        {
            result.Clear();

            IReadOnlyList<BandPlacement> all = _board.Bands;
            for (int i = 0; i < all.Count; i++)
                if (IsPlayable(all[i])) result.Add(all[i]);
        }

        /// <summary>
        /// Legality under the virtual state. With an empty virtual set this matches
        /// <see cref="MoveValidator.IsBandLegal"/> exactly; the two must stay in step, or the AI can
        /// pick a move the flow controller then refuses.
        /// </summary>
        private bool IsPlayable(BandPlacement band)
        {
            if (band == null || band.IsPlaced) return false;
            if (!_settings.requireAtLeastOneNewEdge) return true;

            for (int i = 0; i < band.Edges.Length; i++)
            {
                BoardEdge edge = band.Edges[i];
                if (!edge.IsOccupied && !_virtual.Contains(edge)) return true;
            }

            return false;
        }
    }
}
