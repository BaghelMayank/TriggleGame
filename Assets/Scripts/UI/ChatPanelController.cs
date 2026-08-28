using System;
using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Triggle.UI
{
    /// <summary>
    /// The in-game chat panel: a tab on the left edge that opens a message log, a row of emoji, a grid
    /// of quick-chat phrases and a box to type in.
    /// </summary>
    /// <remarks>
    /// Collapsed by default, and it must stay that way. The board fills most of the screen, so an
    /// always-open panel would sit on top of it - and because it is a raycast target, it would eat peg
    /// clicks in the area it covers. Opening it is a deliberate act, and the tab is small enough to fit
    /// between the top-left and bottom-left HUD player cards without touching either.
    /// <para>
    /// Sending and receiving both go through <see cref="NetworkMatch"/>, so this class holds no
    /// reference to a transport and does not care which one is in use.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ChatPanelController : MonoBehaviour
    {
        /// <summary>One quick-chat button: the emote image and the phrase label beside it.</summary>
        [Serializable]
        public sealed class PhraseButton
        {
            public Button button;
            public Image emote;
            public TMP_Text label;
        }

        /// <summary>One emoji button. Its label carries the sprite tag rather than an image.</summary>
        [Serializable]
        public sealed class EmoteButton
        {
            public Button button;
            public TMP_Text glyph;
        }

        [Header("Dependencies")]
        [SerializeField] private NetworkMatch networkMatch;
        [SerializeField] private PlayerColorPalette palette;

        [Header("Panel")]
        [Tooltip("The expanded panel. Hidden until the tab is tapped.")]
        [SerializeField] private GameObject panel;

        [Tooltip("The collapsed tab that opens the panel.")]
        [SerializeField] private Button openButton;

        [SerializeField] private Button closeButton;

        [Tooltip("Unread dot on the tab. Pulses while there is something unread.")]
        [SerializeField] private GameObject unreadBadge;

        [Header("Log")]
        [Tooltip("Message lines, oldest first. The log holds exactly this many.")]
        [SerializeField] private TMP_Text[] logLines = new TMP_Text[5];

        [SerializeField] private TMP_Text hintLabel;

        [Header("Composing")]
        [SerializeField] private TMP_InputField messageInput;
        [SerializeField] private Button sendButton;

        [Header("Phrases")]
        [SerializeField] private PhraseButton[] phraseButtons = new PhraseButton[6];
        [SerializeField] private EmoteButton[] emoteButtons = new EmoteButton[6];

        [Header("Appearance")]
        [SerializeField] private Color systemTextColor = new Color(0.40f, 0.45f, 0.55f);

        [Tooltip("How fast the unread dot pulses, in cycles per second.")]
        [SerializeField, Min(0.1f)] private float badgePulseSpeed = 1.6f;

        /// <summary>The log, oldest first. Capped at <see cref="logLines"/>.Length.</summary>
        private readonly List<string> _log = new List<string>(8);

        private Coroutine _pulse;
        private bool _open;

        /// <summary>
        /// Raised for every message anyone sends, so the HUD can surface it over the board.
        /// </summary>
        public event Action<int, ChatKind, int, string> MessagePosted;

        private void Awake()
        {
            if (networkMatch == null) networkMatch = FindObjectOfType<NetworkMatch>();
            if (palette == null) palette = PlayerColorPalette.Fallback;

            if (openButton != null) openButton.onClick.AddListener(Open);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            if (sendButton != null) sendButton.onClick.AddListener(SendTyped);

            if (messageInput != null)
            {
                messageInput.characterLimit = NetMessage.MaxTextLength;
                messageInput.onSubmit.AddListener(_ => SendTyped());
            }

            for (int i = 0; i < phraseButtons.Length; i++)
            {
                PhraseButton entry = phraseButtons[i];
                if (entry?.button == null) continue;

                int phraseId = i;
                entry.button.onClick.AddListener(() => SendPhrase(phraseId));
            }

            for (int i = 0; i < emoteButtons.Length; i++)
            {
                EmoteButton entry = emoteButtons[i];
                if (entry?.button == null) continue;

                int emoteId = i;
                entry.button.onClick.AddListener(() => SendEmote(emoteId));
            }

            FillPhraseLabels();
            SetOpen(false);
        }

        private void OnDestroy()
        {
            if (openButton != null) openButton.onClick.RemoveAllListeners();
            if (closeButton != null) closeButton.onClick.RemoveAllListeners();
            if (sendButton != null) sendButton.onClick.RemoveAllListeners();
            if (messageInput != null) messageInput.onSubmit.RemoveAllListeners();

            for (int i = 0; i < phraseButtons.Length; i++)
                if (phraseButtons[i]?.button != null) phraseButtons[i].button.onClick.RemoveAllListeners();

            for (int i = 0; i < emoteButtons.Length; i++)
                if (emoteButtons[i]?.button != null) emoteButtons[i].button.onClick.RemoveAllListeners();
        }

        private void OnEnable()
        {
            if (networkMatch != null)
            {
                networkMatch.ChatReceived += HandleChatReceived;
                networkMatch.StatusChanged += HandleStatusChanged;
            }

            GameEvents.OnGameReset += HandleGameReset;
        }

        private void OnDisable()
        {
            if (networkMatch != null)
            {
                networkMatch.ChatReceived -= HandleChatReceived;
                networkMatch.StatusChanged -= HandleStatusChanged;
            }

            GameEvents.OnGameReset -= HandleGameReset;
            StopPulse();
        }

        // ------------------------------------------------------------------ open / close

        public void Open() => SetOpen(true);

        public void Close() => SetOpen(false);

        public void Toggle() => SetOpen(!_open);

        private void SetOpen(bool open)
        {
            _open = open;
            Apply();
        }

        /// <summary>
        /// Shows the chat only while a network session is running.
        /// </summary>
        /// <remarks>
        /// There is nobody to talk to in a hot-seat or vs-AI match - everyone who can read it is already
        /// looking at the same screen - so the tab is not just useless offline, it is a control that
        /// covers part of the board for nothing. Driven by the transport's state rather than by whether
        /// the panel has ever been opened, so it appears when a room connects and goes away when it ends.
        /// </remarks>
        private void Apply()
        {
            bool online = networkMatch != null && networkMatch.IsOnline;
            if (!online) _open = false;

            if (panel != null) panel.SetActive(online && _open);
            if (openButton != null) openButton.gameObject.SetActive(online && !_open);

            // Opening is what marks the log read, so the dot clears here rather than on send.
            if (!online || _open) StopPulse();

            if (online && _open) RefreshHint();
        }

        // ------------------------------------------------------------------ sending

        /// <summary>Sends a quick-chat phrase, and shows it locally straight away.</summary>
        /// <remarks>
        /// Echoed locally rather than waiting for the message to come back, because it never does: every
        /// transport delivers to the <i>other</i> peers only. Without the echo a player would tap a
        /// phrase and watch nothing happen.
        /// </remarks>
        public void SendPhrase(int phraseId)
        {
            if (!ChatPhrases.IsValid(phraseId)) return;

            int seat = LocalSeat();

            Append(seat, ChatKind.Phrase, phraseId, null);
            MessagePosted?.Invoke(seat, ChatKind.Phrase, phraseId, null);

            networkMatch?.SendChat(ChatKind.Phrase, phraseId, ChatPhrases.Get(phraseId).Text);
        }

        /// <summary>Sends an emoji.</summary>
        public void SendEmote(int emoteId)
        {
            if (!ChatEmotes.IsValid(emoteId)) return;

            int seat = LocalSeat();

            Append(seat, ChatKind.Emote, emoteId, null);
            MessagePosted?.Invoke(seat, ChatKind.Emote, emoteId, null);

            networkMatch?.SendChat(ChatKind.Emote, emoteId, null);
        }

        /// <summary>Sends whatever is typed in the box, and clears it.</summary>
        public void SendTyped()
        {
            if (messageInput == null) return;

            string text = Sanitize(messageInput.text);

            messageInput.SetTextWithoutNotify(string.Empty);
            messageInput.ActivateInputField();

            if (string.IsNullOrEmpty(text)) return;

            int seat = LocalSeat();

            Append(seat, ChatKind.Text, 0, text);
            MessagePosted?.Invoke(seat, ChatKind.Text, 0, text);

            networkMatch?.SendChat(ChatKind.Text, 0, text);
        }

        /// <summary>
        /// Trims a typed message and strips anything that would be read as markup.
        /// </summary>
        /// <remarks>
        /// The log is a rich-text label, so a typed "&lt;color=#ff0000&gt;" would otherwise be
        /// interpreted rather than shown - letting one player recolour or corrupt everyone else's log.
        /// Angle brackets go the same way player names already do.
        /// </remarks>
        private static string Sanitize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string trimmed = raw.Trim().Replace('<', ' ').Replace('>', ' ');
            if (trimmed.Length > NetMessage.MaxTextLength)
                trimmed = trimmed.Substring(0, NetMessage.MaxTextLength);

            return trimmed.Trim();
        }

        // ------------------------------------------------------------------ receiving

        private void HandleChatReceived(int seat, ChatKind kind, int id, string text)
        {
            Append(seat, kind, id, text);

            MessagePosted?.Invoke(seat, kind, id, text);
            if (!_open) StartPulse();
        }

        private void HandleStatusChanged(SessionStatus status) => Apply();

        private void HandleGameReset()
        {
            _log.Clear();
            RefreshLog();
            Apply();
        }

        // ------------------------------------------------------------------ log

        private void Append(int seat, ChatKind kind, int id, string text)
        {
            var player = (PlayerId)Mathf.Clamp(seat, 1, SeatRoster.SeatCount);
            Color color = palette.GetColor(player);

            string body = kind switch
            {
                ChatKind.Emote => ChatEmotes.Tag(id),
                ChatKind.Text => text,
                _ => ChatPhrases.Get(id).Text
            };

            // Names are stripped of angle brackets by PlayerProfiles and typed text by Sanitize, so
            // neither can break out of the tag below.
            _log.Add($"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{NameOf(seat)}</color>  {body}");

            while (_log.Count > logLines.Length) _log.RemoveAt(0);

            RefreshLog();
        }

        /// <summary>
        /// The name to show for a seat.
        /// </summary>
        /// <remarks>
        /// The room roster first: it holds what each player called themselves on their own device.
        /// <see cref="PlayerProfiles"/> holds the names typed into <i>this</i> device's hot-seat lobby,
        /// which describe whoever is sharing this phone - reading them made every remote player's
        /// messages appear under a local name.
        /// </remarks>
        private string NameOf(int seat)
        {
            if (networkMatch != null && networkMatch.TryGetSeatName(seat, out string online)) return online;

            var player = (PlayerId)Mathf.Clamp(seat, 1, SeatRoster.SeatCount);
            return PlayerProfiles.GetName(player, palette.GetDisplayName(player));
        }

        private int LocalSeat()
        {
            int seat = networkMatch != null ? networkMatch.LocalSeat : 0;
            return seat > 0 ? seat : 1;
        }

        private void RefreshLog()
        {
            // Newest at the bottom, so the log fills upward and the last line is where the eye already is.
            int firstUsed = logLines.Length - _log.Count;

            for (int i = 0; i < logLines.Length; i++)
            {
                if (logLines[i] == null) continue;

                logLines[i].text = i < firstUsed ? string.Empty : _log[i - firstUsed];
            }

            RefreshHint();
        }

        private void RefreshHint()
        {
            if (hintLabel == null) return;

            bool online = networkMatch != null && networkMatch.IsOnline;

            hintLabel.color = systemTextColor;
            hintLabel.text = online
                ? string.Empty
                : "No one else is connected - these are only visible to you.";
        }

        private void FillPhraseLabels()
        {
            for (int i = 0; i < phraseButtons.Length; i++)
            {
                PhraseButton entry = phraseButtons[i];
                if (entry == null) continue;

                bool defined = ChatPhrases.IsValid(i);

                if (entry.button != null) entry.button.gameObject.SetActive(defined);
                if (!defined) continue;

                if (entry.label != null) entry.label.text = ChatPhrases.Get(i).Text;
            }

            for (int i = 0; i < emoteButtons.Length; i++)
            {
                EmoteButton entry = emoteButtons[i];
                if (entry == null) continue;

                bool defined = ChatEmotes.IsValid(i);

                if (entry.button != null) entry.button.gameObject.SetActive(defined);
                if (defined && entry.glyph != null) entry.glyph.text = ChatEmotes.Tag(i);
            }
        }

        // ------------------------------------------------------------------ unread dot

        /// <summary>
        /// Starts the unread dot pulsing.
        /// </summary>
        /// <remarks>
        /// A dot rather than a banner: chat arrives mid-turn, and anything larger would cover the board
        /// at the moment a player is reading it. The pulse is what makes something 18 units across
        /// noticeable without it having to be big.
        /// </remarks>
        private void StartPulse()
        {
            if (unreadBadge == null) return;

            unreadBadge.SetActive(true);

            if (_pulse == null && isActiveAndEnabled) _pulse = StartCoroutine(PulseRoutine());
        }

        private void StopPulse()
        {
            if (_pulse != null)
            {
                StopCoroutine(_pulse);
                _pulse = null;
            }

            if (unreadBadge == null) return;

            unreadBadge.transform.localScale = Vector3.one;
            unreadBadge.SetActive(false);
        }

        private IEnumerator PulseRoutine()
        {
            Transform badge = unreadBadge.transform;

            while (true)
            {
                float wave = Mathf.Sin(Time.unscaledTime * badgePulseSpeed * Mathf.PI * 2f);
                badge.localScale = Vector3.one * (1f + wave * 0.22f);

                yield return null;
            }
        }

        private void OnValidate()
        {
            if (logLines != null && logLines.Length == 0) logLines = new TMP_Text[5];
            if (phraseButtons != null && phraseButtons.Length == 0) phraseButtons = new PhraseButton[6];
            if (emoteButtons != null && emoteButtons.Length == 0) emoteButtons = new EmoteButton[6];
        }
    }
}
