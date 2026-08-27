using System.Collections.Generic;
using Triggle.Core;
using UnityEngine;

namespace Triggle.Grid
{
    /// <summary>
    /// Procedurally builds the triangular lattice inside a hexagon of radius R and derives every
    /// higher-order structure from it: unit edges, unit cells (via 3-clique detection) and the full
    /// catalogue of legal rubber band slots.
    /// </summary>
    /// <remarks>
    /// Build order matters and is intentional:
    /// <list type="number">
    /// <item>Pegs — all axial coordinates with hex distance &lt;= R from origin.</item>
    /// <item>Edges — every unordered peg pair at axial distance 1.</item>
    /// <item>Cells — every 3-clique of the adjacency graph, linked to its three boundary edges.</item>
    /// <item>Bands — every straight run of collinear pegs that fits inside the lattice.</item>
    /// </list>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class BoardManager : MonoBehaviour
    {
        [Header("Lattice")]
        [Tooltip("Hexagonal bound of the board. Peg count is 3R^2 + 3R + 1, cell count is 6R^2. " +
                 "Keep radius >= (Pegs Per Band - 1) so that every edge lies on at least one possible " +
                 "band; with the default 4-peg band that means radius 3 or more.")]
        [SerializeField, Range(2, 8)] private int radius = 3;

        [Tooltip("Layout scale. World edge length between two adjacent pegs is sqrt(3) * pegSpacing.")]
        [SerializeField, Min(0.05f)] private float pegSpacing = 1f;

        [Tooltip("Collinear pegs a single rubber band is stretched over. 4 is the standard rule " +
                 "(covering 3 unit edges); 3 or 5 make shorter/longer band variants.")]
        [SerializeField, Range(3, 5)] private int pegsPerBand = 4;

        [Tooltip("Build the board automatically on Awake. Leave off when GameFlowController drives setup " +
                 "so that every listener has had a chance to subscribe first.")]
        [SerializeField] private bool buildOnAwake;

        [Header("Peg Visuals")]
        [Tooltip("Optional peg prefab. Must carry (or will receive) a PegComponent and a SphereCollider. " +
                 "Leave empty to generate a primitive post at runtime.")]
        [SerializeField] private GameObject pegPrefab;

        [Tooltip("Uniform scale applied to the generated peg head.")]
        [SerializeField, Min(0.01f)] private float pegScale = 0.3f;

        [Tooltip("Height of the generated peg post above the board plane.")]
        [SerializeField, Min(0f)] private float pegHeight = 0.35f;

        [Tooltip("Parent for spawned pegs. Created automatically when left empty.")]
        [SerializeField] private Transform pegRoot;

        [Header("Gizmos")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private bool drawEdgeGizmos = true;
        [SerializeField] private bool drawCellGizmos = true;
        [SerializeField] private bool drawCoordinateLabels;
        [SerializeField] private Color pegGizmoColor = new Color(0.85f, 0.85f, 0.9f, 1f);
        [SerializeField] private Color freeEdgeGizmoColor = new Color(0.35f, 0.38f, 0.45f, 0.6f);
        [SerializeField] private Color occupiedEdgeGizmoColor = new Color(1f, 0.75f, 0.2f, 1f);

        private readonly List<Peg> _pegs = new List<Peg>();
        private readonly Dictionary<Vector2Int, Peg> _pegsByCoord = new Dictionary<Vector2Int, Peg>();
        private readonly List<BoardEdge> _edges = new List<BoardEdge>();
        private readonly Dictionary<long, BoardEdge> _edgesByPegPair = new Dictionary<long, BoardEdge>();
        private readonly List<TriangleCell> _cells = new List<TriangleCell>();
        private readonly Dictionary<Vector3Int, TriangleCell> _cellsByPegTriple = new Dictionary<Vector3Int, TriangleCell>();
        private readonly List<BandPlacement> _bands = new List<BandPlacement>();

        /// <summary>True once <see cref="Build"/> has completed successfully.</summary>
        public bool IsBuilt { get; private set; }

        public int Radius => radius;
        public float PegSpacing => pegSpacing;

        /// <summary>
        /// Changes the hexagon radius. The caller must follow up with <see cref="Build"/> - the lattice
        /// is not regenerated here, because doing so mid-match would invalidate every placed band.
        /// Returns true when the value actually changed.
        /// </summary>
        public bool SetRadius(int newRadius)
        {
            int clamped = Mathf.Clamp(newRadius, 2, 8);
            if (clamped == radius) return false;

            radius = clamped;
            return true;
        }

        /// <summary>
        /// Collinear pegs per band, and therefore <c>PegsPerBand - 1</c> covered edges. This is the
        /// single source of truth for the selection size: <see cref="Gameplay.MoveValidator"/> reads it
        /// rather than keeping its own copy.
        /// </summary>
        public int PegsPerBand => Mathf.Clamp(pegsPerBand, 3, 5);

        /// <summary>World-space length of a single unit edge.</summary>
        public float UnitEdgeLength => AxialMath.UnitEdgeLength(pegSpacing);

        public IReadOnlyList<Peg> Pegs => _pegs;
        public IReadOnlyList<BoardEdge> Edges => _edges;
        public IReadOnlyList<TriangleCell> Cells => _cells;

        /// <summary>Every geometrically legal band slot on this board, placed or not.</summary>
        public IReadOnlyList<BandPlacement> Bands => _bands;

        public int TotalCells => _cells.Count;

        private void Awake()
        {
            if (buildOnAwake) Build();
        }

        /// <summary>
        /// Generates the whole board from scratch, destroying any previously spawned peg views.
        /// Raises <see cref="GameEvents.OnBoardGenerated"/> on completion.
        /// </summary>
        public void Build()
        {
            Clear();
            EnsurePegRoot();

            GeneratePegs();
            GenerateEdges();
            GenerateCells();
            GenerateBandPlacements();

            IsBuilt = true;
            WarnIfBoardIsDegenerate();
            GameEvents.RaiseBoardGenerated();
        }

        /// <summary>
        /// A straight band needs a lattice line long enough to hold it. The shortest lines inside a
        /// radius-R hexagon hold R+1 pegs, so covering every edge requires
        /// <c>radius &gt;= PegsPerBand - 1</c>. Below that, edges near the rim belong to no band at all
        /// and the triangles touching them can never be claimed - so say so loudly rather than
        /// shipping a board where part of the score is unreachable.
        /// </summary>
        private void WarnIfBoardIsDegenerate()
        {
            int uncoverable = CountUncoverableEdges();
            if (uncoverable == 0) return;

            int unclaimable = CountUnclaimableCells();
            Debug.LogWarning(
                $"{nameof(BoardManager)}: radius {radius} is too small for bands of {PegsPerBand} pegs. " +
                $"{uncoverable} of {_edges.Count} edges lie on no possible band, so {unclaimable} of " +
                $"{_cells.Count} triangles can never be claimed. " +
                $"Use radius >= {PegsPerBand - 1}, or reduce Pegs Per Band.", this);
        }

        /// <summary>Edges that no band in the catalogue covers. Zero on a well-proportioned board.</summary>
        public int CountUncoverableEdges()
        {
            var covered = new bool[_edges.Count];
            for (int b = 0; b < _bands.Count; b++)
            {
                BoardEdge[] edges = _bands[b].Edges;
                for (int e = 0; e < edges.Length; e++) covered[edges[e].Id] = true;
            }

            int count = 0;
            for (int i = 0; i < covered.Length; i++) if (!covered[i]) count++;

            return count;
        }

        /// <summary>Cells with at least one edge that no band can cover, and so can never be claimed.</summary>
        public int CountUnclaimableCells()
        {
            var covered = new bool[_edges.Count];
            for (int b = 0; b < _bands.Count; b++)
            {
                BoardEdge[] edges = _bands[b].Edges;
                for (int e = 0; e < edges.Length; e++) covered[edges[e].Id] = true;
            }

            int count = 0;
            for (int c = 0; c < _cells.Count; c++)
            {
                TriangleCell cell = _cells[c];
                if (!covered[cell.Edges[0].Id] || !covered[cell.Edges[1].Id] || !covered[cell.Edges[2].Id])
                    count++;
            }

            return count;
        }

        /// <summary>Destroys spawned peg views and drops every cached structure.</summary>
        public void Clear()
        {
            IsBuilt = false;

            _pegs.Clear();
            _pegsByCoord.Clear();
            _edges.Clear();
            _edgesByPegPair.Clear();
            _cells.Clear();
            _cellsByPegTriple.Clear();
            _bands.Clear();

            if (pegRoot == null) return;

            for (int i = pegRoot.childCount - 1; i >= 0; i--)
            {
                GameObject child = pegRoot.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }
        }

        /// <summary>
        /// Clears occupancy, ownership and band placement flags while keeping the generated geometry
        /// and peg GameObjects intact. Used by "play again" so a restart costs no allocations.
        /// </summary>
        public void ResetRuntimeState()
        {
            for (int i = 0; i < _edges.Count; i++) _edges[i].Reset();
            for (int i = 0; i < _cells.Count; i++) _cells[i].Reset();
            for (int i = 0; i < _bands.Count; i++) _bands[i].Reset();
        }

        // ------------------------------------------------------------------ pegs

        private void EnsurePegRoot()
        {
            if (pegRoot != null) return;

            var root = new GameObject("Pegs");
            root.transform.SetParent(transform, false);
            pegRoot = root.transform;
        }

        private void GeneratePegs()
        {
            int id = 0;

            // Axial hexagon: q from -R..R, r bounded so that hex distance from origin never exceeds R.
            for (int q = -radius; q <= radius; q++)
            {
                int rMin = Mathf.Max(-radius, -q - radius);
                int rMax = Mathf.Min(radius, -q + radius);

                for (int r = rMin; r <= rMax; r++)
                {
                    var coord = new Vector2Int(q, r);
                    Vector3 world = transform.TransformPoint(AxialMath.ToWorld(coord, pegSpacing));

                    var peg = new Peg(id++, coord, world);
                    _pegs.Add(peg);
                    _pegsByCoord.Add(coord, peg);

                    peg.View = SpawnPegView(peg);
                }
            }

            // Neighbour lists, resolved after every peg exists.
            foreach (Peg peg in _pegs)
            {
                for (int d = 0; d < 6; d++)
                {
                    if (_pegsByCoord.TryGetValue(AxialMath.Neighbour(peg.Coord, d), out Peg neighbour))
                        peg.Neighbours.Add(neighbour);
                }
            }
        }

        private Transform SpawnPegView(Peg peg)
        {
            GameObject go;

            if (pegPrefab != null)
            {
                go = Instantiate(pegPrefab, peg.WorldPosition, Quaternion.identity, pegRoot);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.SetParent(pegRoot, false);
                go.transform.position = peg.WorldPosition + Vector3.up * pegHeight;
                go.transform.localScale = Vector3.one * pegScale;

                // A thin post grounds the peg head visually. It carries no collider.
                GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Collider postCollider = post.GetComponent<Collider>();
                if (postCollider != null) Destroy(postCollider);
                post.name = "Post";
                post.transform.SetParent(go.transform, false);
                post.transform.localScale = new Vector3(0.45f, pegHeight / Mathf.Max(pegScale, 0.0001f) * 0.5f, 0.45f);
                post.transform.localPosition = new Vector3(0f, -post.transform.localScale.y, 0f);
            }

            go.name = $"Peg_{peg.Coord.x}_{peg.Coord.y}";
            go.transform.position = peg.WorldPosition + Vector3.up * pegHeight;

            var component = go.GetComponent<PegComponent>();
            if (component == null) component = go.AddComponent<PegComponent>();
            component.Bind(peg);

            return go.transform;
        }

        // ------------------------------------------------------------------ edges

        private void GenerateEdges()
        {
            int id = 0;

            foreach (Peg peg in _pegs)
            {
                foreach (Peg neighbour in peg.Neighbours)
                {
                    // Emit each undirected pair exactly once.
                    if (neighbour.Id <= peg.Id) continue;

                    var edge = new BoardEdge(id++, peg, neighbour);
                    _edges.Add(edge);
                    _edgesByPegPair.Add(edge.Key, edge);

                    peg.IncidentEdges.Add(edge);
                    neighbour.IncidentEdges.Add(edge);
                }
            }
        }

        // ------------------------------------------------------------------ cells

        /// <summary>
        /// Finds every unit triangle as a 3-clique of the adjacency graph. For each edge (a, b) we look
        /// at the common neighbours c and keep only those with <c>c.Id &gt; a.Id</c> and
        /// <c>c.Id &gt; b.Id</c>, which yields each triangle exactly once.
        /// </summary>
        private void GenerateCells()
        {
            int id = 0;

            foreach (BoardEdge edge in _edges)
            {
                Peg a = edge.A;
                Peg b = edge.B;
                int threshold = Mathf.Max(a.Id, b.Id);

                foreach (Peg candidate in a.Neighbours)
                {
                    if (candidate.Id <= threshold) continue;
                    if (!b.Neighbours.Contains(candidate)) continue;

                    CellOrientation orientation = ClassifyOrientation(
                        a.WorldPosition, b.WorldPosition, candidate.WorldPosition);

                    var cell = new TriangleCell(id++, a, b, candidate, orientation);

                    // Link the three boundary edges and register the cell on each of them.
                    cell.Edges[0] = GetEdge(a, b);
                    cell.Edges[1] = GetEdge(b, candidate);
                    cell.Edges[2] = GetEdge(candidate, a);

                    for (int i = 0; i < 3; i++) cell.Edges[i].Cells.Add(cell);

                    _cells.Add(cell);
                    _cellsByPegTriple.Add(MakeTripleKey(a.Id, b.Id, candidate.Id), cell);
                }
            }
        }

        /// <summary>
        /// A unit triangle has two vertices sharing one Z extreme and a single apex at the other.
        /// The apex direction defines the orientation.
        /// </summary>
        private static CellOrientation ClassifyOrientation(Vector3 a, Vector3 b, Vector3 c)
        {
            float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
            float maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
            float mid = (minZ + maxZ) * 0.5f;

            int aboveMid = 0;
            if (a.z > mid) aboveMid++;
            if (b.z > mid) aboveMid++;
            if (c.z > mid) aboveMid++;

            // Exactly one vertex above the midline => apex points toward +Z.
            return aboveMid == 1 ? CellOrientation.Up : CellOrientation.Down;
        }

        // ------------------------------------------------------------------ bands

        /// <summary>
        /// The three lattice line directions. The six neighbour directions form three opposite pairs,
        /// and a straight run along <c>d</c> is the same run as along <c>-d</c>, so only one
        /// representative per pair is enumerated.
        /// </summary>
        private static readonly Vector2Int[] BandAxes =
        {
            new Vector2Int(+1,  0),
            new Vector2Int(+1, -1),
            new Vector2Int( 0, +1)
        };

        /// <summary>
        /// Enumerates every straight run of <see cref="PegsPerBand"/> collinear pegs that fits inside
        /// the lattice: a rubber band cannot be bent, so a legal placement is a line, not a loop.
        /// </summary>
        /// <remarks>
        /// Each run is emitted exactly once because the start peg is always the run's tail in the
        /// canonical <c>+axis</c> direction, so no de-duplication pass is needed.
        /// </remarks>
        private void GenerateBandPlacements()
        {
            int count = PegsPerBand;
            var run = new Peg[count];
            int id = 0;

            foreach (Peg start in _pegs)
            {
                for (int a = 0; a < BandAxes.Length; a++)
                {
                    Vector2Int axis = BandAxes[a];

                    bool complete = true;
                    for (int i = 0; i < count && complete; i++)
                    {
                        var coord = new Vector2Int(start.Coord.x + axis.x * i, start.Coord.y + axis.y * i);
                        if (TryGetPeg(coord, out Peg peg)) run[i] = peg;
                        else complete = false;
                    }
                    if (!complete) continue;

                    var band = new BandPlacement(id, run, axis);

                    for (int e = 0; e < band.Edges.Length && complete; e++)
                    {
                        if (TryGetEdge(run[e], run[e + 1], out BoardEdge edge)) band.Edges[e] = edge;
                        else complete = false;
                    }
                    if (!complete) continue;

                    _bands.Add(band);
                    for (int i = 0; i < count; i++) run[i].Bands.Add(band);
                    id++;
                }
            }
        }

        // ------------------------------------------------------------------ lookups

        public bool TryGetPeg(Vector2Int coord, out Peg peg) => _pegsByCoord.TryGetValue(coord, out peg);

        public Peg GetPeg(Vector2Int coord) => _pegsByCoord.TryGetValue(coord, out Peg peg) ? peg : null;

        public bool TryGetEdge(Peg a, Peg b, out BoardEdge edge)
        {
            if (a == null || b == null) { edge = null; return false; }
            return _edgesByPegPair.TryGetValue(BoardEdge.MakeKey(a.Id, b.Id), out edge);
        }

        /// <summary>Returns the unit edge between two adjacent pegs, or null when they are not adjacent.</summary>
        public BoardEdge GetEdge(Peg a, Peg b) => TryGetEdge(a, b, out BoardEdge edge) ? edge : null;

        public bool TryGetCell(Peg a, Peg b, Peg c, out TriangleCell cell) =>
            _cellsByPegTriple.TryGetValue(MakeTripleKey(a.Id, b.Id, c.Id), out cell);

        /// <summary>Number of cells currently owned by any player.</summary>
        public int CountClaimedCells()
        {
            int count = 0;
            for (int i = 0; i < _cells.Count; i++) if (_cells[i].IsClaimed) count++;
            return count;
        }

        /// <summary>Order-independent key for a peg triple.</summary>
        private static Vector3Int MakeTripleKey(int a, int b, int c)
        {
            // Three-element sorting network.
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return new Vector3Int(a, b, c);
        }

        // ------------------------------------------------------------------ gizmos

        private void OnDrawGizmos()
        {
            if (!drawGizmos) return;

            if (!IsBuilt)
            {
                DrawPreviewGizmos();
                return;
            }

            if (drawEdgeGizmos)
            {
                foreach (BoardEdge edge in _edges)
                {
                    Gizmos.color = edge.IsOccupied ? occupiedEdgeGizmoColor : freeEdgeGizmoColor;
                    Vector3 lift = Vector3.up * (edge.IsOccupied ? 0.04f : 0.01f);
                    Gizmos.DrawLine(edge.A.WorldPosition + lift, edge.B.WorldPosition + lift);
                }
            }

            if (drawCellGizmos)
            {
                foreach (TriangleCell cell in _cells)
                {
                    if (cell.IsClaimed)
                    {
                        Gizmos.color = GizmoColorForPlayer(cell.Owner);
                        Gizmos.DrawSphere(cell.CenterPosition + Vector3.up * 0.05f, pegSpacing * 0.18f);
                    }
                    else if (cell.IsFullyEnclosed)
                    {
                        Gizmos.color = Color.white;
                        Gizmos.DrawWireSphere(cell.CenterPosition + Vector3.up * 0.05f, pegSpacing * 0.14f);
                    }
                }
            }

            Gizmos.color = pegGizmoColor;
            foreach (Peg peg in _pegs)
                Gizmos.DrawWireSphere(peg.WorldPosition, pegSpacing * 0.12f);

#if UNITY_EDITOR
            if (drawCoordinateLabels)
            {
                foreach (Peg peg in _pegs)
                {
                    UnityEditor.Handles.Label(peg.WorldPosition + Vector3.up * (pegHeight + 0.25f),
                        $"{peg.Coord.x},{peg.Coord.y}");
                }
            }
#endif
        }

        /// <summary>Edit-mode preview so the board can be sized without entering play mode.</summary>
        private void DrawPreviewGizmos()
        {
            Gizmos.color = pegGizmoColor;

            for (int q = -radius; q <= radius; q++)
            {
                int rMin = Mathf.Max(-radius, -q - radius);
                int rMax = Mathf.Min(radius, -q + radius);

                for (int r = rMin; r <= rMax; r++)
                {
                    var coord = new Vector2Int(q, r);
                    Vector3 world = transform.TransformPoint(AxialMath.ToWorld(coord, pegSpacing));
                    Gizmos.DrawWireSphere(world, pegSpacing * 0.12f);

                    if (!drawEdgeGizmos) continue;

                    Gizmos.color = freeEdgeGizmoColor;
                    // Only three of the six directions, so each edge is drawn once.
                    for (int d = 0; d < 3; d++)
                    {
                        Vector2Int n = AxialMath.Neighbour(coord, d);
                        if (!AxialMath.IsInsideHex(n, radius)) continue;
                        Gizmos.DrawLine(world, transform.TransformPoint(AxialMath.ToWorld(n, pegSpacing)));
                    }
                    Gizmos.color = pegGizmoColor;
                }
            }
        }

        private static Color GizmoColorForPlayer(PlayerId player) => player switch
        {
            PlayerId.Player1 => new Color(0.95f, 0.28f, 0.32f),
            PlayerId.Player2 => new Color(0.25f, 0.60f, 0.95f),
            PlayerId.Player3 => new Color(0.35f, 0.85f, 0.45f),
            PlayerId.Player4 => new Color(0.98f, 0.80f, 0.25f),
            _ => Color.grey
        };

        private void OnValidate()
        {
            radius = Mathf.Clamp(radius, 2, 8);
            pegsPerBand = Mathf.Clamp(pegsPerBand, 3, 5);
            pegSpacing = Mathf.Max(0.05f, pegSpacing);
            pegScale = Mathf.Max(0.01f, pegScale);
            pegHeight = Mathf.Max(0f, pegHeight);
        }
    }
}
