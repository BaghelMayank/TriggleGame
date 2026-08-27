using System;
using System.Collections.Generic;

namespace Triggle.Net
{
    /// <summary>
    /// An in-process transport that wires peers directly to each other, with no sockets and no service
    /// account.
    /// </summary>
    /// <remarks>
    /// Two jobs. It lets the whole multiplayer path - protocol, move relay, chat, round handover - be
    /// exercised headlessly and deterministically, with no Relay allocation, no dashboard setup and no
    /// network flakiness in the way of a failing test. And it is the reference implementation of
    /// <see cref="ISessionTransport"/>: anything the real transport does that this does not is a place
    /// the two can disagree.
    /// <para>
    /// Messages are queued rather than delivered on the spot, and only released by <see cref="Poll"/>.
    /// Delivering synchronously inside <see cref="Send"/> would let a handler send a reply that arrived
    /// before the original had finished being processed - re-entrancy that a real socket never produces,
    /// and which would make this a poor stand-in for one.
    /// </para>
    /// </remarks>
    public sealed class LoopbackTransport : ISessionTransport
    {
        private readonly List<LoopbackTransport> _peers = new List<LoopbackTransport>(4);
        private readonly Queue<NetMessage> _inbox = new Queue<NetMessage>(16);

        private bool _disposed;

        public SessionStatus State { get; private set; } = SessionStatus.Offline;
        public bool IsHost { get; private set; }
        public int LocalSeat { get; private set; }

        public event Action<NetMessage> MessageReceived;
        public event Action<SessionStatus> StateChanged;

        /// <summary>Every message this peer has sent, in order. Test hook.</summary>
        public IReadOnlyList<NetMessage> Sent => _sent;
        private readonly List<NetMessage> _sent = new List<NetMessage>(64);

        public LoopbackTransport(int localSeat, bool isHost)
        {
            LocalSeat = localSeat;
            IsHost = isHost;
        }

        /// <summary>
        /// Joins a set of peers into one session. Every peer ends up connected to every other, and none
        /// to itself.
        /// </summary>
        public static void Connect(params LoopbackTransport[] peers)
        {
            if (peers == null) return;

            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i] == null) continue;

                peers[i]._peers.Clear();

                for (int j = 0; j < peers.Length; j++)
                {
                    if (i == j || peers[j] == null) continue;

                    peers[i]._peers.Add(peers[j]);
                }

                peers[i].SetState(SessionStatus.Connected);
            }
        }

        public void Send(NetMessage message)
        {
            if (_disposed || State != SessionStatus.Connected) return;

            _sent.Add(message);

            // Round-trips through the wire format even though the peer is in the same process. A field
            // that fails to serialise would otherwise only show up against the real transport, which is
            // exactly where it is hardest to debug.
            byte[] bytes = message.Serialize();
            if (!NetMessage.TryDeserialize(bytes, out NetMessage decoded)) return;

            for (int i = 0; i < _peers.Count; i++) _peers[i]._inbox.Enqueue(decoded);
        }

        public void Poll()
        {
            if (_disposed) return;

            // Snapshot the count first: a handler is allowed to send, and on a loopback that could
            // otherwise feed this same loop forever.
            int pending = _inbox.Count;

            for (int i = 0; i < pending; i++)
                MessageReceived?.Invoke(_inbox.Dequeue());
        }

        private void SetState(SessionStatus state)
        {
            if (State == state) return;

            State = state;
            StateChanged?.Invoke(state);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _peers.Clear();
            _inbox.Clear();

            SetState(SessionStatus.Offline);

            MessageReceived = null;
            StateChanged = null;
        }
    }
}
