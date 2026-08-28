using System;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.Gameplay;
using UnityEngine;

namespace Triggle.Net
{
    /// <summary>
    /// Binds a <see cref="ISessionTransport"/> to the rules engine: broadcasts the local player's moves
    /// and applies everyone else's.
    /// </summary>
    /// <remarks>
    /// <b>Nothing about the board travels over the wire.</b> Both devices generate the same lattice from
    /// the same radius and band length, and a turn is the index of a band in that shared catalogue - so
    /// this class relays integers and lets each side's own <see cref="GameFlowController"/> work out
    /// what they mean. No state replication, no authority server, no reconciliation: the rules engine is
    /// already deterministic, so running it twice with the same input is the synchronisation.
    /// <para>
    /// The cost of that design is that a lost or reordered move is unrecoverable - the boards diverge
    /// and neither side can tell. <see cref="ISessionTransport"/> therefore requires a reliable ordered
    /// channel, and the move number in each packet is checked here as a tripwire on that promise rather
    /// than as a way to repair it.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NetworkMatch : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private GameFlowController flowController;
        [SerializeField] private MatchController matchController;

        [Tooltip("Log every packet sent and received.")]
        [SerializeField] private bool verboseLogging;

        private ISessionTransport _transport;
        private int _movesApplied;
        private bool _desynced;
        private bool _announced;
        private string _localName = "Player";
        private int _localIdentity;

        /// <summary>Seat each known peer holds, keyed by its stable identity.</summary>
        private readonly Dictionary<int, int> _seatByIdentity = new Dictionary<int, int>(4);

        /// <summary>
        /// The seat this device plays. Zero when there is no session.
        /// </summary>
        /// <remarks>
        /// Held here rather than read from the transport, because the host can move a guest to a
        /// different seat after it connects and the transport has no say in that.
        /// </remarks>
        public int LocalSeat { get; private set; }

        /// <summary>True while a network session is running.</summary>
        public bool IsOnline => _transport != null && _transport.State == SessionStatus.Connected;

        /// <summary>True once the boards are known to have diverged. The match cannot continue.</summary>
        public bool IsDesynced => _desynced;

        /// <summary>Raised for chat traffic, so the chat panel needs no reference to the transport.</summary>
        public event Action<int, int, string> ChatReceived;

        /// <summary>Raised when the boards diverge, with a player-facing explanation.</summary>
        public event Action<string> Desynced;

        /// <summary>Raised whenever a player joins or leaves the room.</summary>
        public event Action RosterChanged;

        /// <summary>Raised when a player is first seen, with the seat they took and the name they gave.</summary>
        public event Action<int, string> PlayerJoined;

        /// <summary>
        /// Raised on a guest when the host starts the match, after settings have been applied. The
        /// front end uses this to leave the menu, since the guest never pressed anything.
        /// </summary>
        public event Action MatchStartedByHost;

        /// <summary>Who is in the room, keyed by seat. Includes this device.</summary>
        private readonly SortedDictionary<int, string> _roster = new SortedDictionary<int, string>();

        /// <summary>Seats currently in the room, lowest first.</summary>
        public IEnumerable<int> Seats => _roster.Keys;

        /// <summary>How many players are in the room, including this device.</summary>
        public int PlayerCount => _roster.Count;

        /// <summary>The name a seat announced, or a fallback.</summary>
        public string NameOfSeat(int seat) =>
            _roster.TryGetValue(seat, out string name) && !string.IsNullOrEmpty(name)
                ? name
                : $"Player {seat}";

        private void Awake()
        {
            if (flowController == null) flowController = FindObjectOfType<GameFlowController>();
            if (matchController == null) matchController = FindObjectOfType<MatchController>();
        }

        private void OnEnable()
        {
            GameEvents.OnBandPlaced += HandleLocalBandPlaced;
            GameEvents.OnGameReset += HandleGameReset;
        }

        private void OnDisable()
        {
            GameEvents.OnBandPlaced -= HandleLocalBandPlaced;
            GameEvents.OnGameReset -= HandleGameReset;
        }

        private void OnDestroy() => Leave();

        private void Update() => _transport?.Poll();

        // ------------------------------------------------------------------ session

        /// <summary>Takes ownership of a connected transport and starts relaying.</summary>
        /// <param name="localName">Name this device announces to the others.</param>
        /// <param name="localIdentity">
        /// Stable id for this device, independent of seat - the hashed Lobby player id in a real session.
        /// Seats are the host's to assign, so identity is what a peer is actually recognised by.
        /// </param>
        public void Join(ISessionTransport transport, string localName, int localIdentity)
        {
            Leave();

            _transport = transport;
            if (_transport == null) return;

            _transport.MessageReceived += HandleMessage;
            _transport.StateChanged += HandleStateChanged;

            _movesApplied = 0;
            _desynced = false;
            _announced = false;
            _localName = localName;
            _localIdentity = localIdentity;
            LocalSeat = _transport.LocalSeat;

            _roster.Clear();
            _seatByIdentity.Clear();

            _roster[LocalSeat] = localName;
            _seatByIdentity[localIdentity] = LocalSeat;
            RosterChanged?.Invoke();

            // Queued immediately rather than waiting for the transport to report Connected. Send holds
            // it until the pipe is up, so this costs nothing and removes a round of event ordering from
            // the moment a player becomes visible to everyone else.
            Announce();
        }

        /// <summary>Raised when the transport's connection state changes.</summary>
        public event Action<SessionStatus> StatusChanged;

        /// <summary>Connection state of the current session, or Offline when there is none.</summary>
        public SessionStatus Status => _transport?.State ?? SessionStatus.Offline;

        private void HandleStateChanged(SessionStatus status)
        {
            if (status == SessionStatus.Connected) Announce();

            Log($"transport is now {status}");
            StatusChanged?.Invoke(status);
        }

        /// <summary>
        /// Tells the other players who is at this seat.
        /// </summary>
        /// <remarks>
        /// Sent once per session rather than on every connect event, because the host raises Connected
        /// again for each guest that arrives and re-announcing would spam the room.
        /// </remarks>
        private void Announce()
        {
            if (_announced || _transport == null) return;

            _announced = true;
            _transport.Send(BuildHello());
        }

        /// <summary>Changes the name this device shows others, and tells them about it.</summary>
        public void RenameLocalPlayer(string localName)
        {
            if (string.IsNullOrWhiteSpace(localName) || localName == _localName) return;

            _localName = localName;

            if (LocalSeat > 0) _roster[LocalSeat] = localName;
            RosterChanged?.Invoke();

            _transport?.Send(BuildHello());
        }

        private NetMessage BuildHello() =>
            NetMessage.Hello(LocalSeat, _localName,
                             PlayerProfiles.GetColorIndex((PlayerId)Mathf.Clamp(LocalSeat, 1, SeatRoster.SeatCount)),
                             _localIdentity);

        /// <summary>Ends the session and disposes the transport.</summary>
        public void Leave()
        {
            if (_transport == null) return;

            _transport.MessageReceived -= HandleMessage;
            _transport.StateChanged -= HandleStateChanged;
            _transport.Dispose();
            _transport = null;

            _roster.Clear();
            _seatByIdentity.Clear();
            RosterChanged?.Invoke();
        }

        /// <summary>
        /// Host only: tells everyone the rules this match runs under, before any board is built.
        /// </summary>
        /// <remarks>
        /// This is what makes the whole design work. Radius and band length decide the band catalogue,
        /// and the catalogue is what move indices are indices <i>into</i> - so every device must apply
        /// these settings before generating its lattice. Send this, then start the match; doing it the
        /// other way round builds two different boards and every move after that is nonsense.
        /// </remarks>
        public void BroadcastMatchSettings(int radius, int pegsPerBand, int playerCount, int rounds)
        {
            if (!IsOnline || !_transport.IsHost) return;

            _transport.Send(NetMessage.StartMatch(radius, pegsPerBand, playerCount, rounds));
            Log($"sent match settings: radius {radius}, {playerCount} players, {rounds} rounds");
        }

        /// <summary>Host only: tells everyone to begin the next round of the series.</summary>
        public void BroadcastNextRound(int roundNumber)
        {
            if (!IsOnline || !_transport.IsHost) return;

            _transport.Send(NetMessage.NextRound(roundNumber));
        }

        /// <summary>Sends a quick-chat phrase or emote to the other players.</summary>
        public void SendChat(int phraseId, string text)
        {
            if (!IsOnline) return;

            _transport.Send(NetMessage.Chat(_transport.LocalSeat, phraseId, text));
        }

        // ------------------------------------------------------------------ outgoing

        /// <summary>
        /// Broadcasts a move the local player just made.
        /// </summary>
        /// <remarks>
        /// Driven by <see cref="GameEvents.OnBandPlaced"/> rather than by the input layer, so <i>every</i>
        /// path that can place a band is covered - mouse, touch, and the AI when a computer seat is
        /// sharing the device. Hooking the input layer instead would have silently dropped AI moves.
        /// <para>
        /// Seats played by someone else are skipped: their move is already being applied <i>because</i>
        /// a packet arrived, and echoing it back would loop.
        /// </para>
        /// </remarks>
        private void HandleLocalBandPlaced(PlayerId player, BandPlacement band)
        {
            if (!IsOnline || _desynced) return;

            int seat = (int)player;
            bool ownedLocally = seat == _transport.LocalSeat || SeatRoster.IsComputer(player);

            _movesApplied++;
            if (!ownedLocally) return;

            _transport.Send(NetMessage.PlaceBand(seat, band.Id, _movesApplied - 1));
            Log($"sent move {_movesApplied - 1}: seat {seat} band #{band.Id}");
        }

        private void HandleGameReset() => _movesApplied = 0;

        // ------------------------------------------------------------------ incoming

        private void HandleMessage(NetMessage message)
        {
            if (_desynced) return;

            Log($"recv {message}");

            switch (message.Kind)
            {
                case NetMessageKind.PlaceBand:
                    ApplyRemoteMove(message);
                    break;

                case NetMessageKind.Chat:
                    ChatReceived?.Invoke(message.Seat, message.A, message.Text);
                    break;

                case NetMessageKind.NextRound:
                    if (matchController != null) matchController.ContinueToNextRound();
                    break;

                case NetMessageKind.Hello:
                    ApplyHello(message);
                    break;

                case NetMessageKind.AssignSeat:
                    ApplyAssignedSeat(message);
                    break;

                case NetMessageKind.StartMatch:
                    ApplyMatchSettings(message);
                    break;

                case NetMessageKind.Resign:
                    Log($"seat {message.Seat} resigned");
                    break;
            }
        }

        private void ApplyRemoteMove(NetMessage message)
        {
            if (flowController == null) return;

            // The tripwire. Both sides count moves the same way, so a mismatch means a packet was lost,
            // duplicated or overtaken - at which point the boards have already diverged and continuing
            // would just produce two different games that each look fine.
            if (message.B != _movesApplied)
            {
                Desync($"Move {message.B} arrived when move {_movesApplied} was expected.");
                return;
            }

            if (flowController.SubmitBandById(message.A)) return;

            Desync($"Seat {message.Seat} played band #{message.A}, which is not legal on this board.");
        }

        /// <summary>
        /// Records another player's arrival, and answers so they learn about this device too.
        /// </summary>
        /// <remarks>
        /// The reply is the only way a guest finds out who else is already in the room: Relay has no
        /// roster, and each guest announces itself only once. Guarded on the seat being new, or two
        /// peers would answer each other forever.
        /// </remarks>
        private void ApplyHello(NetMessage message)
        {
            if (message.A != NetMessage.ProtocolVersion)
            {
                Desync($"Seat {message.Seat} is running a different version of the game " +
                       $"(protocol {message.A}, this build speaks {NetMessage.ProtocolVersion}).");
                return;
            }

            if (message.C == _localIdentity) return;   // our own announcement, forwarded back to us

            // Already known: refresh the name, but do not re-seat or re-reply, or two peers would keep
            // answering each other forever.
            if (_seatByIdentity.TryGetValue(message.C, out int known))
            {
                _roster[known] = message.Text;
                RosterChanged?.Invoke();
                return;
            }

            int seat = message.Seat;

            // The host owns seat allocation. Guests derive a seat from their own lobby snapshot, and two
            // devices joining at nearly the same instant can both read the same one - in which case the
            // second used to be dropped without a word, leaving a player connected, in the lobby, and
            // invisible to everyone. The host moves them instead.
            if (_transport != null && _transport.IsHost)
            {
                if (seat <= 0 || seat > SeatRoster.SeatCount || _roster.ContainsKey(seat))
                {
                    int free = LowestFreeSeat();
                    if (free == 0)
                    {
                        Log($"room is full; no seat for \"{message.Text}\"");
                        return;
                    }

                    if (seat != free)
                        Log($"seat {seat} was taken, moving \"{message.Text}\" to seat {free}");

                    seat = free;
                }

                _transport.Send(NetMessage.AssignSeat(message.C, seat));
            }
            else if (seat <= 0 || seat > SeatRoster.SeatCount || _roster.ContainsKey(seat))
            {
                // A guest cannot resolve a clash; the host's AssignSeat settles it a moment later.
                return;
            }

            _roster[seat] = message.Text;
            _seatByIdentity[message.C] = seat;

            RosterChanged?.Invoke();
            PlayerJoined?.Invoke(seat, message.Text);

            Log($"seat {seat} joined as \"{message.Text}\" ({_roster.Count} in room)");

            // Answer so the new arrival learns about this device - the only way a guest finds out who
            // was already here, since Relay keeps no roster of its own.
            _transport?.Send(BuildHello());
        }

        /// <summary>Adopts a seat the host handed out, if it is addressed to this device.</summary>
        private void ApplyAssignedSeat(NetMessage message)
        {
            if (message.C != _localIdentity || message.Seat == LocalSeat) return;

            Log($"host moved us from seat {LocalSeat} to seat {message.Seat}");

            _roster.Remove(LocalSeat);
            LocalSeat = message.Seat;

            _roster[LocalSeat] = _localName;
            _seatByIdentity[_localIdentity] = LocalSeat;

            RosterChanged?.Invoke();

            // Re-announce under the corrected seat so everyone records us in the right place.
            _transport?.Send(BuildHello());
        }

        private int LowestFreeSeat()
        {
            for (int seat = 1; seat <= SeatRoster.SeatCount; seat++)
                if (!_roster.ContainsKey(seat)) return seat;

            return 0;
        }

        private void ApplyMatchSettings(NetMessage message)
        {
            // The host owns the rules. Applying them before the board is built is what makes both
            // devices generate the same band catalogue in the first place.
            TrigglePrefs.BoardRadius = message.A;

            if (flowController != null) flowController.ConfigurePlayerCount(message.C);
            if (matchController != null) matchController.ConfigureRounds(message.D);

            Log($"match settings: radius {message.A}, {message.C} players, {message.D} rounds");

            // The guest never pressed anything, so this is what actually begins its match. Seats are
            // assigned first: whichever seat is this device's is the only one that takes input.
            ApplySeatOwnership(message.C);

            if (matchController != null) matchController.StartMatch();
            else if (flowController != null) flowController.StartNewGame();

            MatchStartedByHost?.Invoke();
        }

        /// <summary>
        /// Marks this device's seat as the only one that accepts input; the rest belong to other people.
        /// </summary>
        public void ApplySeatOwnership(int playerCount)
        {
            int local = LocalSeat;

            for (int seat = 1; seat <= SeatRoster.SeatCount; seat++)
            {
                var player = (PlayerId)seat;

                if (seat > playerCount) { SeatRoster.SetKind(player, SeatKind.Human); continue; }

                SeatRoster.SetKind(player, seat == local ? SeatKind.Human : SeatKind.Remote);
            }
        }

        private void Desync(string reason)
        {
            _desynced = true;

            Debug.LogError($"[Triggle] Network desync: {reason} The boards are no longer identical, " +
                           "so the match cannot continue.", this);

            Desynced?.Invoke(reason);
        }

        private void Log(string message)
        {
            if (verboseLogging) Debug.Log($"[Triggle] Net: {message}", this);
        }
    }
}
