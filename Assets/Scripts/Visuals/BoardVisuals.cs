using System.Collections.Generic;
using Triggle.Core;
using Triggle.Grid;
using UnityEngine;

namespace Triggle.Visuals
{
    /// <summary>
    /// Builds the physical-looking board the lattice sits on: a bevelled hexagonal slab, an accent rim,
    /// faint lines along every unit edge and a socket disc under every peg.
    /// </summary>
    /// <remarks>
    /// The lattice lines are the important part - they are not decoration. Without them the player has
    /// to infer the grid from peg positions alone, which makes it hard to see which triangles are close
    /// to being closed. All geometry is generated procedurally from the board data, so it tracks any
    /// radius or spacing change automatically.
    /// <para>
    /// The slab carries the <see cref="MeshCollider"/> that makes "click the board to cancel" work, so
    /// it replaces the placeholder ground plane entirely.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class BoardVisuals : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private BoardManager board;

        [Header("Slab")]
        [SerializeField] private Material slabMaterial;

        [Tooltip("Extra radius past the outermost pegs, in unit-edge lengths.")]
        [SerializeField, Min(0f)] private float slabPadding = 0.95f;

        [SerializeField, Min(0.02f)] private float slabThickness = 0.55f;

        [Tooltip("Width of the chamfer around the top face. 0 gives a hard edge.")]
        [SerializeField, Min(0f)] private float slabBevel = 0.26f;

        [Tooltip("Height of the slab's top face. Slightly below 0 so bands and tokens sit above it.")]
        [SerializeField] private float slabTopY = -0.015f;

        [Header("Accent Rim")]
        [SerializeField] private Material rimMaterial;
        [SerializeField] private bool drawRim = true;
        [SerializeField, Min(0.005f)] private float rimWidth = 0.075f;
        [SerializeField] private Color rimColor = new Color(0.29f, 0.78f, 0.55f, 1f);

        [Header("Lattice Lines")]
        [SerializeField] private Material lineMaterial;
        [SerializeField] private bool drawLatticeLines = true;
        [SerializeField, Min(0.002f)] private float lineWidth = 0.032f;
        [SerializeField] private Color lineColor = new Color(0.32f, 0.36f, 0.46f, 1f);

        [Header("Peg Sockets")]
        [SerializeField] private Material socketMaterial;
        [SerializeField] private bool drawSockets = true;
        [SerializeField, Min(0.02f)] private float socketRadius = 0.17f;
        [SerializeField, Range(6, 24)] private int socketSegments = 12;
        [SerializeField] private Color socketColor = new Color(0.055f, 0.063f, 0.090f, 1f);

        private readonly List<GameObject> _spawned = new List<GameObject>(4);
        private readonly List<Object> _owned = new List<Object>(8);

        /// <summary>
        /// World-space radius of the outermost board geometry, measured from the board origin. Zero
        /// until the first build.
        /// </summary>
        /// <remarks>
        /// Published so the camera rig can frame the <i>slab</i> rather than the peg ring. Framing to
        /// the pegs alone clips the slab and its accent rim, which is what the player actually reads as
        /// the edge of the board. The rim is built inside this radius, so the slab is the true extent.
        /// </remarks>
        public float OuterRadius { get; private set; }

        private void Awake()
        {
            if (board == null) board = FindObjectOfType<BoardManager>();
        }

        private void OnEnable()
        {
            GameEvents.OnBoardGenerated += Rebuild;
        }

        private void OnDisable()
        {
            GameEvents.OnBoardGenerated -= Rebuild;
        }

        private void OnDestroy()
        {
            Clear();
        }

        private void Start()
        {
            // Covers the case where the board was generated before this component subscribed.
            if (_spawned.Count == 0 && board != null && board.IsBuilt) Rebuild();
        }

        /// <summary>Regenerates all board geometry from the current lattice.</summary>
        public void Rebuild()
        {
            Clear();
            if (board == null || !board.IsBuilt) return;

            // Outer pegs sit at this radius; the hexagon's corners align with the +q, +r ... directions.
            float pegRingRadius = AxialMath.UnitEdgeLength(board.PegSpacing) * board.Radius;
            float unit = AxialMath.UnitEdgeLength(board.PegSpacing);
            float slabRadius = pegRingRadius + slabPadding * unit;
            OuterRadius = slabRadius;

            BuildSlab(slabRadius);
            if (drawRim) BuildRim(slabRadius);
            if (drawLatticeLines) BuildLatticeLines();
            if (drawSockets) BuildSockets();
        }

        /// <summary>
        /// Recolours the board surface to a theme. Only touches materials, never geometry, so it is
        /// safe to call at any time - including while a match is in progress.
        /// </summary>
        public void ApplyTheme(BoardTheme theme)
        {
            if (theme == null) return;

            rimColor = theme.rimColor;
            lineColor = theme.latticeLineColor;
            socketColor = theme.socketColor;

            if (slabMaterial != null)
            {
                MaterialUtility.SetColor(slabMaterial, theme.slabColor);
                MaterialUtility.SetSmoothness(slabMaterial, theme.slabSmoothness);
            }

            if (rimMaterial != null) MaterialUtility.SetColor(rimMaterial, theme.rimColor);
            if (lineMaterial != null) MaterialUtility.SetColor(lineMaterial, theme.latticeLineColor);
            if (socketMaterial != null) MaterialUtility.SetColor(socketMaterial, theme.socketColor);

            // Materials generated as a fallback live on the spawned renderers, so refresh those too.
            for (int i = 0; i < _spawned.Count; i++)
            {
                GameObject go = _spawned[i];
                if (go == null) continue;

                var renderer = go.GetComponent<MeshRenderer>();
                if (renderer == null || renderer.sharedMaterial == null) continue;

                Color? target = go.name switch
                {
                    "Slab" => theme.slabColor,
                    "Rim" => theme.rimColor,
                    "LatticeLines" => theme.latticeLineColor,
                    "PegSockets" => theme.socketColor,
                    _ => null
                };

                if (target.HasValue) MaterialUtility.SetColor(renderer.sharedMaterial, target.Value);
            }
        }

        /// <summary>Destroys everything this component generated, including runtime materials.</summary>
        public void Clear()
        {
            for (int i = 0; i < _spawned.Count; i++) DestroySafely(_spawned[i]);
            _spawned.Clear();

            for (int i = 0; i < _owned.Count; i++) DestroySafely(_owned[i]);
            _owned.Clear();
        }

        // ------------------------------------------------------------------ slab

        private void BuildSlab(float radius)
        {
            Mesh mesh = BuildHexSlabMesh(radius, slabThickness, slabBevel);
            mesh.name = "BoardSlab";

            GameObject go = CreateChild("Slab", mesh,
                slabMaterial != null ? slabMaterial : CreateOwnedLit(new Color(0.106f, 0.122f, 0.169f), 0.12f),
                new Vector3(0f, slabTopY, 0f));

            // The slab is what a "click outside the pegs" hits, which cancels the current selection.
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        /// <summary>
        /// Hexagonal slab: a flat top face, a chamfered ring around it and a vertical side wall.
        /// Corners sit at 0, 60, ... 300 degrees, matching the lattice's own hexagonal outline.
        /// </summary>
        private static Mesh BuildHexSlabMesh(float radius, float thickness, float bevel)
        {
            bevel = Mathf.Min(bevel, radius * 0.5f, thickness * 0.9f);
            float innerRadius = radius - bevel;

            var vertices = new List<Vector3>(32);
            var triangles = new List<int>(96);

            // Ring 0: top face, ring 1: bottom of the chamfer, ring 2: bottom of the side wall.
            int ring0 = vertices.Count;
            for (int i = 0; i < 6; i++) vertices.Add(HexCorner(i, innerRadius, 0f));

            int ring1 = vertices.Count;
            for (int i = 0; i < 6; i++) vertices.Add(HexCorner(i, radius, -bevel));

            int ring2 = vertices.Count;
            for (int i = 0; i < 6; i++) vertices.Add(HexCorner(i, radius, -thickness));

            int centreTop = vertices.Count;
            vertices.Add(Vector3.zero);

            int centreBottom = vertices.Count;
            vertices.Add(new Vector3(0f, -thickness, 0f));

            for (int i = 0; i < 6; i++)
            {
                int next = (i + 1) % 6;

                // Top face fan, wound so the normal points up.
                triangles.Add(centreTop);
                triangles.Add(ring0 + next);
                triangles.Add(ring0 + i);

                // Chamfer quad.
                AddQuad(triangles, ring0 + i, ring0 + next, ring1 + next, ring1 + i);

                // Side wall quad.
                AddQuad(triangles, ring1 + i, ring1 + next, ring2 + next, ring2 + i);

                // Bottom face fan, wound the other way.
                triangles.Add(centreBottom);
                triangles.Add(ring2 + i);
                triangles.Add(ring2 + next);
            }

            return Finalise(vertices, triangles);
        }

        // ------------------------------------------------------------------ rim

        /// <summary>A thin accent band inset just under the slab's top edge.</summary>
        private void BuildRim(float radius)
        {
            var vertices = new List<Vector3>(12);
            var triangles = new List<int>(36);

            float outer = radius - slabBevel * 0.35f;
            float inner = outer - rimWidth;

            for (int i = 0; i < 6; i++)
            {
                vertices.Add(HexCorner(i, inner, 0f));
                vertices.Add(HexCorner(i, outer, 0f));
            }

            for (int i = 0; i < 6; i++)
            {
                int a = i * 2;
                int b = i * 2 + 1;
                int next = (i + 1) % 6;
                int c = next * 2;
                int d = next * 2 + 1;

                AddQuad(triangles, a, c, d, b);
            }

            Mesh mesh = Finalise(vertices, triangles);
            mesh.name = "BoardRim";

            Material material = rimMaterial != null ? rimMaterial : CreateOwnedUnlit(rimColor);
            CreateChild("Rim", mesh, material, new Vector3(0f, slabTopY + 0.004f, 0f));
        }

        // ------------------------------------------------------------------ lattice lines

        /// <summary>
        /// One mesh holding a thin quad for every unit edge, so the whole grid costs a single draw call.
        /// </summary>
        private void BuildLatticeLines()
        {
            IReadOnlyList<BoardEdge> edges = board.Edges;
            if (edges.Count == 0) return;

            var vertices = new List<Vector3>(edges.Count * 4);
            var triangles = new List<int>(edges.Count * 6);
            float half = lineWidth * 0.5f;

            for (int i = 0; i < edges.Count; i++)
            {
                BoardEdge edge = edges[i];

                Vector3 a = edge.A.WorldPosition;
                Vector3 b = edge.B.WorldPosition;
                a.y = 0f;
                b.y = 0f;

                Vector3 along = (b - a).normalized;
                Vector3 side = Vector3.Cross(Vector3.up, along) * half;

                int start = vertices.Count;
                vertices.Add(a - side);
                vertices.Add(b - side);
                vertices.Add(b + side);
                vertices.Add(a + side);

                AddQuad(triangles, start, start + 3, start + 2, start + 1);
            }

            Mesh mesh = Finalise(vertices, triangles);
            mesh.name = "LatticeLines";

            Material material = lineMaterial != null ? lineMaterial : CreateOwnedUnlit(lineColor);
            CreateChild("LatticeLines", mesh, material, new Vector3(0f, slabTopY + 0.008f, 0f));
        }

        // ------------------------------------------------------------------ sockets

        /// <summary>A dark disc under each peg so the posts read as seated in the board.</summary>
        private void BuildSockets()
        {
            IReadOnlyList<Peg> pegs = board.Pegs;
            if (pegs.Count == 0) return;

            var vertices = new List<Vector3>(pegs.Count * (socketSegments + 1));
            var triangles = new List<int>(pegs.Count * socketSegments * 3);

            for (int p = 0; p < pegs.Count; p++)
            {
                Vector3 centre = pegs[p].WorldPosition;
                centre.y = 0f;

                int centreIndex = vertices.Count;
                vertices.Add(centre);

                for (int s = 0; s < socketSegments; s++)
                {
                    float angle = s / (float)socketSegments * Mathf.PI * 2f;
                    vertices.Add(centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * socketRadius);
                }

                for (int s = 0; s < socketSegments; s++)
                {
                    int current = centreIndex + 1 + s;
                    int next = centreIndex + 1 + (s + 1) % socketSegments;

                    triangles.Add(centreIndex);
                    triangles.Add(next);
                    triangles.Add(current);
                }
            }

            Mesh mesh = Finalise(vertices, triangles);
            mesh.name = "PegSockets";

            Material material = socketMaterial != null ? socketMaterial : CreateOwnedUnlit(socketColor);
            CreateChild("PegSockets", mesh, material, new Vector3(0f, slabTopY + 0.012f, 0f));
        }

        // ------------------------------------------------------------------ helpers

        private static Vector3 HexCorner(int index, float radius, float y)
        {
            float angle = index * 60f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);
        }

        private static void AddQuad(List<int> triangles, int a, int b, int c, int d)
        {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
            triangles.Add(a);
            triangles.Add(c);
            triangles.Add(d);
        }

        private static Mesh Finalise(List<Vector3> vertices, List<int> triangles)
        {
            var mesh = new Mesh { hideFlags = HideFlags.DontSave };
            if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private GameObject CreateChild(string childName, Mesh mesh, Material material, Vector3 localPosition)
        {
            var go = new GameObject(childName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPosition;

            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;

            _spawned.Add(go);
            _owned.Add(mesh);
            return go;
        }

        private Material CreateOwnedLit(Color color, float smoothness)
        {
            Material material = MaterialUtility.CreateDefaultLitMaterial();
            MaterialUtility.SetColor(material, color);
            MaterialUtility.SetSmoothness(material, smoothness);
            _owned.Add(material);
            return material;
        }

        private Material CreateOwnedUnlit(Color color)
        {
            Material material = MaterialUtility.CreateDefaultUnlitMaterial();
            MaterialUtility.SetColor(material, color);
            _owned.Add(material);
            return material;
        }

        private static void DestroySafely(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        private void OnValidate()
        {
            slabThickness = Mathf.Max(0.02f, slabThickness);
            slabBevel = Mathf.Max(0f, slabBevel);
            lineWidth = Mathf.Max(0.002f, lineWidth);
            socketRadius = Mathf.Max(0.02f, socketRadius);
        }
    }
}
