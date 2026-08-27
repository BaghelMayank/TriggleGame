using Triggle.Core;
using UnityEngine;

namespace Triggle.UI
{
    /// <summary>
    /// Insets a full-screen RectTransform to the display's safe area, so no control ends up under a
    /// notch, a punch-hole camera or the gesture bar.
    /// </summary>
    /// <remarks>
    /// In landscape - the only orientation this game ships in - a cutout takes a bite out of the
    /// <i>side</i> of the screen rather than the top, which is where the HUD's corner player cards and
    /// the pause button live. That is what this protects.
    /// <para>
    /// Works in normalised anchors, so it is independent of the canvas scale factor and correct at any
    /// resolution without converting units itself. It expects its parent to cover the whole screen.
    /// </para>
    /// <para>
    /// Panels put their <b>content</b> inside one of these and leave their <b>scrim</b> outside it. A
    /// scrim clipped to the safe area would leave the board showing in the strip beside a notch, which
    /// reads as a rendering bug; content that ignores the safe area gets a button eaten by the cutout,
    /// which is worse. The scrim is also forced to the back of its parent's draw order - see
    /// <c>TriggleUIBuilder.AddScrim</c> - because a full-screen raycast target drawn after the content
    /// swallows every click aimed at it.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [ExecuteAlways]
    public sealed class CanvasSafeArea : MonoBehaviour
    {
        [Tooltip("Apply the inset horizontally. This is the one that matters in landscape.")]
        [SerializeField] private bool insetHorizontal = true;

        [Tooltip("Apply the inset vertically.")]
        [SerializeField] private bool insetVertical = true;

        private RectTransform _rect;
        private Rect _appliedSafeArea;
        private Vector2Int _appliedSize = new Vector2Int(-1, -1);

        private void Awake() => Apply();

        /// <summary>Panels are switched off while hidden, so a rotation missed then is caught here.</summary>
        private void OnEnable() => Apply();

        private void Update()
        {
            if (ScreenMetrics.Size == _appliedSize && ScreenMetrics.SafeArea == _appliedSafeArea) return;

            Apply();
        }

        /// <summary>Re-reads the safe area and re-insets this rect.</summary>
        public void Apply()
        {
            if (_rect == null) _rect = GetComponent<RectTransform>();
            if (_rect == null) return;

            Vector2Int size = ScreenMetrics.Size;
            if (size.x <= 0 || size.y <= 0) return;

            Rect safe = ScreenMetrics.SafeArea;

            // A device that reports nothing useful (or the editor Game view) yields the full screen;
            // guard anyway so a bad report cannot collapse the UI to zero size.
            if (safe.width <= 0f || safe.height <= 0f) safe = new Rect(0f, 0f, size.x, size.y);

            _appliedSize = size;
            _appliedSafeArea = safe;

            Vector2 min = new Vector2(safe.xMin / size.x, safe.yMin / size.y);
            Vector2 max = new Vector2(safe.xMax / size.x, safe.yMax / size.y);

            if (!insetHorizontal) { min.x = 0f; max.x = 1f; }
            if (!insetVertical) { min.y = 0f; max.y = 1f; }

            min.x = Mathf.Clamp01(min.x);
            min.y = Mathf.Clamp01(min.y);
            max.x = Mathf.Clamp01(max.x);
            max.y = Mathf.Clamp01(max.y);

            if (max.x <= min.x || max.y <= min.y) return;   // nonsensical report: leave the rect alone

            _rect.anchorMin = min;
            _rect.anchorMax = max;
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;
            _rect.pivot = new Vector2(0.5f, 0.5f);
        }
    }
}
