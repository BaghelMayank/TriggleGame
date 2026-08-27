using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.UI;
using UnityEngine;

namespace Triggle.Visuals
{
    /// <summary>
    /// Fills each claimed triangle with a flat, player-coloured plate that pops into place.
    /// </summary>
    /// <remarks>
    /// This is what actually communicates ownership: a token marks a triangle, but a filled area lets
    /// the player read territory across the whole board at a glance.
    /// <para>
    /// Every up-pointing unit triangle is congruent with every other, and likewise for down-pointing
    /// ones, so exactly two meshes are built and shared by all fills. The vertices are stored relative
    /// to the centroid in a canonical order (sorted by angle), which is what makes that sharing valid.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class CellFillRenderer : MonoBehaviour
    {
        [Header("Palette")]
        [SerializeField] private PlayerColorPalette palette;

        [Header("Appearance")]
        [Tooltip("Optional material. An unlit material tinted per player is generated when empty.")]
        [SerializeField] private Material fillMaterialOverride;

        [Tooltip("How far each corner is pulled toward the centre, as a fraction of the triangle. " +
                 "Keeps the rubber bands and pegs readable over the fill.")]
        [SerializeField, Range(0f, 0.45f)] private float inset = 0.14f;

        [Tooltip("Height above the board plane. Must clear the lattice lines but stay under the bands.")]
        [SerializeField] private float height = 0.022f;

        [Tooltip("Opacity of the fill.")]
        [SerializeField, Range(0.1f, 1f)] private float fillAlpha = 0.62f;

        [Header("Pop Animation")]
        [SerializeField, Min(0.01f)] private float popDuration = 0.34f;

        [Tooltip("Overshoot of the scale-in. 0 is a plain ease-out.")]
        [SerializeField, Range(0f, 3f)] private float popOvershoot = 1.9f;

        [Tooltip("Degrees the plate spins through as it appears.")]
        [SerializeField, Range(0f, 180f)] private float popSpin = 40f;

        [SerializeField] private Transform fillRoot;

        private readonly List<GameObject> _spawned = new List<GameObject>(96);
        private readonly Dictionary<PlayerId, Material> _materials = new Dictionary<PlayerId, Material>();
        private readonly Dictionary<CellOrientation, Mesh> _meshes = new Dictionary<CellOrientation, Mesh>(2);

        private void Awake()
        {
            if (palette == null) palette = PlayerColorPalette.Fallback;
            EnsureRoot();
        }

        private void OnEnable()
        {
            GameEvents.OnCellClaimed += HandleCellClaimed;
            GameEvents.OnGameReset += ClearAll;
        }

        private void OnDisable()
        {
            GameEvents.OnCellClaimed -= HandleCellClaimed;
            GameEvents.OnGameReset -= ClearAll;
        }

        private void OnDestroy()
        {
            ClearAll();

            foreach (KeyValuePair<PlayerId, Material> entry in _materials) DestroySafely(entry.Value);
            _materials.Clear();

            foreach (KeyValuePair<CellOrientation, Mesh> entry in _meshes) DestroySafely(entry.Value);
            _meshes.Clear();
        }

        private void EnsureRoot()
        {
            if (fillRoot != null) return;

            var root = new GameObject("CellFills");
            root.transform.SetParent(transform, false);
            fillRoot = root.transform;
        }

        private void HandleCellClaimed(TriangleCell cell)
        {
            if (cell == null || cell.Owner == PlayerId.None) return;

            EnsureRoot();

            var go = new GameObject($"Fill_Cell{cell.Id}_{cell.Owner}");
            go.transform.SetParent(fillRoot, false);
            go.transform.position = new Vector3(cell.CenterPosition.x, height, cell.CenterPosition.z);

            go.AddComponent<MeshFilter>().sharedMesh = GetMesh(cell);

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = GetMaterial(cell.Owner);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _spawned.Add(go);

            if (Application.isPlaying) StartCoroutine(PopRoutine(go.transform));
            else go.transform.localScale = Vector3.one;
        }

        /// <summary>Scales the plate up from nothing with a slight overshoot and a short spin.</summary>
        private IEnumerator PopRoutine(Transform target)
        {
            float elapsed = 0f;
            float startYaw = -popSpin;

            target.localScale = Vector3.zero;
            target.localRotation = Quaternion.Euler(0f, startYaw, 0f);

            while (elapsed < popDuration)
            {
                elapsed += Time.deltaTime;
                if (target == null) yield break;

                float linear = Mathf.Clamp01(elapsed / popDuration);
                float eased = UITween.EaseOutBack(linear, popOvershoot);

                target.localScale = Vector3.one * eased;
                target.localRotation = Quaternion.Euler(0f, Mathf.LerpUnclamped(startYaw, 0f, eased), 0f);

                yield return null;
            }

            if (target == null) yield break;

            target.localScale = Vector3.one;
            target.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// The shared mesh for this cell's orientation, built on first use. Vertices are relative to the
        /// centroid, inset toward it, and ordered counter-clockwise so the face points up.
        /// </summary>
        private Mesh GetMesh(TriangleCell cell)
        {
            if (_meshes.TryGetValue(cell.Orientation, out Mesh cached) && cached != null) return cached;

            var offsets = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                Vector3 local = cell.Pegs[i].WorldPosition - cell.CenterPosition;
                local.y = 0f;
                offsets[i] = local * (1f - inset);
            }

            // Canonical ordering by angle: without it, the peg order from cell discovery varies and the
            // shared mesh would not match every cell of the same orientation.
            System.Array.Sort(offsets, (a, b) =>
                Mathf.Atan2(a.z, a.x).CompareTo(Mathf.Atan2(b.z, b.x)));

            var mesh = new Mesh { name = $"CellFill_{cell.Orientation}", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(new List<Vector3>(offsets));

            // Flip the winding if this order happens to face downward.
            Vector3 normal = Vector3.Cross(offsets[1] - offsets[0], offsets[2] - offsets[0]);
            var indices = normal.y >= 0f ? new[] { 0, 1, 2 } : new[] { 0, 2, 1 };

            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            _meshes[cell.Orientation] = mesh;
            return mesh;
        }

        /// <summary>One tinted material per seat, shared by all of that seat's fills.</summary>
        private Material GetMaterial(PlayerId player)
        {
            if (_materials.TryGetValue(player, out Material cached) && cached != null) return cached;

            Material material = fillMaterialOverride != null
                ? MaterialUtility.Instantiate(fillMaterialOverride, $"CellFill_{player}_Mat")
                : MaterialUtility.CreateDefaultTransparentMaterial();

            // The fill is deliberately translucent, so the material must blend even when an override
            // material was supplied.
            MaterialUtility.MakeTransparent(material);

            Color color = palette.GetColor(player);
            color.a = fillAlpha;
            MaterialUtility.SetColor(material, color);

            _materials[player] = material;
            return material;
        }

        /// <summary>Destroys every spawned fill. Wired to <see cref="GameEvents.OnGameReset"/>.</summary>
        public void ClearAll()
        {
            for (int i = 0; i < _spawned.Count; i++) DestroySafely(_spawned[i]);
            _spawned.Clear();
        }

        private static void DestroySafely(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
