using System;
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

        /// <summary>The seat this device plays. Zero when there is no session.</summary>
        public int LocalSeat => _transport?.LocalSeat ?? 0;

        /// <summary>True while a network session is running.</summary>
        public bool IsOnline => _transport != null && _transport.State == SessionStatus.Connected;

        /// <summary>True once the boards are known to have diverged. The match cannot continue.</summary>
        public bool IsDesynced => _desynced;

        /// <summary>Raised for chat traffic, so the chat panel needs no reference to the transport.</summary>
        public event Action<int, int, string> ChatReceived;

        /// <summary>Raised when the boards diverge, with a player-facing explanation.</summary>
        public event Action<string> Desynced;

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
        public void Join(ISessionTransport transport)
        {
            Leave();

            _transport = transport;
            if (_transport == null) return;

            _transport.MessageReceived += HandleMessage;
            _movesApplied = 0;
            _desynced = false;
        }

        /// <summary>Ends the session and disposes the transport.</summary>
        public void Leave()
        {
            if (_transport == null) return;

            _transport.MessageReceived -= HandleMessage;
            _transport.Dispose();
            _transport = null;
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

        private void ApplyMatchSettings(NetMessage message)
        {
            // The host owns the rules. Applying them before the board is built is what makes both
            // devices generate the same band catalogue in the first place.
            TrigglePrefs.BoardRadius = message.A;

            if (flowController != null) flowController.ConfigurePlayerCount(message.C);
            if (matchController != null) matchController.ConfigureRounds(message.D);

            Log($"match settings: radius {message.A}, {message.C} players, {message.D} rounds");
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
