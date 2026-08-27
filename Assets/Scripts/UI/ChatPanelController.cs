using System;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Triggle.UI
{
    /// <summary>
    /// The in-game chat panel: a collapsed tab on the left edge that opens into a message log and a grid
    /// of quick-chat phrases.
    /// </summary>
    /// <remarks>
    /// Collapsed by default, and it must stay that way. The board now fills most of the screen, so an
    /// always-open panel would sit on top of it - and because it is a raycast target, it would eat peg
    /// clicks in the area it covers. Opening it is a deliberate act, and the tab is small enough to sit
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

        [Header("Dependencies")]
        [SerializeField] private NetworkMatch networkMatch;
        [SerializeField] private PlayerColorPalette palette;

        [Header("Panel")]
        [Tooltip("The expanded panel. Hidden until the tab is tapped.")]
        [SerializeField] private GameObject panel;

        [Tooltip("The collapsed tab that opens the panel.")]
        [SerializeField] private Button openButton;

        [SerializeField] private Button closeButton;

        [Tooltip("Unread badge on the tab, shown when a message arrives while the panel is closed.")]
        [SerializeField] private GameObject unreadBadge;

        [Header("Log")]
        [Tooltip("Message lines, oldest first. The log holds exactly this many.")]
        [SerializeField] private TMP_Text[] logLines = new TMP_Text[6];

        [SerializeField] private TMP_Text hintLabel;

        [Header("Phrases")]
        [SerializeField] private PhraseButton[] phraseButtons = new PhraseButton[6];

        [Header("Appearance")]
        [SerializeField] private Color systemTextColor = new Color(0.40f, 0.45f, 0.55f);

        /// <summary>The log, oldest first. Capped at <see cref="logLines"/>.Length.</summary>
        private readonly List<string> _log = new List<string>(8);

        private bool _open;

        private void Awake()
        {
            if (networkMatch == null) networkMatch = FindObjectOfType<NetworkMatch>();
            if (palette == null) palette = PlayerColorPalette.Fallback;

            if (openButton != null) openButton.onClick.AddListener(Open);
            if (closeButton != null) closeButton.onClick.AddListener(Close);

            for (int i = 0; i < phraseButtons.Length; i++)
            {
                PhraseButton entry = phraseButtons[i];
                if (entry?.button == null) continue;

                int phraseId = i;
                entry.button.onClick.AddListener(() => Send(phraseId));
            }

            FillPhraseLabels();
            SetOpen(false);
        }

        private void OnDestroy()
        {
            if (openButton != null) openButton.onClick.RemoveAllListeners();
            if (closeButton != null) closeButton.onClick.RemoveAllListeners();

            for (int i = 0; i < phraseButtons.Length; i++)
                if (phraseButtons[i]?.button != null) phraseButtons[i].button.onClick.RemoveAllListeners();
        }

        private void OnEnable()
        {
            if (networkMatch != null) networkMatch.ChatReceived += HandleChatReceived;

            GameEvents.OnGameReset += HandleGameReset;
        }

        private void OnDisable()
        {
            if (networkMatch != null) networkMatch.ChatReceived -= HandleChatReceived;

            GameEvents.OnGameReset -= HandleGameReset;
        }

        // ------------------------------------------------------------------ open / close

        public void Open() => SetOpen(true);

        public void Close() => SetOpen(false);

        public void Toggle() => SetOpen(!_open);

        private void SetOpen(bool open)
        {
            _open = open;

            if (panel != null) panel.SetActive(open);
            if (openButton != null) openButton.gameObject.SetActive(!open);

            // Opening is what marks the log read, so the badge clears here rather than on send.
            if (open && unreadBadge != null) unreadBadge.SetActive(false);

            if (open) RefreshHint();
        }

        // ------------------------------------------------------------------ sending

        /// <summary>Sends a quick-chat phrase, and shows it locally straight away.</summary>
        /// <remarks>
        /// Echoed locally rather than waiting for the message to come back, because it never does:
        /// <see cref="LoopbackTransport"/> and every real transport deliver to the <i>other</i> peers
        /// only. Without the echo a player would tap a phrase and watch nothing happen.
        /// </remarks>
        public void Send(int phraseId)
        {
            if (!ChatPhrases.IsValid(phraseId)) return;

            int seat = networkMatch != null && networkMatch.IsOnline ? networkMatch.LocalSeat : 1;

            Append(seat, phraseId);

            if (networkMatch != null)
                networkMatch.SendChat(phraseId, ChatPhrases.Get(phraseId).Text);
        }

        // ------------------------------------------------------------------ receiving

        private void HandleChatReceived(int seat, int phraseId, string text)
        {
            Append(seat, phraseId);

            if (!_open && unreadBadge != null) unreadBadge.SetActive(true);
        }

        private void HandleGameReset()
        {
            _log.Clear();
            RefreshLog();
        }

        // ------------------------------------------------------------------ log

        private void Append(int seat, int phraseId)
        {
            ChatPhrases.Phrase phrase = ChatPhrases.Get(phraseId);

            var player = (PlayerId)Mathf.Clamp(seat, 1, SeatRoster.SeatCount);
            string name = PlayerProfiles.GetName(player, palette.GetDisplayName(player));
            Color color = palette.GetColor(player);

            // Names are already stripped of angle brackets by PlayerProfiles, so embedding one in a
            // rich-text tag here cannot break the label.
            _log.Add($"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{name}</color>  {phrase.Text}");

            while (_log.Count > logLines.Length) _log.RemoveAt(0);

            RefreshLog();
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
        }

        private void OnValidate()
        {
            if (logLines != null && logLines.Length == 0) logLines = new TMP_Text[6];
            if (phraseButtons != null && phraseButtons.Length == 0) phraseButtons = new PhraseButton[6];
        }
    }
}
