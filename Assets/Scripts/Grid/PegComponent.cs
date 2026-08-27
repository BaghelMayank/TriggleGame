using Triggle.Core;
using UnityEngine;

namespace Triggle.Grid
{
    /// <summary>Visual/interaction states a peg can be driven into by the highlighter.</summary>
    public enum PegVisualState
    {
        /// <summary>Default resting look.</summary>
        Idle,

        /// <summary>A legal next pick for the current selection.</summary>
        Selectable,

        /// <summary>Pointer is over this peg.</summary>
        Hovered,

        /// <summary>Already part of the pending selection buffer.</summary>
        Selected,

        /// <summary>Cannot participate in any remaining legal band.</summary>
        Disabled
    }

    /// <summary>
    /// The scene-side face of a <see cref="Peg"/>. Owns the pick collider and animates colour and
    /// scale toward the target dictated by its current <see cref="PegVisualState"/>.
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    [DisallowMultipleComponent]
    public sealed class PegComponent : MonoBehaviour
    {
        [Header("Picking")]
        [Tooltip("Multiplies the collider radius so pegs stay comfortable to click without growing visually.")]
        [SerializeField, Min(1f)] private float hitRadiusMultiplier = 1.9f;

        [Header("State Colours")]
        [SerializeField] private Color idleColor = new Color(0.78f, 0.78f, 0.83f);
        [SerializeField] private Color selectableColor = new Color(0.55f, 0.90f, 1f);
        [SerializeField] private Color hoveredColor = new Color(1f, 0.97f, 0.70f);
        [SerializeField] private Color selectedColor = new Color(1f, 0.55f, 0.25f);
        [SerializeField] private Color disabledColor = new Color(0.34f, 0.34f, 0.38f);

        [Header("Animation")]
        [Tooltip("Extra scale applied on top of the base scale when hovered or selected.")]
        [SerializeField, Min(1f)] private float emphasisScale = 1.35f;

        [Tooltip("Higher is snappier. Interpolation is frame-rate independent.")]
        [SerializeField, Min(0.5f)] private float animationSharpness = 14f;

        [Tooltip("Emission strength used for selectable/selected states on URP Lit materials.")]
        [SerializeField, Min(0f)] private float emissionIntensity = 1.4f;

        private Renderer _renderer;
        private Material _materialInstance;
        private SphereCollider _collider;
        private Vector3 _baseScale;
        private Color _currentColor;
        private PegVisualState _state = PegVisualState.Idle;

        /// <summary>The data model this component renders. Assigned by <see cref="BoardManager"/>.</summary>
        public Peg Peg { get; private set; }

        public PegVisualState State => _state;

        private void Awake()
        {
            CacheReferences();
        }

        private void CacheReferences()
        {
            if (_collider == null) _collider = GetComponent<SphereCollider>();
            if (_renderer == null) _renderer = GetComponent<Renderer>();

            if (_baseScale == Vector3.zero) _baseScale = transform.localScale;

            if (_renderer != null && _materialInstance == null)
            {
                // Instantiate so per-peg tinting never touches the shared asset.
                _materialInstance = new Material(_renderer.sharedMaterial != null
                    ? _renderer.sharedMaterial
                    : MaterialUtility.CreateDefaultLitMaterial());
                _materialInstance.name = $"{name}_Mat";
                _materialInstance.hideFlags = HideFlags.DontSave;
                _renderer.material = _materialInstance;
            }
        }

        /// <summary>Links this view to its model and applies the initial idle look.</summary>
        public void Bind(Peg peg)
        {
            Peg = peg;
            CacheReferences();

            if (_collider != null)
            {
                _collider.radius = 0.5f * hitRadiusMultiplier;
                _collider.isTrigger = false;
            }

            _currentColor = idleColor;
            ApplyColor(_currentColor, 0f);
            transform.localScale = _baseScale;
        }

        /// <summary>Requests a new visual state. Cheap and idempotent; safe to call every frame.</summary>
        public void SetState(PegVisualState state)
        {
            _state = state;
        }

        private void Update()
        {
            if (_renderer == null && _materialInstance == null) return;

            float t = 1f - Mathf.Exp(-animationSharpness * Time.deltaTime);

            Color target = TargetColor(_state);
            _currentColor = Color.Lerp(_currentColor, target, t);

            float emphasis = _state == PegVisualState.Selected || _state == PegVisualState.Hovered
                ? emphasisScale
                : _state == PegVisualState.Selectable ? Mathf.Lerp(1f, emphasisScale, 0.4f) : 1f;

            transform.localScale = Vector3.Lerp(transform.localScale, _baseScale * emphasis, t);

            float glow = _state switch
            {
                PegVisualState.Selected => emissionIntensity,
                PegVisualState.Hovered => emissionIntensity * 0.8f,
                PegVisualState.Selectable => emissionIntensity * 0.45f,
                _ => 0f
            };

            ApplyColor(_currentColor, glow);
        }

        private Color TargetColor(PegVisualState state) => state switch
        {
            PegVisualState.Selectable => selectableColor,
            PegVisualState.Hovered => hoveredColor,
            PegVisualState.Selected => selectedColor,
            PegVisualState.Disabled => disabledColor,
            _ => idleColor
        };

        private void ApplyColor(Color color, float glow)
        {
            if (_materialInstance == null) return;
            MaterialUtility.SetColor(_materialInstance, color);
            MaterialUtility.SetEmission(_materialInstance, color * glow);
        }

        private void OnDestroy()
        {
            if (_materialInstance == null) return;
            if (Application.isPlaying) Destroy(_materialInstance);
            else DestroyImmediate(_materialInstance);
        }
    }
}
