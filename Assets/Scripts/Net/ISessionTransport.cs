using System;

namespace Triggle.Net
{
    /// <summary>How a session is doing.</summary>
    public enum SessionStatus
    {
        Offline = 0,
        Connecting = 1,
        Connected = 2,
        Failed = 3
    }

    /// <summary>
    /// Everything the game needs from a network stack: ship these bytes to the other players, and tell
    /// me when bytes arrive.
    /// </summary>
    /// <remarks>
    /// The seam exists so the choice of stack stays cheap to change. Unity Gaming Services, Photon and a
    /// self-hosted Mirror server all differ in how a room is created and how bytes move, and nothing
    /// else in the game should have to know which one is in use - the rules engine, the HUD and the chat
    /// panel all sit above this line and see only <see cref="NetMessage"/>.
    /// <para>
    /// Deliberately not an interface over Unity's <c>NetworkBehaviour</c>: this game does not replicate
    /// objects. A turn is one integer, applied to a board both sides generated identically, so what is
    /// needed is an ordered message pipe and nothing more.
    /// </para>
    /// <para>
    /// <b>Ordering is required, delivery is required.</b> A dropped or reordered
    /// <see cref="NetMessageKind.PlaceBand"/> silently diverges the two boards, which is unrecoverable
    /// and invisible. Implementations must use a reliable ordered channel; the move number in the
    /// message is a check on that, not a substitute for it.
    /// </para>
    /// </remarks>
    public interface ISessionTransport : IDisposable
    {
        /// <summary>Current connection state.</summary>
        SessionStatus State { get; }

        /// <summary>True when this peer is the authority for the match: it decides the rules and rounds.</summary>
        bool IsHost { get; }

        /// <summary>This peer's seat, 1-4. Zero until the session assigns one.</summary>
        int LocalSeat { get; }

        /// <summary>Raised on the main thread for every message from another peer.</summary>
        event Action<NetMessage> MessageReceived;

        /// <summary>Raised when <see cref="State"/> changes.</summary>
        event Action<SessionStatus> StateChanged;

        /// <summary>Sends to every other peer over a reliable ordered channel.</summary>
        void Send(NetMessage message);

        /// <summary>
        /// Pumps queued traffic. Called once per frame by <c>NetworkMatch</c>, so implementations can
        /// deliver on the main thread rather than from a socket thread - the rules engine and the whole
        /// UI layer are not thread-safe.
        /// </summary>
        void Poll();
    }
}
