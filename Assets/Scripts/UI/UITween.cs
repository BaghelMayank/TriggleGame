using System.Collections;
using UnityEngine;

namespace Triggle.UI
{
    /// <summary>
    /// Small, dependency-free tween helpers for UI panels. Coroutine-based so nothing runs when the
    /// owning object is disabled, and frame-rate independent.
    /// </summary>
    public static class UITween
    {
        /// <summary>Ease-out-cubic: fast start, soft landing. Good default for panels appearing.</summary>
        public static float EaseOut(float t) => 1f - Mathf.Pow(1f - Mathf.Clamp01(t), 3f);

        /// <summary>Ease-in-out-cubic, for transitions that both start and end at rest.</summary>
        public static float EaseInOut(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
        }

        /// <summary>Overshoots past 1 then settles back, giving UI a bit of spring.</summary>
        public static float EaseOutBack(float t, float overshoot = 1.70158f)
        {
            t = Mathf.Clamp01(t) - 1f;
            return 1f + (overshoot + 1f) * t * t * t + overshoot * t * t;
        }

        /// <summary>
        /// Fades a panel in and enables interaction. Also lifts the panel slightly as it appears, which
        /// reads as much more responsive than a plain alpha fade.
        /// </summary>
        public static IEnumerator FadeIn(CanvasGroup group, float duration, float riseDistance = 26f)
        {
            if (group == null) yield break;

            group.gameObject.SetActive(true);
            group.blocksRaycasts = true;
            group.interactable = true;

            var rect = group.transform as RectTransform;
            Vector2 target = rect != null ? rect.anchoredPosition : Vector2.zero;
            Vector2 from = target - new Vector2(0f, riseDistance);

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = EaseOut(elapsed / duration);

                group.alpha = t;
                if (rect != null) rect.anchoredPosition = Vector2.LerpUnclamped(from, target, t);

                yield return null;
            }

            group.alpha = 1f;
            if (rect != null) rect.anchoredPosition = target;
        }

        /// <summary>Fades a panel out, disables interaction and deactivates it when finished.</summary>
        public static IEnumerator FadeOut(CanvasGroup group, float duration, bool deactivate = true)
        {
            if (group == null) yield break;

            group.blocksRaycasts = false;
            group.interactable = false;

            float startAlpha = group.alpha;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                group.alpha = Mathf.Lerp(startAlpha, 0f, EaseInOut(elapsed / duration));
                yield return null;
            }

            group.alpha = 0f;
            if (deactivate) group.gameObject.SetActive(false);
        }

        /// <summary>Scale punch, for score changes and turn handovers.</summary>
        public static IEnumerator Punch(Transform target, float strength = 0.18f, float duration = 0.22f)
        {
            if (target == null) yield break;

            Vector3 baseScale = Vector3.one;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);

                // One sine lobe: out and back with no second curve needed.
                float bump = Mathf.Sin(t * Mathf.PI) * strength;
                if (target == null) yield break;
                target.localScale = baseScale * (1f + bump);

                yield return null;
            }

            if (target != null) target.localScale = baseScale;
        }

        /// <summary>Sets a panel to its hidden state instantly, without a coroutine.</summary>
        public static void SetHidden(CanvasGroup group, bool deactivate = true)
        {
            if (group == null) return;

            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;
            if (deactivate) group.gameObject.SetActive(false);
        }

        /// <summary>Sets a panel to its fully visible state instantly.</summary>
        public static void SetVisible(CanvasGroup group)
        {
            if (group == null) return;

            group.gameObject.SetActive(true);
            group.alpha = 1f;
            group.blocksRaycasts = true;
            group.interactable = true;
        }
    }
}
