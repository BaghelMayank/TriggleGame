using System.Threading.Tasks;
using Triggle.Core;
using Triggle.Gameplay;
using Triggle.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Triggle.UI
{
    /// <summary>
    /// The online room screen: host a room and read out its code, or type someone else's and join it.
    /// </summary>
    /// <remarks>
    /// Everything here is a thin shell over <see cref="UgsRoomService"/> and <see cref="NetworkMatch"/>.
    /// The one piece of real sequencing it owns is the start: the host must broadcast the rules
    /// <i>before</i> starting its own match, because radius and band length decide the band catalogue and
    /// a move is an index into it. Start first and the guests build a different board from a different
    /// radius, and every move after that lands on the wrong triangles.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MultiplayerPanelController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private UgsRoomService rooms;
        [SerializeField] private NetworkMatch networkMatch;
        [SerializeField] private GameFlowController flowController;
        [SerializeField] private MatchController matchController;
        [SerializeField] private MainMenuController mainMenu;
        [SerializeField] private PlayerColorPalette palette;

        [Header("Panel")]
        [SerializeField] private CanvasGroup panel;

        [Header("Host")]
        [SerializeField] private Button hostButton;
        [SerializeField] private TMP_Text roomCodeLabel;

        [Header("Join")]
        [SerializeField] private TMP_InputField codeInput;
        [SerializeField] private Button joinButton;

        [Header("Profile")]
        [Tooltip("The name other players see. Persisted, and used for every online match.")]
        [SerializeField] private TMP_InputField nameInput;

        [Header("Room")]
        [SerializeField] private TMP_Text[] playerRows = new TMP_Text[4];
        [SerializeField] private TMP_Text statusLabel;
        [SerializeField] private TMP_Text occupancyLabel;
        [SerializeField] private Button startButton;
        [SerializeField] private TMP_Text startLabel;
        [SerializeField] private Button leaveButton;
        [SerializeField] private Button backButton;

        [Header("Rules")]
        [Tooltip("Rounds the host sets for an online match.")]
        [SerializeField, Range(MatchState.MinRounds, MatchState.MaxRounds)] private int roundCount = 1;

        [Header("Appearance")]
        [SerializeField] private Color infoColor = new Color(0.62f, 0.68f, 0.78f);
        [SerializeField] private Color errorColor = new Color(0.98f, 0.45f, 0.42f);

        private bool _busy;

        private bool InRoom => rooms != null && !string.IsNullOrEmpty(rooms.RoomCode);

        private void Awake()
        {
            if (rooms == null) rooms = FindObjectOfType<UgsRoomService>();
            if (networkMatch == null) networkMatch = FindObjectOfType<NetworkMatch>();
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
            if (matchController == null) matchController = FindObjectOfType<MatchController>();
            if (mainMenu == null) mainMenu = FindObjectOfType<MainMenuController>();
            if (palette == null) palette = PlayerColorPalette.Fallback;

            if (hostButton != null) hostButton.onClick.AddListener(() => _ = HostAsync());
            if (joinButton != null) joinButton.onClick.AddListener(() => _ = JoinAsync());
            if (startButton != null) startButton.onClick.AddListener(StartMatch);
            if (leaveButton != null) leaveButton.onClick.AddListener(() => _ = LeaveAsync());
            if (backButton != null) backButton.onClick.AddListener(() => _ = BackAsync());

            if (codeInput != null) codeInput.characterLimit = 6;

            if (nameInput != null)
            {
                nameInput.characterLimit = PlayerProfiles.MaxNameLength;
                nameInput.onEndEdit.AddListener(CommitName);
            }
        }

        /// <summary>
        /// Saves the typed name, and tells the room about it if one is already open.
        /// </summary>
        /// <remarks>
        /// Re-announcing matters: a player who renames themselves after joining would otherwise still
        /// show up under the old name on every other device, with no way to correct it short of leaving.
        /// </remarks>
        private void CommitName(string value)
        {
            PlayerProfiles.DisplayName = value;

            if (nameInput != null) nameInput.SetTextWithoutNotify(PlayerProfiles.DisplayName);
            if (networkMatch != null) networkMatch.RenameLocalPlayer(LocalName());

            Refresh();
        }

        private void OnDestroy()
        {
            if (hostButton != null) hostButton.onClick.RemoveAllListeners();
            if (joinButton != null) joinButton.onClick.RemoveAllListeners();
            if (startButton != null) startButton.onClick.RemoveAllListeners();
            if (leaveButton != null) leaveButton.onClick.RemoveAllListeners();
            if (backButton != null) backButton.onClick.RemoveAllListeners();
            if (nameInput != null) nameInput.onEndEdit.RemoveAllListeners();
        }

        private void OnEnable()
        {
            if (rooms != null) rooms.Failed += ShowError;

            if (networkMatch != null)
            {
                networkMatch.RosterChanged += Refresh;
                networkMatch.MatchStartedByHost += HandleMatchStartedByHost;
                networkMatch.PlayerJoined += HandlePlayerJoined;
                networkMatch.Desynced += ShowError;
                networkMatch.StatusChanged += HandleStatusChanged;
            }

            Refresh();
        }

        private void OnDisable()
        {
            if (rooms != null) rooms.Failed -= ShowError;

            if (networkMatch != null)
            {
                networkMatch.RosterChanged -= Refresh;
                networkMatch.MatchStartedByHost -= HandleMatchStartedByHost;
                networkMatch.PlayerJoined -= HandlePlayerJoined;
                networkMatch.Desynced -= ShowError;
                networkMatch.StatusChanged -= HandleStatusChanged;
            }
        }

        /// <summary>Opens the screen fresh. Called by the main menu.</summary>
        public void Show()
        {
            if (nameInput != null) nameInput.SetTextWithoutNotify(PlayerProfiles.DisplayName);

            SetStatus("Host a room, or type a friend's code.", false);
            Refresh();
        }

        // ------------------------------------------------------------------ actions

        private async Task HostAsync()
        {
            if (_busy || InRoom) return;

            _busy = true;
            SetStatus("Creating a room...", false);
            Refresh();

            UgsSessionTransport transport = await rooms.HostAsync(LocalName());

            if (transport != null)
            {
                networkMatch.Join(transport, LocalName(), rooms.LocalIdentity);
                SetStatus("Room open. Read the code out, then press START.", false);
            }

            _busy = false;
            Refresh();
        }

        private async Task JoinAsync()
        {
            if (_busy || InRoom) return;

            _busy = true;
            SetStatus("Joining...", false);
            Refresh();

            string code = codeInput != null ? codeInput.text : string.Empty;
            UgsSessionTransport transport = await rooms.JoinAsync(code, LocalName());

            if (transport != null)
            {
                networkMatch.Join(transport, LocalName(), rooms.LocalIdentity);
                SetStatus("Joined. Waiting for the host to start.", false);
            }

            _busy = false;
            Refresh();
        }

        /// <summary>
        /// Host only. Publishes the rules, then starts locally.
        /// </summary>
        /// <remarks>
        /// Order matters and is the whole reason this method exists rather than reusing the lobby's
        /// start button: the settings must be on the wire before any board is generated, or the guests
        /// build a different lattice and every move index afterwards means something else.
        /// </remarks>
        private void StartMatch()
        {
            if (networkMatch == null || !networkMatch.IsOnline || _busy) return;

            int players = Mathf.Clamp(networkMatch.PlayerCount, 2, SeatRoster.SeatCount);
            int radius = TrigglePrefs.BoardRadius;
            int pegsPerBand = flowController != null && flowController.Board != null
                ? flowController.Board.PegsPerBand
                : 4;

            networkMatch.BroadcastMatchSettings(radius, pegsPerBand, players, roundCount);
            networkMatch.ApplySeatOwnership(players);

            flowController.ConfigurePlayerCount(players);

            if (matchController != null)
            {
                matchController.ConfigureRounds(roundCount);
                matchController.StartMatch();
            }
            else
            {
                flowController.StartNewGame();
            }

            if (mainMenu != null) mainMenu.EnterGame();
        }

        private void HandleMatchStartedByHost()
        {
            if (mainMenu != null) mainMenu.EnterGame();
        }

        /// <summary>Names the player who just arrived, rather than saying "someone".</summary>
        /// <remarks>
        /// The connection and the name arrive separately - Relay reports a peer as soon as the pipe is
        /// up, and the name only when that peer's announcement lands - so the status line says the vague
        /// thing first and is corrected here. Nothing is being waited for; these are simply two different
        /// events.
        /// </remarks>
        private void HandlePlayerJoined(int seat, string name)
        {
            SetStatus($"{name} joined  -  {Occupancy()} in the room.", false);
            Refresh();
        }

        private string Occupancy()
        {
            int count = networkMatch != null ? networkMatch.PlayerCount : 0;
            return $"{count}/{UgsRoomService.RoomCapacity}";
        }

        /// <summary>
        /// Keeps the screen honest about the connection, which the roster alone cannot do.
        /// </summary>
        /// <remarks>
        /// The Relay handshake takes several frames and can fail outright, and neither shows up as a
        /// roster change - so without this the host sits looking at a room code and a dead START button
        /// with nothing on screen explaining why.
        /// </remarks>
        private void HandleStatusChanged(SessionStatus status)
        {
            switch (status)
            {
                case SessionStatus.Connecting:
                    SetStatus("Connecting to Relay...", false);
                    break;

                case SessionStatus.Connected when rooms != null && rooms.IsHost:
                    SetStatus("Someone joined. Press START when everyone is in.", false);
                    break;

                case SessionStatus.Connected:
                    SetStatus("Connected. Waiting for the host to start.", false);
                    break;

                case SessionStatus.Failed:
                    SetStatus("Lost the connection. Leave the room and try again.", true);
                    break;
            }

            Refresh();
        }

        private async Task LeaveAsync()
        {
            if (_busy) return;

            _busy = true;

            if (networkMatch != null) networkMatch.Leave();
            if (rooms != null) await rooms.LeaveAsync();

            // Back to a hot-seat lineup, or a later local game would still think seats belong to people
            // who are no longer connected and refuse input for them.
            SeatRoster.SetAllHuman();

            _busy = false;
            SetStatus("Left the room.", false);
            Refresh();
        }

        private async Task BackAsync()
        {
            await LeaveAsync();

            if (mainMenu != null) mainMenu.ShowRootMenu();
        }

        // ------------------------------------------------------------------ display

        private void Refresh()
        {
            bool inRoom = InRoom;
            bool online = networkMatch != null && networkMatch.IsOnline;
            bool isHost = rooms != null && rooms.IsHost;
            int count = networkMatch != null ? networkMatch.PlayerCount : 0;

            if (hostButton != null) hostButton.interactable = !inRoom && !_busy;
            if (joinButton != null) joinButton.interactable = !inRoom && !_busy;
            if (codeInput != null) codeInput.interactable = !inRoom && !_busy;
            if (leaveButton != null) leaveButton.gameObject.SetActive(inRoom);

            // Editable at any time: renaming mid-room re-announces, so it stays useful after joining.
            if (nameInput != null) nameInput.interactable = !_busy;

            if (roomCodeLabel != null)
                roomCodeLabel.text = inRoom ? rooms.RoomCode : "- - - - - -";

            // Only the host can start, and only with someone to play against.
            if (startButton != null)
                startButton.interactable = isHost && online && count >= 2 && !_busy;

            if (startLabel != null)
                startLabel.text = isHost ? "START GAME" : "HOST STARTS";

            if (occupancyLabel != null) occupancyLabel.text = $"IN THE ROOM  -  {Occupancy()}";

            RefreshPlayerRows(count);
        }

        private void RefreshPlayerRows(int count)
        {
            if (playerRows == null) return;

            for (int i = 0; i < playerRows.Length; i++)
            {
                if (playerRows[i] == null) continue;

                int seat = i + 1;
                bool present = networkMatch != null && seat <= SeatRoster.SeatCount && HasSeat(seat);

                if (!present)
                {
                    playerRows[i].text = $"<color=#{ColorUtility.ToHtmlStringRGB(infoColor)}>Seat {seat} - empty</color>";
                    continue;
                }

                string name = networkMatch.NameOfSeat(seat);
                Color color = palette.GetColor((PlayerId)seat);
                string you = seat == networkMatch.LocalSeat ? "  (you)" : string.Empty;

                playerRows[i].text =
                    $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{name}</color>{you}";
            }

            _ = count;
        }

        private bool HasSeat(int seat)
        {
            foreach (int occupied in networkMatch.Seats)
                if (occupied == seat) return true;

            return false;
        }

        private void SetStatus(string message, bool isError)
        {
            if (statusLabel == null) return;

            statusLabel.text = message;
            statusLabel.color = isError ? errorColor : infoColor;
        }

        private void ShowError(string message)
        {
            SetStatus(message, true);
            Refresh();
        }

        /// <summary>The name this device shows others online.</summary>
        private string LocalName()
        {
            PlayerProfiles.Load();

            string name = PlayerProfiles.DisplayName;
            return string.IsNullOrWhiteSpace(name) ? palette.GetDisplayName(PlayerId.Player1) : name;
        }

        private void OnValidate()
        {
            if (playerRows != null && playerRows.Length != SeatRoster.SeatCount)
                playerRows = new TMP_Text[SeatRoster.SeatCount];
        }

        /// <summary>Exposed so the main menu can fade the panel like every other screen.</summary>
        public CanvasGroup Panel => panel;
    }
}
