using Triggle.Core;
using Triggle.Grid;
using UnityEngine;

namespace Triggle.Visuals
{
    /// <summary>
    /// Keeps the whole board inside the viewport, whatever radius the player picked in Settings and
    /// whatever shape the window is.
    /// </summary>
    /// <remarks>
    /// Board radius used to be a build-time constant, so the scene generator could bake one camera
    /// position and be done. It is a runtime choice now (Settings ▸ Board ▸ Board Size), and a position
    /// framed for radius 3 clips a radius 4 or 5 board off the edges of the screen. Framing therefore
    /// has to be recomputed whenever the lattice is regenerated.
    /// <para>
    /// <b>How the fit works.</b> Moving the camera backwards along its own forward axis leaves every
    /// point's camera-space <c>x</c> and <c>y</c> untouched and adds exactly that distance to <c>z</c>.
    /// A point is inside the frustum when <c>|x| &lt;= z·tanH</c> and <c>|y| &lt;= z·tanV</c>, so the
    /// distance needed to bring one point inside solves directly: <c>|x|/tanH - z</c>. Take the largest
    /// requirement over points around the board's rim and push back by that much - no search, no
    /// iteration, and correct for any pitch or aspect ratio. It stays correct when a sample starts
    /// behind the camera, because the expression is linear in <c>z</c> and does not care about its sign.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class BoardCameraRig : MonoBehaviour
    {
        /// <summary>Six slab corners, each at the board plane and at content height.</summary>
        private const int SampleCapacity = 12;

        [Header("Dependencies")]
        [SerializeField] private BoardManager board;

        [Tooltip("Supplies the slab radius. Optional - without it the peg ring plus Fallback Edge " +
                 "Padding is used instead.")]
        [SerializeField] private BoardVisuals boardVisuals;

        [Header("Framing")]
        [Tooltip("Downward tilt in degrees. 90 looks straight down; the default keeps the board " +
                 "readable in perspective without hiding the peg posts.")]
        [SerializeField, Range(20f, 89f)] private float pitch = 56f;

        [Tooltip("Fraction of the viewport left empty at the left and right edges.")]
        [SerializeField, Range(0f, 0.3f)] private float sidePadding = 0.04f;

        [Tooltip("Fraction of the viewport reserved at the top for HUD chrome - the TRIGGLE chip, the " +
                 "round counter and the pause button. The board is fitted below it.")]
        [SerializeField, Range(0f, 0.4f)] private float topMargin = 0.145f;

        [Tooltip("Fraction reserved at the bottom for the turn banner. Set this and Top Margin from " +
                 "the HUD's own geometry and the gap above and below the board comes out equal.")]
        [SerializeField, Range(0f, 0.4f)] private float bottomMargin = 0.143f;

        [Tooltip("Extra radius past the outermost pegs, in unit-edge lengths. Only used when no " +
                 "BoardVisuals is assigned; otherwise the real slab radius is read from it.")]
        [SerializeField, Min(0f)] private float fallbackEdgePadding = 1.1f;

        [Tooltip("How far the tallest thing standing on the board reaches - a peg post plus its head. " +
                 "Fitted at the peg ring, not the slab rim, because that is where the pegs actually are.")]
        [SerializeField, Min(0f)] private float contentHeight = 0.5f;

        [Tooltip("Re-fit when the window or Game view is resized. Costs one integer compare per frame.")]
        [SerializeField] private bool trackViewportChanges = true;

        private readonly Vector3[] _samples = new Vector3[SampleCapacity];
        private int _sampleCount;

        private Camera _camera;
        private int _framedWidth;
        private int _framedHeight;
        private bool _warnedOrthographic;

        /// <summary>Where the camera rests when nothing is shaking it. Valid after the first fit.</summary>
        public Vector3 RestingPosition { get; private set; }

        /// <summary>Viewport span the fit believes the board occupies. Diagnostics for the audit.</summary>
        public float FittedLow { get; private set; }

        /// <inheritdoc cref="FittedLow"/>
        public float FittedHigh { get; private set; }

        /// <summary>Offset applied along the camera's up axis to centre the board. Diagnostics.</summary>
        public float FittedRise { get; private set; }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            if (board == null) board = FindObjectOfType<BoardManager>();
            if (boardVisuals == null) boardVisuals = FindObjectOfType<BoardVisuals>();
        }

        private void OnEnable()
        {
            GameEvents.OnBoardGenerated += HandleBoardGenerated;
        }

        private void OnDisable()
        {
            GameEvents.OnBoardGenerated -= HandleBoardGenerated;
        }

        private void Start()
        {
            // Covers the case where the board was generated before this component subscribed.
            if (board != null && board.IsBuilt) Frame();
        }

        /// <remarks>
        /// The board geometry is rebuilt by <see cref="BoardVisuals"/> on the same event, and subscriber
        /// order is not guaranteed, so the slab radius may still be the previous board's when this runs.
        /// Deferred by one frame so the fit always reads the geometry that is actually on screen.
        /// </remarks>
        private void HandleBoardGenerated()
        {
            // The scene generator builds a board in edit mode too, where there are no coroutines.
            if (Application.isPlaying && isActiveAndEnabled) StartCoroutine(FrameNextFrame());
            else Frame();
        }

        private System.Collections.IEnumerator FrameNextFrame()
        {
            yield return null;
            Frame();
        }

        private void LateUpdate()
        {
            if (!trackViewportChanges) return;
            if (Screen.width == _framedWidth && Screen.height == _framedHeight) return;

            Frame();
        }

        /// <summary>
        /// Positions and aims the camera so the whole board fits, then announces the new resting
        /// position so <see cref="ClaimVfx"/> does not shake the camera back to a stale one.
        /// </summary>
        public void Frame()
        {
            if (_camera == null) _camera = GetComponent<Camera>();
            if (_camera == null || board == null || !board.IsBuilt) return;

            if (_camera.orthographic)
            {
                // The fit below is expressed in terms of field of view, which an orthographic camera
                // does not have. Say so once rather than silently leaving the board mis-framed.
                if (!_warnedOrthographic)
                {
                    _warnedOrthographic = true;
                    Debug.LogWarning($"{nameof(BoardCameraRig)}: the camera is orthographic. This rig " +
                                     "frames a perspective camera; the board will not be fitted.", this);
                }

                return;
            }

            float radius = ResolveBoardRadius();
            if (radius <= 0f) return;

            Vector3 center = board.transform.position;
            BuildSamples(center, radius);

            transform.rotation = Quaternion.Euler(pitch, 0f, 0f);
            Vector3 forward = transform.forward;
            Vector3 up = transform.up;

            float tanV = Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanH = tanV * _camera.aspect;
            if (tanV <= 0f || tanH <= 0f) return;

            // The target band in normalised device coordinates, where -1 is the bottom of the screen
            // and +1 the top. Asymmetric on purpose: the HUD owns a strip at each end.
            float topNdc = 2f * (1f - topMargin) - 1f;
            float bottomNdc = 2f * bottomMargin - 1f;
            float sideNdc = 1f - 2f * sidePadding;

            if (topNdc <= 0f || bottomNdc >= 0f || sideNdc <= 0f) return;   // margins overlap

            float bandCentre = (bottomMargin + 1f - topMargin) * 0.5f;

            float distance = radius * 2f;
            float rise = 0f;

            // Distance and rise are coupled - moving the camera up changes what has to fit, and pushing
            // it back changes how far up it needs to be - so they are solved by alternating. Four passes
            // is well past the point where the numbers stop moving.
            for (int pass = 0; pass < 4; pass++)
            {
                transform.position = center - forward * distance + up * rise;
                distance += RequiredPush(tanH, tanV, sideNdc, topNdc, bottomNdc);

                transform.position = center - forward * distance + up * rise;
                rise = SolveRise(tanV, rise, bandCentre);
            }

            // Centring is an approximation - it shifts every sample by the same amount, but perspective
            // does not - so finish with a pure distance pass. That one is exact, so the board is
            // guaranteed inside the band even if it ends up a pixel off centre.
            transform.position = center - forward * distance + up * rise;
            distance += Mathf.Max(0f, RequiredPush(tanH, tanV, sideNdc, topNdc, bottomNdc));

            transform.position = center - forward * distance + up * rise;

            RecordFit(tanV, rise);

            RestingPosition = transform.position;
            _framedWidth = Screen.width;
            _framedHeight = Screen.height;

            GameEvents.RaiseCameraReframed();
        }

        /// <summary>
        /// How much further back the camera must sit for every sample to clear the band.
        /// </summary>
        /// <remarks>
        /// Moving back along the view axis adds exactly that distance to each point's camera-space
        /// <c>z</c> and leaves <c>x</c> and <c>y</c> alone, so each constraint solves directly for the
        /// distance it needs and the answer is the largest of them. The bottom term divides by a
        /// negative, which flips the inequality - that is why it reads the same as the top term instead
        /// of being negated.
        /// </remarks>
        private float RequiredPush(float tanH, float tanV, float sideNdc, float topNdc, float bottomNdc)
        {
            float push = float.NegativeInfinity;

            for (int i = 0; i < _sampleCount; i++)
            {
                Vector3 local = transform.InverseTransformPoint(_samples[i]);

                float horizontal = Mathf.Abs(local.x) / (tanH * sideNdc) - local.z;
                float top = local.y / (tanV * topNdc) - local.z;
                float bottom = local.y / (tanV * bottomNdc) - local.z;

                push = Mathf.Max(push, Mathf.Max(horizontal, Mathf.Max(top, bottom)));
            }

            return push == float.NegativeInfinity ? 0f : push;
        }

        /// <summary>Captures where the fit ended up, so the audit can compare it against the real board.</summary>
        private void RecordFit(float tanV, float rise)
        {
            float low = float.PositiveInfinity;
            float high = float.NegativeInfinity;

            for (int i = 0; i < _sampleCount; i++)
            {
                Vector3 local = transform.InverseTransformPoint(_samples[i]);
                if (local.z <= 0.0001f) continue;

                float viewport = 0.5f + 0.5f * local.y / (local.z * tanV);

                low = Mathf.Min(low, viewport);
                high = Mathf.Max(high, viewport);
            }

            FittedLow = low;
            FittedHigh = high;
            FittedRise = rise;
        }

        /// <summary>
        /// The camera rise that leaves the same gap above the board as below it.
        /// </summary>
        /// <remarks>
        /// Solved rather than nudged. Shifting the camera along its own up axis moves every point's
        /// camera-space <c>y</c> by the same amount but each one's <i>viewport</i> position by
        /// <c>1/depth</c> of it - so the near edge of the board travels much further up the screen than
        /// the far edge does. Correcting by an averaged depth therefore overshoots, which is what left
        /// the board riding high with the turn banner crowded underneath it.
        /// <para>
        /// Depth does not change when the camera moves along its up axis, so for a fixed pair of
        /// extremes this is a linear equation in the rise and solves in closed form. Only which samples
        /// are extreme can change, which is what the surrounding passes settle.
        /// </para>
        /// </remarks>
        private float SolveRise(float tanV, float currentRise, float bandCentre)
        {
            float lowest = float.PositiveInfinity;
            float highest = float.NegativeInfinity;

            float lowY = 0f, lowZ = 1f, highY = 0f, highZ = 1f;

            for (int i = 0; i < _sampleCount; i++)
            {
                Vector3 local = transform.InverseTransformPoint(_samples[i]);
                if (local.z <= 0.0001f) continue;

                float viewport = 0.5f + 0.5f * local.y / (local.z * tanV);

                // Recorded at zero rise, so the equation below is in absolute terms.
                if (viewport < lowest) { lowest = viewport; lowY = local.y + currentRise; lowZ = local.z; }
                if (viewport > highest) { highest = viewport; highY = local.y + currentRise; highZ = local.z; }
            }

            if (lowest > highest) return currentRise;

            float a = 0.5f / (lowZ * tanV);
            float b = 0.5f / (highZ * tanV);
            if (a + b <= 0f) return currentRise;

            // vLow + vHigh == 2 * bandCentre, expanded and solved for the rise.
            return (a * lowY + b * highY - (2f * bandCentre - 1f)) / (a + b);
        }

        /// <summary>
        /// The board's silhouette: the slab hexagon on the board plane, and a second, smaller hexagon
        /// at peg-top height covering everything that stands up off it.
        /// </summary>
        /// <remarks>
        /// Two different radii on purpose. Nothing tall sits at the slab's rim - the outermost pegs are
        /// a full slab-padding further in - so lifting the <i>rim</i> corners invents headroom that no
        /// geometry occupies, and the fit then reserves screen space for it. That phantom headroom is
        /// what left more room above the board than below it.
        /// <para>
        /// Six points per ring rather than a circle of samples: the slab is a hexagon, and a circle
        /// through its corners bulges up to 13% past its flat sides, so fitting to one threw away that
        /// much of the screen. Perspective maps a convex polygon to a convex polygon with vertices going
        /// to vertices, so these twelve points bound every pixel of the board exactly.
        /// </para>
        /// </remarks>
        private void BuildSamples(Vector3 center, float slabRadius)
        {
            _sampleCount = 0;

            float pegRadius = AxialMath.UnitEdgeLength(board.PegSpacing) * board.Radius;

            for (int i = 0; i < 6; i++)
            {
                // Matches BoardVisuals.HexCorner, so these are the slab's real corners.
                float angle = i * 60f * Mathf.Deg2Rad;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                _samples[_sampleCount++] =
                    center + new Vector3(cos * slabRadius, 0f, sin * slabRadius);

                _samples[_sampleCount++] =
                    center + new Vector3(cos * pegRadius, contentHeight, sin * pegRadius);
            }
        }

        /// <summary>
        /// The board's outer radius: the real slab radius when <see cref="BoardVisuals"/> has built one,
        /// otherwise the peg ring plus a configured margin.
        /// </summary>
        private float ResolveBoardRadius()
        {
            if (boardVisuals != null && boardVisuals.OuterRadius > 0f) return boardVisuals.OuterRadius;

            float unit = AxialMath.UnitEdgeLength(board.PegSpacing);
            return unit * (board.Radius + fallbackEdgePadding);
        }
    }
}
