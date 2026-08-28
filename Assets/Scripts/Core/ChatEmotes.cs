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

        /// <summary>One emoji: its sprite name, and words for when a sprite cannot be drawn.</summary>
        public readonly struct Emote
        {
            /// <summary>Sprite name inside <see cref="SpriteAsset"/>, which is its unicode code point.</summary>
            public readonly string Sprite;

            /// <summary>Fallback wording, used in places that cannot render a sprite.</summary>
            public readonly string Label;

            public Emote(string sprite, string label)
            {
                Sprite = sprite;
                Label = label;
            }
        }

        private static readonly Emote[] Entries =
        {
            new Emote("1f602", "laughing"),      // face with tears of joy
            new Emote("1f60d", "loving it"),     // smiling face with heart eyes
            new Emote("2639", "sad"),            // frowning face
            new Emote("1f605", "sweating"),      // smiling face with cold sweat
            new Emote("1f60e", "too cool"),      // smiling face with sunglasses
            new Emote("1f923", "rolling")        // rolling on the floor laughing
        };

        public static int Count => Entries.Length;

        public static bool IsValid(int id) => id >= 0 && id < Entries.Length;

        /// <summary>
        /// The emoji for an id, or a neutral stand-in when a peer on a newer build sends one this
        /// version does not know.
        /// </summary>
        public static Emote Get(int id) => IsValid(id) ? Entries[id] : new Emote("1f60a", "...");

        /// <summary>The rich-text tag that renders this emoji inside a TextMeshPro label.</summary>
        public static string Tag(int id) => $"<sprite=\"{SpriteAsset}\" name=\"{Get(id).Sprite}\">";
    }
}
