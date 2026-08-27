namespace Triggle.Core
{
    /// <summary>
    /// The fixed set of things a player can say. Quick-chat rather than free text.
    /// </summary>
    /// <remarks>
    /// Three reasons this is a table and not a keyboard. It is playable one-handed on a phone, which
    /// free text is not. It travels as a single integer, so a message costs the same as a move and
    /// cannot be used to smuggle anything. And it needs no moderation, filtering or reporting flow -
    /// which a shipped game with open text chat between strangers does need, and which is a great deal
    /// more work than the chat panel itself.
    /// <para>
    /// Ids are the wire format, so entries may be appended but never reordered or removed: an older
    /// build would otherwise show the wrong phrase for an id it does not have.
    /// </para>
    /// </remarks>
    public static class ChatPhrases
    {
        /// <summary>One thing a player can say: an emote glyph and the words beside it.</summary>
        public readonly struct Phrase
        {
            /// <summary>Index into the generated emote sprites.</summary>
            public readonly int Emote;

            public readonly string Text;

            public Phrase(int emote, string text)
            {
                Emote = emote;
                Text = text;
            }
        }

        private static readonly Phrase[] Entries =
        {
            new Phrase(0, "Nice one!"),
            new Phrase(1, "Good game!"),
            new Phrase(2, "Wow!"),
            new Phrase(3, "Thinking..."),
            new Phrase(4, "Good luck!"),
            new Phrase(5, "So close!")
        };

        /// <summary>How many phrases exist. Matches <c>TriggleUISprites.EmoteCount</c>.</summary>
        public static int Count => Entries.Length;

        /// <summary>True when an id names a phrase this build knows.</summary>
        public static bool IsValid(int id) => id >= 0 && id < Entries.Length;

        /// <summary>
        /// The phrase for an id. An unknown id - a peer on a newer build - yields a neutral placeholder
        /// rather than an exception, so a version mismatch degrades to a shrug instead of a crash.
        /// </summary>
        public static Phrase Get(int id) => IsValid(id) ? Entries[id] : new Phrase(0, "...");
    }
}
