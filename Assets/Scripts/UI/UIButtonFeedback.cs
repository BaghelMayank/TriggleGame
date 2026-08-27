using Triggle.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Triggle.UI
{
    /// <summary>
    /// Gives any UI control tactile feedback: it lifts on hover, presses in on click, and raises
    /// <see cref="GameEvents.OnUiClick"/> so audio can respond without a direct reference.
    /// </summary>
    /// <remarks>
    /// Works on touch as well as mouse - on mobile the pointer-enter/exit events simply do not fire,
    /// leaving the press animation, which is the part that matters there.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class UIButtonFeedback : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        [Header("Scale")]
        [SerializeField, Min(1f)] private float hoverScale = 1.045f;
        [SerializeField, Range(0.8f, 1f)] private float pressedScale = 0.955f;

        [Tooltip("Higher is snappier. Frame-rate independent.")]
        [SerializeField, Min(1f)] private float sharpness = 18f;

        [Header("Tint")]
        [Tooltip("Brightness multiplier applied to the target graphic while hovered.")]
        [SerializeField, Range(1f, 1.6f)] private float hoverBrightness = 1.12f;

        [Header("Audio")]
        [Tooltip("Raise the global UI click event when this control is released.")]
        [SerializeField] private bool playClickSound = true;

        private Graphic _graphic;
        private Selectable _selectable;
        private Vector3 _baseScale;
        private Color _baseColor;
        private bool _hovered;
        private bool _pressed;

        private void Awake()
        {
            _selectable = GetComponent<Selectable>();
            _graphic = _selectable != null && _selectable.targetGraphic != null
                ? _selectable.targetGraphic
                : GetComponent<Graphic>();

            _baseScale = transform.localScale;
            if (_graphic != null) _baseColor = _graphic.color;
        }

        private void OnDisable()
        {
            // Reset, otherwise a control hidden mid-hover reappears stuck in the hovered look.
            _hovered = false;
            _pressed = false;

            transform.localScale = _baseScale;
            if (_graphic != null) _graphic.color = _baseColor;
        }

        private void Update()
        {
            bool interactable = _selectable == null || _selectable.IsInteractable();

            float targetScale = !interactable ? 1f
                : _pressed ? pressedScale
                : _hovered ? hoverScale
                : 1f;

            float t = 1f - Mathf.Exp(-sharpness * Time.unscaledDeltaTime);
            transform.localScale = Vector3.Lerp(transform.localScale, _baseScale * targetScale, t);

            if (_graphic == null) return;

            Color targetColor = _baseColor;
            if (interactable && (_hovered || _pressed))
            {
                float brightness = _pressed ? 1f : hoverBrightness;
                targetColor = new Color(
                    Mathf.Clamp01(_baseColor.r * brightness),
                    Mathf.Clamp01(_baseColor.g * brightness),
                    Mathf.Clamp01(_baseColor.b * brightness),
                    _baseColor.a);
            }

            _graphic.color = Color.Lerp(_graphic.color, targetColor, t);
        }

        public void OnPointerEnter(PointerEventData eventData) => _hovered = true;

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            _pressed = false;
        }

        public void OnPointerDown(PointerEventData eventData) => _pressed = true;

        public void OnPointerUp(PointerEventData eventData)
        {
            bool wasPressed = _pressed;
            _pressed = false;

            bool interactable = _selectable == null || _selectable.IsInteractable();
            if (wasPressed && interactable && playClickSound) GameEvents.RaiseUiClick();
        }
    }
}
