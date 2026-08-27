using System.Collections.Generic;
using Triggle.Core;
using Triggle.Grid;
using UnityEngine;

namespace Triggle.Gameplay
{
    /// <summary>
    /// Pure geometric rules engine. Decides whether a peg selection describes a legal rubber band and
    /// which pegs may legally extend a partial selection.
    /// </summary>
    /// <remarks>
    /// A rubber band cannot be bent, so a legal placement is a <b>straight run of collinear pegs</b>
    /// along one of the three lattice line directions. With the standard rule the player picks four
    /// pegs in a line and the band covers the three unit edges between them.
    /// <para>
    /// The validator never searches geometry at runtime: <see cref="BoardManager"/> has already
    /// enumerated every straight run that fits inside the hexagon, so validation reduces to set
    /// intersection over that catalogue. Four collinear pegs determine exactly one run, so a complete
    /// selection resolves to a unique band.
    /// </para>
    /// </remarks>
    public sealed class MoveValidator
    {
        private readonly BoardManager _board;
        private readonly GameSettings _settings;

        private readonly List<BandPlacement> _candidates = new List<BandPlacement>(16);
        private readonly List<Peg> _nextPegs = new List<Peg>(12);
        private readonly HashSet<Peg> _selectionSet = new HashSet<Peg>();
        private readonly HashSet<Peg> _nextPegSet = new HashSet<Peg>();

        public MoveValidator(BoardManager board, GameSettings settings)
        {
            _board = board;
            _settings = settings;
        }

        /// <summary>Collinear pegs a complete selection must contain. Owned by the board.</summary>
        public int RequiredPegCount => _board.PegsPerBand;

        /// <summary>Unit edges a placed band covers.</summary>
        public int EdgesPerBand => RequiredPegCount - 1;

        /// <summary>
        /// Resolves a complete peg selection into the single band it describes.
        /// </summary>
        /// <param name="selection">Pegs in click order; order is irrelevant to validity.</param>
        /// <param name="band">The resolved band slot, or null on failure.</param>
        /// <param name="failureReason">Player-facing explanation, valid only when the call returns false.</param>
        public bool TryResolveBand(IReadOnlyList<Peg> selection, out BandPlacement band, out string failureReason)
        {
            band = null;

            if (selection == null || selection.Count == 0)
            {
                failureReason = "No pegs selected.";
                return false;
            }

            int required = RequiredPegCount;
            if (selection.Count != required)
            {
                failureReason = $"Select exactly {required} pegs in a straight line " +
                                $"(you selected {selection.Count}).";
                return false;
            }

            _selectionSet.Clear();
            foreach (Peg peg in selection)
            {
                if (peg == null)
                {
                    failureReason = "Selection contained an empty slot.";
                    return false;
                }
                if (!_selectionSet.Add(peg))
                {
                    failureReason = "The same peg was selected twice.";
                    return false;
                }
            }

            // Explicit geometric check first: it produces a far better message than a failed catalogue
            // lookup can ("collinear but with a gap" reads very differently from "not in a line").
            if (!IsStraightRun(selection, out string geometryError))
            {
                failureReason = geometryError;
                return false;
            }

            CollectCandidateBands(selection, _candidates);
            if (_candidates.Count == 0)
            {
                failureReason = "That run of pegs is not fully on the board.";
                return false;
            }

            string lastLegalityFailure = null;

            // A run of `required` collinear pegs is unique, so at most one candidate can match.
            foreach (BandPlacement candidate in _candidates)
            {
                if (candidate.Pegs.Length != required) continue;

                if (!IsBandLegal(candidate, out string reason))
                {
                    lastLegalityFailure = reason;
                    continue;
                }

                band = candidate;
                failureReason = null;
                return true;
            }

            failureReason = lastLegalityFailure ?? "That band cannot be played.";
            return false;
        }

        /// <summary>
        /// True when the pegs form a gap-free straight run along one lattice line, in any click order.
        /// </summary>
        /// <remarks>
        /// Each of the three lattice axes is tested by projecting every peg onto a (line, position)
        /// pair: all pegs must share the line, and their positions must form consecutive integers.
        /// </remarks>
        public static bool IsStraightRun(IReadOnlyList<Peg> pegs, out string failureReason)
        {
            failureReason = null;
            if (pegs == null || pegs.Count < 2) return true;

            for (int axis = 0; axis < 3; axis++)
            {
                int line = LineKey(pegs[0].Coord, axis);
                int min = int.MaxValue;
                int max = int.MinValue;
                bool sameLine = true;

                for (int i = 0; i < pegs.Count && sameLine; i++)
                {
                    if (LineKey(pegs[i].Coord, axis) != line) { sameLine = false; break; }

                    int position = PositionOnLine(pegs[i].Coord, axis);
                    if (position < min) min = position;
                    if (position > max) max = position;
                }

                if (!sameLine) continue;

                // Distinct pegs on one line: consecutive exactly when the span matches the count.
                if (max - min == pegs.Count - 1) return true;

                failureReason = "Those pegs are in a straight line but not adjacent - " +
                                "a band covers consecutive pegs with no gaps.";
                return false;
            }

            failureReason = "A rubber band cannot be bent - pick pegs in a single straight line.";
            return false;
        }

        /// <summary>Identifies which line of the given axis a coordinate sits on.</summary>
        private static int LineKey(Vector2Int coord, int axis) => axis switch
        {
            0 => coord.y,                // axis (1, 0):  constant r
            1 => coord.x + coord.y,      // axis (1,-1):  constant q + r
            _ => coord.x                 // axis (0, 1):  constant q
        };

        /// <summary>Position of a coordinate along its line, for the given axis.</summary>
        private static int PositionOnLine(Vector2Int coord, int axis) => axis switch
        {
            0 => coord.x,
            1 => coord.x,
            _ => coord.y
        };

        /// <summary>
        /// Rule check independent of geometry: the exact band must be unplaced, and (when configured)
        /// must contribute at least one edge no other band covers yet.
        /// </summary>
        public bool IsBandLegal(BandPlacement band, out string reason)
        {
            if (band == null)
            {
                reason = "Band is null.";
                return false;
            }

            if (band.IsPlaced)
            {
                reason = "A band already occupies that exact line.";
                return false;
            }

            if (_settings.requireAtLeastOneNewEdge && !band.HasUncoveredEdge())
            {
                reason = "Every segment there is already covered - that band would add nothing.";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Pegs that may legally be added to <paramref name="selection"/> without making a valid band
        /// unreachable. Used to drive selectable-peg highlighting.
        /// </summary>
        /// <returns>
        /// A buffer owned by this validator; copy it if you need to keep the contents across calls.
        /// </returns>
        public IReadOnlyList<Peg> GetValidNextPegs(IReadOnlyList<Peg> selection)
        {
            _nextPegs.Clear();
            _nextPegSet.Clear();

            int freeSlots = RequiredPegCount - (selection?.Count ?? 0);
            if (freeSlots <= 0) return _nextPegs;

            _selectionSet.Clear();
            if (selection != null)
            {
                foreach (Peg peg in selection)
                    if (peg != null) _selectionSet.Add(peg);
            }

            CollectCandidateBands(selection, _candidates);

            // Any unselected peg of a still-playable candidate run is a legal continuation: the run is
            // already gap-free by construction, so no ordering or adjacency check is needed here.
            foreach (BandPlacement band in _candidates)
            {
                if (!IsBandLegal(band, out _)) continue;

                for (int i = 0; i < band.Pegs.Length; i++)
                {
                    Peg peg = band.Pegs[i];
                    if (_selectionSet.Contains(peg)) continue;
                    if (_nextPegSet.Add(peg)) _nextPegs.Add(peg);
                }
            }

            return _nextPegs;
        }

        /// <summary>
        /// True when the partial selection narrows down to exactly one legal band, which lets the
        /// preview renderer ghost the full run before the last peg is clicked.
        /// </summary>
        public bool TryGetUniqueCandidate(IReadOnlyList<Peg> selection, out BandPlacement band)
        {
            band = null;
            if (selection == null || selection.Count == 0) return false;

            CollectCandidateBands(selection, _candidates);

            foreach (BandPlacement candidate in _candidates)
            {
                if (!IsBandLegal(candidate, out _)) continue;

                if (band != null) { band = null; return false; }   // ambiguous
                band = candidate;
            }

            return band != null;
        }

        /// <summary>True while at least one band can still be played by anyone.</summary>
        public bool HasAnyLegalMove()
        {
            IReadOnlyList<BandPlacement> bands = _board.Bands;
            for (int i = 0; i < bands.Count; i++)
                if (IsBandLegal(bands[i], out _)) return true;

            return false;
        }

        /// <summary>Number of distinct bands still playable. Handy for UI and for AI heuristics.</summary>
        public int CountLegalMoves()
        {
            int count = 0;
            IReadOnlyList<BandPlacement> bands = _board.Bands;
            for (int i = 0; i < bands.Count; i++)
                if (IsBandLegal(bands[i], out _)) count++;

            return count;
        }

        /// <summary>The unit edges a band would activate.</summary>
        public IReadOnlyList<BoardEdge> GetSpannedEdges(BandPlacement band) => band.Edges;

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Every band whose run contains all selected pegs. With an empty selection this is the whole
        /// catalogue; otherwise it starts from the first peg's band list (at most 3 axes x band length
        /// entries) and filters, so the cost is independent of board size.
        /// </summary>
        private void CollectCandidateBands(IReadOnlyList<Peg> selection, List<BandPlacement> result)
        {
            result.Clear();

            if (selection == null || selection.Count == 0)
            {
                IReadOnlyList<BandPlacement> all = _board.Bands;
                for (int i = 0; i < all.Count; i++) result.Add(all[i]);
                return;
            }

            Peg seed = null;
            for (int i = 0; i < selection.Count && seed == null; i++) seed = selection[i];
            if (seed == null) return;

            foreach (BandPlacement band in seed.Bands)
            {
                bool containsAll = true;
                for (int i = 0; i < selection.Count; i++)
                {
                    Peg peg = selection[i];
                    if (peg == null || band.ContainsPeg(peg)) continue;
                    containsAll = false;
                    break;
                }

                if (containsAll) result.Add(band);
            }
        }
    }
}
