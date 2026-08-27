using System;
using System.IO;
using System.Text;

namespace Triggle.Net
{
    /// <summary>What a packet means. One byte on the wire.</summary>
    public enum NetMessageKind : byte
    {
        None = 0,

        /// <summary>Sent on connect: who I am and what protocol I speak.</summary>
        Hello = 1,

        /// <summary>Host to everyone: the exact rules this match runs under.</summary>
        StartMatch = 2,

        /// <summary>A player took their turn. The payload is a band index and the move number.</summary>
        PlaceBand = 3,

        /// <summary>Quick-chat phrase or emote.</summary>
        Chat = 4,

        /// <summary>Host to everyone: begin the next round of the series.</summary>
        NextRound = 5,

        /// <summary>A player left the match deliberately.</summary>
        Resign = 6
    }

    /// <summary>
    /// One packet. Deliberately a flat struct with a handful of ints rather than a class hierarchy:
    /// every message this game needs fits in a few numbers.
    /// </summary>
    /// <remarks>
    /// <b>A turn is a single integer.</b> <see cref="Triggle.Grid.BoardManager"/> enumerates every legal
    /// band once at build time, so a move is an index into that catalogue rather than a set of peg
    /// coordinates - and the catalogue is a pure function of radius and band length, so index 47 is the
    /// same band on every device. That is what keeps this protocol small enough to read in a log.
    /// <para>
    /// Serialised by hand rather than through a networking library's own writer, so the transport can be
    /// swapped without touching the protocol. See <see cref="ISessionTransport"/>.
    /// </para>
    /// </remarks>
    [Serializable]
    public struct NetMessage
    {
        /// <summary>Bumped whenever the wire format changes, so mismatched builds refuse each other.</summary>
        public const int ProtocolVersion = 1;

        public NetMessageKind Kind;

        /// <summary>Seat the message is about, 1-4. Zero when it is not seat-specific.</summary>
        public int Seat;

        /// <summary>General-purpose payload; meaning depends on <see cref="Kind"/>.</summary>
        public int A, B, C, D;

        /// <summary>Only <see cref="NetMessageKind.Hello"/> and <see cref="NetMessageKind.Chat"/> use this.</summary>
        public string Text;

        // ------------------------------------------------------------------ constructors

        public static NetMessage Hello(int seat, string playerName, int colorSlot) => new NetMessage
        {
            Kind = NetMessageKind.Hello,
            Seat = seat,
            A = ProtocolVersion,
            B = colorSlot,
            Text = playerName
        };

        public static NetMessage StartMatch(int radius, int pegsPerBand, int playerCount, int rounds) =>
            new NetMessage
            {
                Kind = NetMessageKind.StartMatch,
                A = radius,
                B = pegsPerBand,
                C = playerCount,
                D = rounds
            };

        /// <param name="moveNumber">
        /// Which move this is, counting from zero. Lets the receiver drop a duplicate and detect a gap
        /// rather than silently applying moves out of order - the one failure this protocol cannot
        /// recover from, because the board would diverge without anyone noticing.
        /// </param>
        public static NetMessage PlaceBand(int seat, int bandId, int moveNumber) => new NetMessage
        {
            Kind = NetMessageKind.PlaceBand,
            Seat = seat,
            A = bandId,
            B = moveNumber
        };

        public static NetMessage Chat(int seat, int phraseId, string text) => new NetMessage
        {
            Kind = NetMessageKind.Chat,
            Seat = seat,
            A = phraseId,
            Text = text
        };

        public static NetMessage NextRound(int roundNumber) => new NetMessage
        {
            Kind = NetMessageKind.NextRound,
            A = roundNumber
        };

        public static NetMessage Resign(int seat) => new NetMessage
        {
            Kind = NetMessageKind.Resign,
            Seat = seat
        };

        // ------------------------------------------------------------------ wire format

        /// <summary>Longest chat string accepted, in characters. Anything longer is truncated.</summary>
        public const int MaxTextLength = 120;

        /// <summary>Packs to bytes. Never throws - a message that cannot be written returns empty.</summary>
        public byte[] Serialize()
        {
            using var stream = new MemoryStream(32);
            using var writer = new BinaryWriter(stream, Encoding.UTF8);

            writer.Write((byte)Kind);
            writer.Write((byte)Mathf_Clamp(Seat, 0, 255));
            writer.Write(A);
            writer.Write(B);
            writer.Write(C);
            writer.Write(D);
            writer.Write(Truncate(Text));

            return stream.ToArray();
        }

        /// <summary>
        /// Unpacks bytes. Returns false on anything malformed rather than throwing.
        /// </summary>
        /// <remarks>
        /// Bytes arriving over a network are untrusted input, including from a peer running a different
        /// build. A short or corrupt packet must be dropped, not allowed to take the game down.
        /// </remarks>
        public static bool TryDeserialize(byte[] data, out NetMessage message)
        {
            message = default;
            if (data == null || data.Length < 18) return false;

            try
            {
                using var stream = new MemoryStream(data, false);
                using var reader = new BinaryReader(stream, Encoding.UTF8);

                message.Kind = (NetMessageKind)reader.ReadByte();
                message.Seat = reader.ReadByte();
                message.A = reader.ReadInt32();
                message.B = reader.ReadInt32();
                message.C = reader.ReadInt32();
                message.D = reader.ReadInt32();
                message.Text = reader.ReadString();

                if (message.Kind == NetMessageKind.None) return false;
                if (message.Text != null && message.Text.Length > MaxTextLength) return false;

                return true;
            }
            catch (Exception)
            {
                // EndOfStream, bad UTF-8, absurd string length - all mean the same thing here.
                return false;
            }
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            return value.Length <= MaxTextLength ? value : value.Substring(0, MaxTextLength);
        }

        /// <summary>Local clamp so this file stays free of a UnityEngine dependency.</summary>
        private static int Mathf_Clamp(int value, int min, int max) =>
            value < min ? min : value > max ? max : value;

        public override string ToString() =>
            $"{Kind}(seat:{Seat} a:{A} b:{B} c:{C} d:{D}{(string.IsNullOrEmpty(Text) ? "" : $" \"{Text}\"")})";
    }
}
