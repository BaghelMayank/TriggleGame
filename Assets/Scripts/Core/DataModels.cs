using System.Collections.Generic;
using UnityEngine;

namespace Triggle.Core
{
    /// <summary>Identifies a seat at the table. <see cref="None"/> means "unowned / neutral".</summary>
    public enum PlayerId
    {
        None = 0,
        Player1 = 1,
        Player2 = 2,
        Player3 = 3,
        Player4 = 4
    }

    /// <summary>A unit triangle in a triangular lattice either points toward +Z ("up") or -Z ("down").</summary>
    public enum CellOrientation
    {
        Up,
        Down
    }

    /// <summary>The <see cref="GameFlowController"/> state machine vocabulary.</summary>
    public enum GamePhase
    {
        Uninitialized,
        WaitingForInput,
        ValidatingMove,
        PlacingBand,
        ResolvingClaims,
        CheckingEndGame,
        NextTurn,
        GameOver
    }

    /// <summary>
    /// A physical post of the board, addressed by axial coordinates <c>(q, r)</c>.
    /// Pegs are pure data; their visual representation lives on <see cref="View"/>.
    /// </summary>
    public sealed class Peg
    {
        public readonly int Id;
        public readonly Vector2Int Coord;
        public readonly Vector3 WorldPosition;

        /// <summary>Up to six lattice neighbours at axial distance 1.</summary>
        public readonly List<Peg> Neighbours = new List<Peg>(6);

        /// <summary>Every unit edge that terminates on this peg.</summary>
        public readonly List<BoardEdge> IncidentEdges = new List<BoardEdge>(6);

        /// <summary>Every legal band placement whose perimeter passes through this peg.</summary>
        public readonly List<BandPlacement> Bands = new List<BandPlacement>(12);

        /// <summary>Transform of the spawned peg GameObject (null in headless / unit-test usage).</summary>
        public Transform View;

        public int Q => Coord.x;
        public int R => Coord.y;

        public Peg(int id, Vector2Int coord, Vector3 worldPosition)
        {
            Id = id;
            Coord = coord;
            WorldPosition = worldPosition;
        }

        public override string ToString() => $"Peg#{Id}({Coord.x},{Coord.y})";
    }

    /// <summary>
    /// An undirected unit-length segment between two adjacent pegs. Edges are shared: a single edge
    /// may be covered by several rubber bands, which is what <see cref="BandCount"/> tracks.
    /// </summary>
    public sealed class BoardEdge
    {
        public readonly int Id;
        public readonly Peg A;
        public readonly Peg B;

        /// <summary>Every unit cell that uses this edge as one of its three boundaries (1 or 2).</summary>
        public readonly List<TriangleCell> Cells = new List<TriangleCell>(2);

        /// <summary>True as soon as at least one rubber band covers this edge.</summary>
        public bool IsOccupied;

        /// <summary>Number of distinct bands stacked on this edge (used for Y-offset anti Z-fighting).</summary>
        public int BandCount;

        /// <summary>The player whose band first covered this edge.</summary>
        public PlayerId FirstCoveredBy = PlayerId.None;

        public Vector3 Midpoint => (A.WorldPosition + B.WorldPosition) * 0.5f;
        public Vector3 Direction => (B.WorldPosition - A.WorldPosition).normalized;
        public float Length => Vector3.Distance(A.WorldPosition, B.WorldPosition);

        public BoardEdge(int id, Peg a, Peg b)
        {
            Id = id;
            // Canonical ordering keeps equality / hashing stable regardless of discovery order.
            if (a.Id <= b.Id) { A = a; B = b; }
            else { A = b; B = a; }
        }

        /// <summary>Stable 64-bit key for a peg pair, independent of argument order.</summary>
        public static long MakeKey(int pegIdA, int pegIdB)
        {
            int lo = Mathf.Min(pegIdA, pegIdB);
            int hi = Mathf.Max(pegIdA, pegIdB);
            return ((long)lo << 32) | (uint)hi;
        }

        public long Key => MakeKey(A.Id, B.Id);

        public Peg Other(Peg peg) => peg == A ? B : A;

        public void Reset()
        {
            IsOccupied = false;
            BandCount = 0;
            FirstCoveredBy = PlayerId.None;
        }

        public override string ToString() => $"Edge#{Id}[{A.Coord}-{B.Coord}] occ:{IsOccupied} bands:{BandCount}";
    }

    /// <summary>
    /// The smallest enclosable space: an equilateral triangle bounded by exactly three unit edges
    /// and three mutually adjacent pegs (a 3-clique of the lattice adjacency graph).
    /// </summary>
    public sealed class TriangleCell
    {
        public readonly int Id;
        public readonly Peg[] Pegs = new Peg[3];
        public readonly BoardEdge[] Edges = new BoardEdge[3];
        public readonly Vector3 CenterPosition;
        public readonly CellOrientation Orientation;

        public PlayerId Owner = PlayerId.None;

        public bool IsClaimed => Owner != PlayerId.None;

        /// <summary>True when every boundary edge is covered by at least one band.</summary>
        public bool IsFullyEnclosed => Edges[0].IsOccupied && Edges[1].IsOccupied && Edges[2].IsOccupied;

        public TriangleCell(int id, Peg a, Peg b, Peg c, CellOrientation orientation)
        {
            Id = id;
            Pegs[0] = a;
            Pegs[1] = b;
            Pegs[2] = c;
            Orientation = orientation;
            CenterPosition = (a.WorldPosition + b.WorldPosition + c.WorldPosition) / 3f;
        }

        public void Reset() => Owner = PlayerId.None;

        public override string ToString() => $"Cell#{Id}({Pegs[0].Coord},{Pegs[1].Coord},{Pegs[2].Coord}) owner:{Owner}";
    }

    /// <summary>
    /// A pre-computed legal rubber band slot: a <b>straight</b> run of collinear pegs.
    /// </summary>
    /// <remarks>
    /// A real rubber band cannot be bent, so it is stretched over pegs lying on a single lattice line
    /// and covers the consecutive unit edges between them - four pegs cover three edges.
    /// <para>
    /// Because a unit triangle's three edges each run in a different lattice direction, one straight
    /// band can cover <b>at most one edge of any given triangle</b>. Every triangle therefore needs
    /// three separate bands before it can be claimed, which is what creates the contest over the
    /// third-and-final edge.
    /// </para>
    /// </remarks>
    public sealed class BandPlacement
    {
        public readonly int Id;

        /// <summary>The collinear pegs the band rests on, ordered along <see cref="Axis"/>.</summary>
        public readonly Peg[] Pegs;

        /// <summary>The consecutive unit edges the band covers (<c>Pegs.Length - 1</c> of them).</summary>
        public readonly BoardEdge[] Edges;

        /// <summary>Canonical axial direction of the run: (1,0), (1,-1) or (0,1).</summary>
        public readonly Vector2Int Axis;

        /// <summary>Midpoint of the run in world space.</summary>
        public readonly Vector3 Center;

        /// <summary>Unit vector along the run in world space, from the first peg to the last.</summary>
        public readonly Vector3 Direction;

        public bool IsPlaced;
        public PlayerId PlacedBy = PlayerId.None;

        /// <summary>Stacking index assigned at placement time so overlapping bands never Z-fight.</summary>
        public int StackIndex;

        public Peg First => Pegs[0];
        public Peg Last => Pegs[Pegs.Length - 1];

        /// <summary>World-space distance between the two outermost pegs.</summary>
        public float Span => Vector3.Distance(First.WorldPosition, Last.WorldPosition);

        public BandPlacement(int id, Peg[] run, Vector2Int axis)
        {
            Id = id;
            Axis = axis;

            Pegs = new Peg[run.Length];
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < run.Length; i++)
            {
                Pegs[i] = run[i];
                sum += run[i].WorldPosition;
            }

            Edges = new BoardEdge[run.Length - 1];
            Center = sum / run.Length;

            Vector3 delta = Last.WorldPosition - First.WorldPosition;
            Direction = delta.sqrMagnitude > 1e-6f ? delta.normalized : Vector3.forward;
        }

        public bool ContainsPeg(Peg peg)
        {
            for (int i = 0; i < Pegs.Length; i++) if (Pegs[i] == peg) return true;
            return false;
        }

        /// <summary>True when at least one covered edge is not yet occupied by any band.</summary>
        public bool HasUncoveredEdge()
        {
            for (int i = 0; i < Edges.Length; i++) if (!Edges[i].IsOccupied) return true;
            return false;
        }

        /// <summary>
        /// Fills <paramref name="into"/> (length 4) with a closed quad that reads as a stretched
        /// rubber band: the straight run offset to both sides by <paramref name="halfWidth"/>, with
        /// the ends pushed <paramref name="capExtension"/> past the outer pegs so the loop appears to
        /// wrap around them.
        /// </summary>
        public void BuildLoop(Vector3[] into, float height, float halfWidth, float capExtension)
        {
            if (into == null || into.Length < 4) return;

            Vector3 along = Direction;
            Vector3 side = Vector3.Cross(Vector3.up, along);
            side = side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.right;

            Vector3 tail = First.WorldPosition - along * capExtension;
            Vector3 head = Last.WorldPosition + along * capExtension;
            tail.y = height;
            head.y = height;

            Vector3 offset = side * halfWidth;
            into[0] = tail + offset;
            into[1] = head + offset;
            into[2] = head - offset;
            into[3] = tail - offset;
        }

        public void Reset()
        {
            IsPlaced = false;
            PlacedBy = PlayerId.None;
            StackIndex = 0;
        }

        public override string ToString() =>
            $"Band#{Id}[{First.Coord}->{Last.Coord} axis({Axis.x},{Axis.y})] placed:{IsPlaced}";
    }

    /// <summary>Immutable snapshot describing how a finished game ended.</summary>
    public sealed class GameResult
    {
        public readonly IReadOnlyList<PlayerId> Winners;
        public readonly IReadOnlyDictionary<PlayerId, int> Scores;
        public readonly int TotalTurns;
        public readonly int ClaimedCells;
        public readonly int TotalCells;

        public bool IsDraw => Winners.Count > 1;

        public GameResult(IReadOnlyList<PlayerId> winners, IReadOnlyDictionary<PlayerId, int> scores,
                          int totalTurns, int claimedCells, int totalCells)
        {
            Winners = winners;
            Scores = scores;
            TotalTurns = totalTurns;
            ClaimedCells = claimedCells;
            TotalCells = totalCells;
        }
    }
}
