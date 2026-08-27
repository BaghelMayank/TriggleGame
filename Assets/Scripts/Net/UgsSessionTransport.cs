using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using UnityEngine;

namespace Triggle.Net
{
    /// <summary>
    /// Moves <see cref="NetMessage"/> bytes between devices over Unity Relay, using Unity Transport
    /// directly.
    /// </summary>
    /// <remarks>
    /// <b>No Netcode for GameObjects.</b> NGO exists to replicate objects and reconcile their state, and
    /// this game replicates nothing - a turn is one integer applied to a board both sides generated
    /// identically. All that is needed is a reliable ordered byte pipe, which is exactly what a
    /// <see cref="NetworkDriver"/> with a <see cref="ReliableSequencedPipelineStage"/> is. Skipping NGO
    /// also skips its NetworkManager, spawn lifecycle and scene-synchronisation rules, none of which
    /// this design has any use for.
    /// <para>
    /// <b>Reliable and ordered is not optional.</b> A dropped or reordered move silently diverges the
    /// two boards, so every message goes down the reliable pipeline. Relay itself is only a forwarding
    /// service - it makes no delivery guarantees of its own - which is why the pipeline stage, not the
    /// service, is what provides them.
    /// </para>
    /// <para>
    /// The host is the only peer with a connection to every other, because Relay is a star topology:
    /// guests reach each other by way of the host's allocation. With up to four players and one packet
    /// per turn, forwarding through the host costs nothing worth optimising.
    /// </para>
    /// </remarks>
    public sealed class UgsSessionTransport : ISessionTransport
    {
        /// <summary>
        /// Largest message this transport will send or accept, in bytes. Checked against the largest
        /// message the protocol can produce by the multiplayer spine verification.
        /// </summary>
        public const int MaxPacketSize = 512;

        /// <summary>How long to wait for the Relay bind handshake before giving up.</summary>
        private const float BindTimeoutSeconds = 15f;

        private NetworkDriver _driver;
        private NetworkPipeline _pipeline;
        private NetworkEndPoint _serverEndpoint;

        /// <summary>False until the Relay handshake finishes and the driver can listen or connect.</summary>
        private bool _ready;
        private float _bindDeadline;
        private readonly bool _verbose;

        private readonly List<NetworkConnection> _connections = new List<NetworkConnection>(4);
        private readonly Queue<NetMessage> _outbox = new Queue<NetMessage>(8);

        private bool _disposed;

        public SessionStatus State { get; private set; } = SessionStatus.Connecting;
        public bool IsHost { get; }
        public int LocalSeat { get; private set; }

        public event Action<NetMessage> MessageReceived;
        public event Action<SessionStatus> StateChanged;

        /// <summary>The code another player types to join this room. Host only.</summary>
        public string JoinCode { get; }

        /// <summary>
        /// Wraps an already-allocated Relay endpoint.
        /// </summary>
        /// <remarks>
        /// Allocation is async and this is not, so <see cref="UgsRoomService"/> does that part and hands
        /// over the result. Keeping the driver's lifetime synchronous means <see cref="Poll"/> and
        /// <see cref="Dispose"/> have no awaits to race against.
        /// </remarks>
        public UgsSessionTransport(RelayServerData relayData, bool isHost, int localSeat, string joinCode,
                                   bool verboseLogging = false)
        {
            IsHost = isHost;
            LocalSeat = localSeat;
            JoinCode = joinCode;
            _verbose = verboseLogging;

            var settings = new NetworkSettings();
            settings.WithRelayParameters(ref relayData);

            _driver = NetworkDriver.Create(settings);
            _pipeline = _driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
            _serverEndpoint = relayData.Endpoint;

            // NetworkEndPoint, capital P: Unity Transport 1.3 spells it this way and only renamed it to
            // NetworkEndpoint in 2.x.
            if (_driver.Bind(NetworkEndPoint.AnyIpv4) != 0)
            {
                Fail("Could not bind to the Relay allocation.");
                return;
            }

            // Listening and connecting both wait for Bound, in Poll. Over Relay, Bind only *starts* a
            // handshake with the relay server - the driver is not usable until that completes, which
            // takes several frames. Calling Listen on the next line silently fails, and the symptom is
            // a room that forms perfectly over Lobby and then never carries a single byte.
            _bindDeadline = Time.realtimeSinceStartup + BindTimeoutSeconds;
        }

        // ------------------------------------------------------------------ sending

        public void Send(NetMessage message)
        {
            if (_disposed) return;

            // Queued rather than sent on the spot. A guest that has not finished connecting would
            // otherwise silently drop its own first messages, and for a turn-based game holding them for
            // a frame costs nothing.
            _outbox.Enqueue(message);

            if (State == SessionStatus.Connected) Flush();
        }

        private void Flush()
        {
            while (_outbox.Count > 0)
            {
                NetMessage message = _outbox.Peek();
                byte[] bytes = message.Serialize();

                if (bytes.Length > MaxPacketSize)
                {
                    Debug.LogError($"[Triggle] Refusing to send a {bytes.Length} byte message; the " +
                                   $"limit is {MaxPacketSize}. Dropping {message.Kind}.");

                    _outbox.Dequeue();
                    continue;
                }

                if (!SendToAll(bytes)) return;   // no room in the send queue; try again next Poll

                _outbox.Dequeue();
            }
        }

        private bool SendToAll(byte[] bytes)
        {
            bool allSent = true;

            for (int i = 0; i < _connections.Count; i++)
            {
                if (!_connections[i].IsCreated) continue;

                if (_driver.BeginSend(_pipeline, _connections[i], out DataStreamWriter writer) != 0)
                {
                    allSent = false;
                    continue;
                }

                var payload = new NativeArray<byte>(bytes.Length, Allocator.Temp);
                try
                {
                    payload.CopyFrom(bytes);
                    writer.WriteBytes(payload);
                }
                finally
                {
                    payload.Dispose();
                }

                _driver.EndSend(writer);
            }

            return allSent;
        }

        // ------------------------------------------------------------------ receiving

        public void Poll()
        {
            if (_disposed || !_driver.IsCreated) return;

            _driver.ScheduleUpdate().Complete();

            if (!_ready && !TryFinishBinding()) return;

            if (IsHost) AcceptNewConnections();

            NetworkEvent.Type type;
            while ((type = _driver.PopEvent(out NetworkConnection connection, out DataStreamReader reader))
                   != NetworkEvent.Type.Empty)
            {
                switch (type)
                {
                    case NetworkEvent.Type.Connect:
                        SetState(SessionStatus.Connected);
                        break;

                    case NetworkEvent.Type.Data:
                        Receive(connection, reader);
                        break;

                    case NetworkEvent.Type.Disconnect:
                        Drop(connection);
                        break;
                }
            }

            if (State == SessionStatus.Connected) Flush();
        }

        /// <summary>
        /// Starts listening or connecting, once the Relay handshake has completed.
        /// </summary>
        /// <returns>True when the driver is ready to carry traffic.</returns>
        private bool TryFinishBinding()
        {
            if (State == SessionStatus.Failed) return false;

            if (!_driver.Bound)
            {
                if (Time.realtimeSinceStartup < _bindDeadline) return false;

                Fail("Relay did not finish binding. Check that Relay is enabled for this project on " +
                     "the Unity Cloud dashboard, and that the device has a working connection.");

                return false;
            }

            if (IsHost)
            {
                if (_driver.Listen() != 0)
                {
                    Fail("Relay host could not start listening.");
                    return false;
                }
            }
            else
            {
                // A guest's single connection is to the host's allocation, which the relay parameters in
                // the driver settings already point at.
                _connections.Add(_driver.Connect(_serverEndpoint));
            }

            _ready = true;
            Log(IsHost ? "relay bound, host is listening" : "relay bound, connecting to host");
            return true;
        }

        private void AcceptNewConnections()
        {
            NetworkConnection incoming;
            while ((incoming = _driver.Accept()) != default)
            {
                _connections.Add(incoming);
                SetState(SessionStatus.Connected);
            }
        }

        private void Receive(NetworkConnection from, DataStreamReader reader)
        {
            int length = reader.Length;
            if (length <= 0 || length > MaxPacketSize) return;

            var buffer = new NativeArray<byte>(length, Allocator.Temp);
            byte[] managed;

            try
            {
                reader.ReadBytes(buffer);
                managed = buffer.ToArray();
            }
            finally
            {
                buffer.Dispose();
            }

            // Untrusted input: a peer on a different build, or a corrupt packet, must not take the game
            // down. TryDeserialize refuses anything malformed rather than throwing.
            if (!NetMessage.TryDeserialize(managed, out NetMessage message)) return;

            // Relay is a star, so a guest's message reaches the other guests only if the host passes it
            // on. Forwarded before it is handled locally, so a slow handler cannot delay the relay.
            if (IsHost && _connections.Count > 1) Forward(managed, from);

            MessageReceived?.Invoke(message);
        }

        private void Forward(byte[] bytes, NetworkConnection origin)
        {
            for (int i = 0; i < _connections.Count; i++)
            {
                if (!_connections[i].IsCreated || _connections[i] == origin) continue;

                if (_driver.BeginSend(_pipeline, _connections[i], out DataStreamWriter writer) != 0)
                    continue;

                var payload = new NativeArray<byte>(bytes.Length, Allocator.Temp);
                try
                {
                    payload.CopyFrom(bytes);
                    writer.WriteBytes(payload);
                }
                finally
                {
                    payload.Dispose();
                }

                _driver.EndSend(writer);
            }
        }

        private void Drop(NetworkConnection connection)
        {
            for (int i = _connections.Count - 1; i >= 0; i--)
                if (_connections[i] == connection) _connections.RemoveAt(i);

            if (_connections.Count == 0) SetState(SessionStatus.Offline);
        }

        // ------------------------------------------------------------------ lifetime

        /// <summary>Assigns this device's seat once the room has decided the running order.</summary>
        public void AssignSeat(int seat) => LocalSeat = seat;

        private void Fail(string reason)
        {
            Debug.LogError($"[Triggle] Relay transport failed: {reason}");
            SetState(SessionStatus.Failed);
        }

        private void Log(string message)
        {
            if (_verbose) Debug.Log($"[Triggle] Relay: {message}");
        }

        private void SetState(SessionStatus status)
        {
            if (State == status) return;

            State = status;
            StateChanged?.Invoke(status);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_driver.IsCreated)
            {
                for (int i = 0; i < _connections.Count; i++)
                    if (_connections[i].IsCreated) _driver.Disconnect(_connections[i]);

                _driver.ScheduleUpdate().Complete();
                _driver.Dispose();
            }

            _connections.Clear();
            _outbox.Clear();

            SetState(SessionStatus.Offline);

            MessageReceived = null;
            StateChanged = null;
        }
    }
}
