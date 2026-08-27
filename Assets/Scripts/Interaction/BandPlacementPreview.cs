using System.Collections.Generic;
using Triggle.Core;
using Triggle.Gameplay;
using UnityEngine;

namespace Triggle.Interaction
{
    /// <summary>
    /// Draws the "ghost" rubber band while the player is still choosing pegs.
    /// </summary>
    /// <remarks>
    /// Two overlapping hints are rendered:
    /// <list type="bullet">
    /// <item>A solid polyline through the pegs already picked, extended to the hovered peg.</item>
    /// <item>Once the partial selection narrows to a single legal band, a translucent loop showing the
    /// whole straight run that band would occupy - so the player sees the result before committing.</item>
    /// </list>
    /// Both line renderers are created at runtime; no prefabs required.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class BandPlacementPreview : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;
        [SerializeField] private InputController inputController;

        [Header("Appearance")]
        [Tooltip("Optional material for the preview lines. An unlit material is generated when empty.")]
        [SerializeField] private Material previewMaterial;

        [SerializeField] private Color selectionColor = new Color(1f, 0.85f, 0.35f, 0.95f);
        [SerializeField] private Color ghostColor = new Color(0.55f, 0.95f, 1f, 0.40f);

        [SerializeField, Min(0.005f)] private float selectionWidth = 0.09f;
        [SerializeField, Min(0.005f)] private float ghostWidth = 0.055f;

        [Tooltip("Height above the board plane, kept above placed bands so the preview always reads.")]
        [SerializeField] private float previewHeight = 0.55f;

        [Tooltip("Half-thickness of the ghost band loop.")]
        [SerializeField, Min(0.005f)] private float ghostHalfWidth = 0.07f;

        [Tooltip("How far the ghost loop extends past the outermost pegs of the run.")]
        [SerializeField, Min(0f)] private float ghostCapExtension = 0.16f;

        [Header("Motion")]
        [Tooltip("Pulse speed of the ghost loop's alpha. 0 disables pulsing.")]
        [SerializeField, Min(0f)] private float pulseSpeed = 2.6f;

        private LineRenderer _selectionLine;
        private LineRenderer _ghostLine;
        private Material _selectionMaterial;
        private Material _ghostMaterial;

        private readonly List<Vector3> _points = new List<Vector3>(8);
        private readonly List<Peg> _probe = new List<Peg>(5);

        /// <summary>Reused four-point buffer for the ghost band loop.</summary>
        private readonly Vector3[] _ghostLoop = new Vector3[4];

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
            if (inputController == null) inputController = FindObjectOfType<InputController>();

            // Both preview lines rely on alpha (the ghost especially), so they must blend.
            _selectionMaterial = previewMaterial != null
                ? MaterialUtility.Instantiate(previewMaterial, "PreviewSelection_Mat")
                : MaterialUtility.CreateDefaultTransparentMaterial();

            _ghostMaterial = previewMaterial != null
                ? MaterialUtility.Instantiate(previewMaterial, "PreviewGhost_Mat")
                : MaterialUtility.CreateDefaultTransparentMaterial();

            MaterialUtility.MakeTransparent(_selectionMaterial);
            MaterialUtility.MakeTransparent(_ghostMaterial);

            _selectionLine = CreateLine("SelectionPreview", _selectionMaterial, selectionWidth, selectionColor, false);
            _ghostLine = CreateLine("GhostPreview", _ghostMaterial, ghostWidth, ghostColor, true);
        }

        private LineRenderer CreateLine(string lineName, Material material, float width, Color color, bool loop)
        {
            var go = new GameObject(lineName);
            go.transform.SetParent(transform, false);

            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = loop;
            line.alignment = LineAlignment.View;
            line.numCapVertices = 4;
            line.numCornerVertices = 4;
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.material = material;
            line.widthMultiplier = width;
            line.startColor = color;
            line.endColor = color;
            line.positionCount = 0;

            MaterialUtility.SetColor(material, color);
            return line;
        }

        private void OnDisable()
        {
            HideAll();
        }

        private void OnDestroy()
        {
            DestroyMaterial(_selectionMaterial);
            DestroyMaterial(_ghostMaterial);
        }

        private static void DestroyMaterial(Material material)
        {
            if (material == null) return;
            if (Application.isPlaying) Destroy(material);
            else DestroyImmediate(material);
        }

        private void LateUpdate()
        {
            if (flowController == null || inputController == null || flowController.Validator == null)
            {
                HideAll();
                return;
            }

            if (!flowController.AcceptsInput)
            {
                HideAll();
                return;
            }

            IReadOnlyList<Peg> selection = inputController.Selection;
            Peg hovered = inputController.HoveredPeg;

            UpdateSelectionLine(selection, hovered);
            UpdateGhostLine(selection, hovered);
        }

        /// <summary>Solid polyline through the picked pegs, with a rubber-banding tail to the cursor.</summary>
        private void UpdateSelectionLine(IReadOnlyList<Peg> selection, Peg hovered)
        {
            _points.Clear();

            for (int i = 0; i < selection.Count; i++)
                _points.Add(Lift(selection[i].WorldPosition));

            // Extend to the hovered peg only when it is not already part of the selection.
            if (hovered != null && !Contains(selection, hovered))
                _points.Add(Lift(hovered.WorldPosition));

            if (_points.Count < 2)
            {
                _selectionLine.positionCount = 0;
                return;
            }

            _selectionLine.positionCount = _points.Count;
            for (int i = 0; i < _points.Count; i++) _selectionLine.SetPosition(i, _points[i]);

            _selectionLine.widthMultiplier = selectionWidth;
            _selectionLine.startColor = selectionColor;
            _selectionLine.endColor = selectionColor;
            MaterialUtility.SetColor(_selectionMaterial, selectionColor);
        }

        /// <summary>
        /// Translucent loop over the full run of pegs, shown as soon as the selection (optionally plus
        /// the hovered peg) resolves to exactly one legal band.
        /// </summary>
        private void UpdateGhostLine(IReadOnlyList<Peg> selection, Peg hovered)
        {
            BandPlacement band = ResolveGhostBand(selection, hovered);

            if (band == null)
            {
                _ghostLine.positionCount = 0;
                return;
            }

            band.BuildLoop(_ghostLoop, previewHeight, ghostHalfWidth, ghostCapExtension);
            _ghostLine.positionCount = _ghostLoop.Length;
            _ghostLine.SetPositions(_ghostLoop);

            Color color = ghostColor;
            if (pulseSpeed > 0f)
                color.a *= Mathf.Lerp(0.55f, 1f, 0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed));

            _ghostLine.widthMultiplier = ghostWidth;
            _ghostLine.startColor = color;
            _ghostLine.endColor = color;
            MaterialUtility.SetColor(_ghostMaterial, color);
        }

        /// <summary>
        /// Prefers the band implied by "selection + hovered peg" so the preview updates a step ahead of
        /// the click; falls back to the band implied by the selection alone.
        /// </summary>
        private BandPlacement ResolveGhostBand(IReadOnlyList<Peg> selection, Peg hovered)
        {
            MoveValidator validator = flowController.Validator;

            if (hovered != null && !Contains(selection, hovered) &&
                selection.Count < validator.RequiredPegCount)
            {
                _probe.Clear();
                for (int i = 0; i < selection.Count; i++) _probe.Add(selection[i]);
                _probe.Add(hovered);

                if (validator.TryGetUniqueCandidate(_probe, out BandPlacement withHover)) return withHover;
            }

            if (selection.Count > 0 && validator.TryGetUniqueCandidate(selection, out BandPlacement band))
                return band;

            return null;
        }

        private static bool Contains(IReadOnlyList<Peg> list, Peg peg)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == peg) return true;
            return false;
        }

        private Vector3 Lift(Vector3 point) => new Vector3(point.x, point.y + previewHeight, point.z);

        private void HideAll()
        {
            if (_selectionLine != null) _selectionLine.positionCount = 0;
            if (_ghostLine != null) _ghostLine.positionCount = 0;
        }
    }
}
