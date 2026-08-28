namespace Triggle.Core
{
    /// <summary>
    /// The emoji a player can send, drawn from the EmojiOne sprite sheet that ships with TextMeshPro.
    /// </summary>
    /// <remarks>
    /// Real emoji rather than generated glyphs: TMP renders them inline from a sprite asset already in
    /// the project, so they arrive in colour, at any size, on every platform, with none of the drawing
    /// or atlas work that hand-made ones would need.
    /// <para>
    /// <b>The bundled sheet is only sixteen faces.</b> There is no plain heart, no crying face and no
    /// exploding head in it, so this is the closest honest spread rather than the exact list anyone would
    /// pick. Adding more means importing emoji art and regenerating the atlas, which carries its own
    /// licence terms - the sheet already in the project ships with an attribution file beside it.
    /// </para>
    /// <para>
    /// Ids are the wire format, so entries may be appended but never reordered or removed.
    /// </para>
    /// </remarks>
    public static class ChatEmotes
    {
        /// <summary>The sprite asset TextMeshPro resolves the emoji from.</summary>
        public const string SpriteAsset = "EmojiOne";

        /// <summary>One emoji: where it sits in the sheet, and words for when it cannot be drawn.</summary>
        public readonly struct Emote
        {
            /// <summary>Position in <see cref="SpriteAsset"/>.</summary>
            public readonly int Index;

            /// <summary>Fallback wording, used in places that cannot render a sprite.</summary>
            public readonly string Label;

            public Emote(int index, string label)
            {
                Index = index;
                Label = label;
            }
        }

        /// <summary>
        /// Indices into the EmojiOne sheet, not names.
        /// </summary>
        /// <remarks>
        /// The sheet's entries are not consistently named after their code points - index 6 is called
        /// "Face with tears of joy" rather than "1f602", while its neighbours do use the code point. A
        /// name that misses resolves to the sheet's <c>.notdef</c> entry, which draws a question mark,
        /// and nothing warns: the tag is valid, it just points at nothing. Indices are unambiguous, and
        /// the UI verification checks every one of these against the asset so a reordered sheet fails
        /// loudly rather than shipping question marks.
        /// </remarks>
        private static readonly Emote[] Entries =
        {
            new Emote(6, "laughing"),      // Face with tears of joy
            new Emote(2, "loving it"),     // 1f60d, heart eyes
            new Emote(15, "sad"),          // 2639, frowning face
            new Emote(9, "sweating"),      // 1f605, cold sweat
            new Emote(3, "too cool"),      // 1f60e, sunglasses
            new Emote(13, "rolling")       // 1f923, rolling on the floor
        };

        public static int Count => Entries.Length;

        public static bool IsValid(int id) => id >= 0 && id < Entries.Length;

        /// <summary>
        /// The emoji for an id, or a neutral stand-in when a peer on a newer build sends one this
        /// version does not know.
        /// </summary>
        public static Emote Get(int id) => IsValid(id) ? Entries[id] : new Emote(0, "...");

        /// <summary>The rich-text tag that renders this emoji inside a TextMeshPro label.</summary>
        public static string Tag(int id) => $"<sprite=\"{SpriteAsset}\" index={Get(id).Index}>";
    }
}
