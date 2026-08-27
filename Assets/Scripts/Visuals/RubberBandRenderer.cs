using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.UI;
using UnityEngine;

namespace Triggle.Visuals
{
    /// <summary>
    /// Spawns one procedural <see cref="LineRenderer"/> loop per placed rubber band and animates it
    /// snapping onto the pegs.
    /// </summary>
    /// <remarks>
    /// A band is a straight run of pegs, so it renders as a flat four-point loop: the peg line offset
    /// to either side by <c>bandHalfWidth</c>, with the ends pushed <c>capExtension</c> past the two
    /// outermost pegs so the rubber appears to wrap around them. The loop is lifted by
    /// <c>baseHeight + stackIndex * stackHeightStep</c> so bands sharing an edge never Z-fight.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class RubberBandRenderer : MonoBehaviour
    {
        [Header("Palette")]
        [SerializeField] private PlayerColorPalette palette;

        [Tooltip("Tint each band with the placing player's colour. Off uses the palette neutral colour.")]
        [SerializeField] private bool tintByPlayer = true;

        [Header("Geometry")]
        [Tooltip("Optional material for bands. An unlit material from the palette is used when empty.")]
        [SerializeField] private Material bandMaterialOverride;

        [SerializeField, Min(0.005f)] private float bandWidth = 0.085f;

        [Tooltip("Height of the first band above the board plane.")]
        [SerializeField] private float baseHeight = 0.28f;

        [Tooltip("Extra height per stacking level, applied when bands share edges.")]
        [SerializeField, Min(0f)] private float stackHeightStep = 0.055f;

        [Tooltip("Half-thickness of the stretched band: how far the two sides sit either side of the " +
                 "peg line. Larger values read as a slacker, fatter band.")]
        [SerializeField, Min(0.005f)] private float bandHalfWidth = 0.075f;

        [Tooltip("How far the loop extends past the two outermost pegs, so it appears to wrap them.")]
        [SerializeField, Min(0f)] private float capExtension = 0.16f;

        [Tooltip("Rounded corner segments. 0 draws hard corners.")]
        [SerializeField, Range(0, 6)] private int cornerVertices = 4;

        [Header("Snap Animation")]
        [Tooltip("Seconds for the band to snap from a collapsed loop onto the pegs.")]
        [SerializeField, Min(0f)] private float snapDuration = 0.22f;

        [Tooltip("How far past the final radius the band overshoots before settling. 0 disables the overshoot.")]
        [SerializeField, Range(0f, 0.5f)] private float overshoot = 0.14f;

        [SerializeField] private Transform bandRoot;

        private readonly List<GameObject> _spawned = new List<GameObject>(64);
        private readonly List<Material> _ownedMaterials = new List<Material>(8);

        /// <summary>One tinted instance of the override material per seat, not per band.</summary>
        private readonly Dictionary<PlayerId, Material> _overrideMaterials = new Dictionary<PlayerId, Material>();

        private void Awake()
        {
            if (palette == null) palette = PlayerColorPalette.Fallback;
            EnsureRoot();
        }

        private void OnEnable()
        {
            GameEvents.OnBandPlaced += HandleBandPlaced;
            GameEvents.OnGameReset += ClearAll;
        }

        private void OnDisable()
        {
            GameEvents.OnBandPlaced -= HandleBandPlaced;
            GameEvents.OnGameReset -= ClearAll;
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _ownedMaterials.Count; i++)
            {
                if (_ownedMaterials[i] == null) continue;
                if (Application.isPlaying) Destroy(_ownedMaterials[i]);
                else DestroyImmediate(_ownedMaterials[i]);
            }

            _ownedMaterials.Clear();
            _overrideMaterials.Clear();
        }

        private void EnsureRoot()
        {
            if (bandRoot != null) return;

            var root = new GameObject("Bands");
            root.transform.SetParent(transform, false);
            bandRoot = root.transform;
        }

        private void HandleBandPlaced(PlayerId player, BandPlacement band)
        {
            if (band == null) return;

            EnsureRoot();

            Color color = tintByPlayer ? palette.GetColor(player) : palette.NeutralColor;
            float height = baseHeight + band.StackIndex * stackHeightStep;

            var go = new GameObject($"Band_{band.Id}_{player}");
            go.transform.SetParent(bandRoot, false);

            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = true;
            line.alignment = LineAlignment.View;
            line.numCornerVertices = cornerVertices;
            line.numCapVertices = cornerVertices;
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.widthMultiplier = bandWidth;
            line.startColor = color;
            line.endColor = color;

            Material material = ResolveMaterial(player, color);
            line.material = material;

            // A straight band is a flat four-point loop: the peg line offset to both sides.
            var points = new Vector3[4];
            line.positionCount = points.Length;

            if (snapDuration > 0f && Application.isPlaying)
            {
                // Start with zero thickness (a bare line along the pegs) and stretch it open.
                band.BuildLoop(points, height, 0f, capExtension);
                line.SetPositions(points);

                StartCoroutine(SnapRoutine(line, band, height, points));
            }
            else
            {
                band.BuildLoop(points, height, bandHalfWidth, capExtension);
                line.SetPositions(points);
            }

            _spawned.Add(go);
        }

        /// <summary>
        /// Stretches the band open from a flat line to its full thickness, overshooting slightly so it
        /// reads as rubber snapping over the pegs.
        /// </summary>
        private IEnumerator SnapRoutine(LineRenderer line, BandPlacement band, float height, Vector3[] points)
        {
            float elapsed = 0f;

            while (elapsed < snapDuration)
            {
                elapsed += Time.deltaTime;
                if (line == null) yield break;

                float linear = Mathf.Clamp01(elapsed / snapDuration);
                float eased = Mathf.SmoothStep(0f, 1f, linear);

                // A single sine lobe adds the overshoot without needing a second easing curve.
                float t = eased + overshoot * Mathf.Sin(linear * Mathf.PI) * (1f - linear);

                band.BuildLoop(points, height, bandHalfWidth * t, capExtension);
                line.SetPositions(points);

                yield return null;
            }

            if (line == null) yield break;

            band.BuildLoop(points, height, bandHalfWidth, capExtension);
            line.SetPositions(points);
        }

        private Material ResolveMaterial(PlayerId player, Color color)
        {
            if (bandMaterialOverride == null) return palette.GetBandMaterial(player);

            // Instance the override once per seat: tinting must not mutate the shared asset, but a
            // fresh material per band would leak dozens of them over a full match.
            if (_overrideMaterials.TryGetValue(player, out Material cached) && cached != null) return cached;

            Material instance = MaterialUtility.Instantiate(bandMaterialOverride, $"Band_{player}_Mat");
            MaterialUtility.SetColor(instance, color);

            _overrideMaterials[player] = instance;
            _ownedMaterials.Add(instance);
            return instance;
        }

        /// <summary>Destroys every spawned band. Wired to <see cref="GameEvents.OnGameReset"/>.</summary>
        public void ClearAll()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] == null) continue;
                if (Application.isPlaying) Destroy(_spawned[i]);
                else DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();
        }
    }
}
