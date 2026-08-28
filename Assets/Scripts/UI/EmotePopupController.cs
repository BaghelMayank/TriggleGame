using System.Collections;
using Triggle.Core;
using Triggle.Net;
using TMPro;
using UnityEngine;

namespace Triggle.UI
{
    /// <summary>
    /// Floats an emoji and the name of whoever sent it down the right-hand side of the board.
    /// </summary>
    /// <remarks>
    /// So an emoji lands without anyone having to have the chat panel open. Chat is collapsed by default
    /// and most players will leave it that way mid-match, which would make emoji invisible to exactly the
    /// people they are aimed at.
    /// <para>
    /// The right edge, mirroring the chat tab on the left, and inside the band between the corner player
    /// cards so it covers neither. Nothing here takes input - the slots have no raycast targets - so a
    /// popup can never swallow a peg click on the board underneath.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class EmotePopupController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private ChatPanelController chat;
        [SerializeField] private PlayerColorPalette palette;
        [SerializeField] private NetworkMatch networkMatch;

        [Header("Slots")]
        [Tooltip("Newest is shown in the last slot; older ones move up. All optional.")]
        [SerializeField] private CanvasGroup[] slotGroups = new CanvasGroup[3];

        [SerializeField] private TMP_Text[] slotLabels = new TMP_Text[3];

        [Header("Timing")]
        [SerializeField, Min(0.5f)] private float holdSeconds = 3.5f;
        [SerializeField, Min(0.1f)] private float fadeSeconds = 0.6f;

        private Coroutine[] _fades;

        private void Awake()
        {
            if (chat == null) chat = FindObjectOfType<ChatPanelController>();
            if (networkMatch == null) networkMatch = FindObjectOfType<NetworkMatch>();
            if (palette == null) palette = PlayerColorPalette.Fallback;

            _fades = new Coroutine[slotGroups.Length];

            for (int i = 0; i < slotGroups.Length; i++)
                if (slotGroups[i] != null) slotGroups[i].alpha = 0f;
        }

        private void OnEnable()
        {
            if (chat != null) chat.EmotePosted += Show;

            GameEvents.OnGameReset += ClearAll;
        }

        private void OnDisable()
        {
            if (chat != null) chat.EmotePosted -= Show;

            GameEvents.OnGameReset -= ClearAll;
        }

        /// <summary>Pushes an emoji into the newest slot, shuffling the others up.</summary>
        public void Show(int seat, int emoteId)
        {
            if (!ChatEmotes.IsValid(emoteId) || slotLabels == null || slotLabels.Length == 0) return;

            // Oldest first: move each line up one, so the newest always lands in the last slot and the
            // stack reads top-to-bottom in the order things were said.
            for (int i = 0; i < slotLabels.Length - 1; i++)
            {
                if (slotLabels[i] == null || slotLabels[i + 1] == null) continue;

                slotLabels[i].text = slotLabels[i + 1].text;

                if (slotGroups[i] != null && slotGroups[i + 1] != null)
                    slotGroups[i].alpha = slotGroups[i + 1].alpha;
            }

            int last = slotLabels.Length - 1;
            if (slotLabels[last] == null) return;

            var player = (PlayerId)Mathf.Clamp(seat, 1, SeatRoster.SeatCount);
            Color color = palette.GetColor(player);

            slotLabels[last].text =
                $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{NameOf(seat)}</color>  " +
                ChatEmotes.Tag(emoteId);

            RestartFade(last);
        }

        private string NameOf(int seat)
        {
            if (networkMatch != null && networkMatch.TryGetSeatName(seat, out string online)) return online;

            var player = (PlayerId)Mathf.Clamp(seat, 1, SeatRoster.SeatCount);
            return PlayerProfiles.GetName(player, palette.GetDisplayName(player));
        }

        private void RestartFade(int index)
        {
            if (slotGroups == null || index >= slotGroups.Length || slotGroups[index] == null) return;

            if (_fades[index] != null) StopCoroutine(_fades[index]);

            slotGroups[index].alpha = 1f;
            if (isActiveAndEnabled) _fades[index] = StartCoroutine(FadeRoutine(slotGroups[index], index));
        }

        private IEnumerator FadeRoutine(CanvasGroup group, int index)
        {
            yield return new WaitForSeconds(holdSeconds);

            float elapsed = 0f;
            while (elapsed < fadeSeconds)
            {
                elapsed += Time.deltaTime;
                group.alpha = Mathf.Clamp01(1f - elapsed / fadeSeconds);

                yield return null;
            }

            group.alpha = 0f;
            _fades[index] = null;
        }

        private void ClearAll()
        {
            for (int i = 0; i < slotGroups.Length; i++)
            {
                if (_fades != null && _fades[i] != null)
                {
                    StopCoroutine(_fades[i]);
                    _fades[i] = null;
                }

                if (slotGroups[i] != null) slotGroups[i].alpha = 0f;
                if (slotLabels[i] != null) slotLabels[i].text = string.Empty;
            }
        }

        private void OnValidate()
        {
            if (slotGroups != null && slotGroups.Length == 0) slotGroups = new CanvasGroup[3];
            if (slotLabels != null && slotLabels.Length == 0) slotLabels = new TMP_Text[3];
        }
    }
}
