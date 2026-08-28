using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.Net;
using TMPro;
using UnityEngine;

namespace Triggle.UI
{
    /// <summary>
    /// Floats a burst of emoji up the right-hand side of the board when anyone reacts, the way a live
    /// stream does.
    /// </summary>
    /// <remarks>
    /// A burst rather than one icon, because a single static popup reads as a notification while a
    /// stream of them reads as a reaction - which is the point of sending one mid-turn. Everyone in the
    /// room sees the same burst, including the sender, so a reaction feels shared rather than sent.
    /// <para>
    /// It has to be cheap and it has to be harmless. The labels come from a pool that only ever grows to
    /// <see cref="PoolLimit"/>, so a player holding down an emoji cannot spawn objects without bound;
    /// nothing here is a raycast target, so a reaction drifting over the board can never swallow a peg
    /// click; and every coroutine is stopped on disable, so leaving the match cannot leave one running.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class EmoteReactionController : MonoBehaviour
    {
        /// <summary>Most labels the pool will ever hold, however fast reactions arrive.</summary>
        private const int PoolLimit = 48;

        [Header("Dependencies")]
        [SerializeField] private ChatPanelController chat;
        [SerializeField] private PlayerColorPalette palette;
        [SerializeField] private NetworkMatch networkMatch;

        [Header("Stage")]
        [Tooltip("The area reactions rise through. They start at its bottom and leave at its top.")]
        [SerializeField] private RectTransform stage;

        [Tooltip("Hidden label cloned for each floating emoji.")]
        [SerializeField] private TMP_Text template;

        [Tooltip("Names whoever just reacted, under the rising emoji.")]
        [SerializeField] private TMP_Text captionLabel;

        [SerializeField] private CanvasGroup captionGroup;

        [Header("Burst")]
        [Tooltip("Emoji released per reaction.")]
        [SerializeField, Range(1, 20)] private int burstCount = 8;

        [Tooltip("Seconds between one emoji in a burst and the next.")]
        [SerializeField, Min(0f)] private float burstStagger = 0.07f;

        [Header("Flight")]
        [SerializeField] private Vector2 riseSeconds = new Vector2(2.0f, 3.0f);
        [SerializeField] private Vector2 sizeRange = new Vector2(0.75f, 1.25f);

        [Tooltip("How far an emoji wanders left and right as it climbs, in canvas units.")]
        [SerializeField, Min(0f)] private float swayAmount = 34f;

        [Tooltip("Spread of launch positions across the bottom of the stage.")]
        [SerializeField, Min(0f)] private float launchSpread = 80f;

        [Tooltip("How long the sender's name stays up.")]
        [SerializeField, Min(0.2f)] private float captionSeconds = 2.2f;

        private readonly List<TMP_Text> _pool = new List<TMP_Text>(PoolLimit);
        private readonly List<TMP_Text> _idle = new List<TMP_Text>(PoolLimit);

        private Coroutine _caption;

        private void Awake()
        {
            if (chat == null) chat = FindObjectOfType<ChatPanelController>();
            if (networkMatch == null) networkMatch = FindObjectOfType<NetworkMatch>();
            if (palette == null) palette = PlayerColorPalette.Fallback;

            if (template != null) template.gameObject.SetActive(false);
            if (captionGroup != null) captionGroup.alpha = 0f;
        }

        private void OnEnable()
        {
            if (chat != null) chat.EmotePosted += React;

            GameEvents.OnGameReset += StopEverything;
        }

        private void OnDisable()
        {
            if (chat != null) chat.EmotePosted -= React;

            GameEvents.OnGameReset -= StopEverything;
            StopEverything();
        }

        /// <summary>Releases a burst of one emoji, and names who sent it.</summary>
        public void React(int seat, int emoteId)
        {
            if (!ChatEmotes.IsValid(emoteId) || stage == null || template == null) return;
            if (!isActiveAndEnabled) return;

            StartCoroutine(BurstRoutine(ChatEmotes.Tag(emoteId)));
            ShowCaption(seat);
        }

        private IEnumerator BurstRoutine(string tag)
        {
            for (int i = 0; i < burstCount; i++)
            {
                Launch(tag);

                if (burstStagger > 0f) yield return new WaitForSeconds(burstStagger);
            }
        }

        private void Launch(string tag)
        {
            TMP_Text label = Take();
            if (label == null) return;   // pool at its limit; drop this one rather than grow

            label.text = tag;
            label.gameObject.SetActive(true);

            StartCoroutine(FlyRoutine(label));
        }

        /// <summary>
        /// Carries one emoji from the bottom of the stage to the top, swaying as it climbs.
        /// </summary>
        /// <remarks>
        /// The pop at the start and the fade at the end are what stop it reading as a sprite sliding up
        /// a rail. Each one gets its own duration, size, sway and phase, so a burst looks like a handful
        /// of separate reactions rather than one animation played eight times.
        /// </remarks>
        private IEnumerator FlyRoutine(TMP_Text label)
        {
            RectTransform rect = label.rectTransform;

            float height = stage.rect.height;
            float duration = Random.Range(riseSeconds.x, riseSeconds.y);
            float scale = Random.Range(sizeRange.x, sizeRange.y);
            float startX = Random.Range(-launchSpread, launchSpread);
            float sway = Random.Range(-swayAmount, swayAmount);
            float phase = Random.Range(0f, Mathf.PI * 2f);
            float wobbles = Random.Range(1.4f, 2.6f);

            var colour = label.color;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);

                rect.anchoredPosition = new Vector2(
                    startX + Mathf.Sin(phase + t * Mathf.PI * 2f * wobbles) * sway,
                    t * height);

                // Pops in over the first eighth of the climb, then holds its size.
                float pop = t < 0.125f ? UITween.EaseOutBack(t / 0.125f, 2.4f) : 1f;
                rect.localScale = Vector3.one * (scale * pop);

                // Fades over the last third, so it thins out near the top rather than blinking off.
                colour.a = t < 0.66f ? 1f : Mathf.Clamp01(1f - (t - 0.66f) / 0.34f);
                label.color = colour;

                yield return null;
            }

            Release(label);
        }

        // ------------------------------------------------------------------ caption

        private void ShowCaption(int seat)
        {
            if (captionLabel == null) return;

            var player = (PlayerId)Mathf.Clamp(seat, 1, SeatRoster.SeatCount);
            Color colour = palette.GetColor(player);

            captionLabel.text = $"<color=#{ColorUtility.ToHtmlStringRGB(colour)}>{NameOf(seat)}</color>";

            if (_caption != null) StopCoroutine(_caption);
            _caption = StartCoroutine(CaptionRoutine());
        }

        private IEnumerator CaptionRoutine()
        {
            if (captionGroup != null) captionGroup.alpha = 1f;

            yield return new WaitForSeconds(captionSeconds);

            float elapsed = 0f;
            while (elapsed < 0.4f)
            {
                elapsed += Time.deltaTime;
                if (captionGroup != null) captionGroup.alpha = Mathf.Clamp01(1f - elapsed / 0.4f);

                yield return null;
            }

            if (captionGroup != null) captionGroup.alpha = 0f;
            _caption = null;
        }

        /// <remarks>
        /// The room roster first, for the same reason the HUD and the chat log use it:
        /// <see cref="PlayerProfiles"/> describes whoever is sharing <i>this</i> device.
        /// </remarks>
        private string NameOf(int seat)
        {
            if (networkMatch != null && networkMatch.TryGetSeatName(seat, out string online)) return online;

            var player = (PlayerId)Mathf.Clamp(seat, 1, SeatRoster.SeatCount);
            return PlayerProfiles.GetName(player, palette.GetDisplayName(player));
        }

        // ------------------------------------------------------------------ pool

        private TMP_Text Take()
        {
            if (_idle.Count > 0)
            {
                TMP_Text reused = _idle[_idle.Count - 1];
                _idle.RemoveAt(_idle.Count - 1);

                return reused;
            }

            if (_pool.Count >= PoolLimit) return null;

            var clone = Instantiate(template, stage);
            clone.name = $"Reaction{_pool.Count}";

            _pool.Add(clone);
            return clone;
        }

        private void Release(TMP_Text label)
        {
            label.gameObject.SetActive(false);
            label.rectTransform.localScale = Vector3.one;

            var colour = label.color;
            colour.a = 1f;
            label.color = colour;

            _idle.Add(label);
        }

        private void StopEverything()
        {
            StopAllCoroutines();
            _caption = null;

            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i] == null) continue;
                if (!_pool[i].gameObject.activeSelf) continue;

                Release(_pool[i]);
            }

            if (captionGroup != null) captionGroup.alpha = 0f;
        }
    }
}
