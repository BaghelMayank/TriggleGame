using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Triggle.Core
{
    /// <summary>
    /// Runtime store for the names players type on the main menu, persisted with
    /// <see cref="PlayerPrefs"/> so they are remembered between sessions.
    /// </summary>
    /// <remarks>
    /// Deliberately decoupled from the colour palette: callers pass the palette's default as the
    /// fallback, so this class has no dependency on the UI layer.
    /// </remarks>
    public static class PlayerProfiles
    {
        /// <summary>Longest name accepted; also the character limit set on the menu input fields.</summary>
        public const int MaxNameLength = 14;

        private const string PrefKeyPrefix = "triggle.player.name.";
        private const string ColorKeyPrefix = "triggle.player.color.";

        /// <summary>Number of colours in the palette a player can choose between.</summary>
        public const int ColorSlotCount = 4;

        private static readonly Dictionary<PlayerId, string> Names = new Dictionary<PlayerId, string>();
        private static readonly Dictionary<PlayerId, int> ColorIndices = new Dictionary<PlayerId, int>();
        private static bool _loaded;

        /// <summary>Loads every stored name. Safe to call repeatedly; only the first call does work.</summary>
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            for (int seat = 1; seat <= 4; seat++)
            {
                var player = (PlayerId)seat;
                string stored = PlayerPrefs.GetString(PrefKeyPrefix + seat, string.Empty);
                string clean = Sanitize(stored);

                if (!string.IsNullOrEmpty(clean)) Names[player] = clean;

                // Default: each seat uses its own palette colour.
                int colorIndex = PlayerPrefs.GetInt(ColorKeyPrefix + seat, seat - 1);
                ColorIndices[player] = Mathf.Clamp(colorIndex, 0, ColorSlotCount - 1);
            }
        }

        /// <summary>
        /// Which palette colour this seat uses. Defaults to the seat's own index.
        /// </summary>
        public static int GetColorIndex(PlayerId player)
        {
            if (player == PlayerId.None) return 0;

            Load();
            return ColorIndices.TryGetValue(player, out int index)
                ? Mathf.Clamp(index, 0, ColorSlotCount - 1)
                : Mathf.Clamp((int)player - 1, 0, ColorSlotCount - 1);
        }

        /// <summary>
        /// Assigns a palette colour to a seat. If another seat already holds that colour the two swap,
        /// so every player always has a distinct colour and no seat is ever left without one.
        /// </summary>
        public static void SetColorIndex(PlayerId player, int colorIndex)
        {
            if (player == PlayerId.None) return;

            Load();
            int target = Mathf.Clamp(colorIndex, 0, ColorSlotCount - 1);
            int current = GetColorIndex(player);
            if (target == current) return;

            // Find whoever currently owns the requested colour and hand them this seat's colour.
            for (int seat = 1; seat <= 4; seat++)
            {
                var other = (PlayerId)seat;
                if (other == player) continue;

                if (GetColorIndex(other) == target)
                {
                    ColorIndices[other] = current;
                    PlayerPrefs.SetInt(ColorKeyPrefix + seat, current);
                    break;
                }
            }

            ColorIndices[player] = target;
            PlayerPrefs.SetInt(ColorKeyPrefix + (int)player, target);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Stores a name for a seat. Blank or whitespace-only input clears the entry, so the seat falls
        /// back to its palette name.
        /// </summary>
        public static void SetName(PlayerId player, string name)
        {
            if (player == PlayerId.None) return;

            Load();
            string clean = Sanitize(name);

            if (string.IsNullOrEmpty(clean))
            {
                Names.Remove(player);
                PlayerPrefs.DeleteKey(PrefKeyPrefix + (int)player);
            }
            else
            {
                Names[player] = clean;
                PlayerPrefs.SetString(PrefKeyPrefix + (int)player, clean);
            }

            PlayerPrefs.Save();
        }

        /// <summary>True when the player typed a name for this seat.</summary>
        public static bool HasName(PlayerId player)
        {
            Load();
            return Names.TryGetValue(player, out string stored) && !string.IsNullOrEmpty(stored);
        }

        /// <summary>The stored name, or <paramref name="fallback"/> when the seat has none.</summary>
        public static string GetName(PlayerId player, string fallback)
        {
            Load();
            return Names.TryGetValue(player, out string stored) && !string.IsNullOrEmpty(stored)
                ? stored
                : fallback;
        }

        /// <summary>The stored name, or an empty string - use this to populate an input field.</summary>
        public static string GetRawName(PlayerId player)
        {
            Load();
            return Names.TryGetValue(player, out string stored) ? stored : string.Empty;
        }

        /// <summary>Forgets every stored name.</summary>
        public static void Clear()
        {
            Load();
            Names.Clear();

            for (int seat = 1; seat <= 4; seat++) PlayerPrefs.DeleteKey(PrefKeyPrefix + seat);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Trims, collapses runs of whitespace, strips control characters and caps the length, so a
        /// pasted or malformed name can never break the HUD layout.
        /// </summary>
        /// <remarks>
        /// Angle brackets are also dropped: names are echoed into TextMeshPro labels that have rich
        /// text enabled, so a typed "&lt;color=#ff0000&gt;" would otherwise be interpreted as markup
        /// and could corrupt the rest of the label.
        /// </remarks>
        private static string Sanitize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var builder = new StringBuilder(raw.Length);
            bool lastWasSpace = false;

            foreach (char c in raw.Trim())
            {
                if (char.IsControl(c)) continue;
                if (c == '<' || c == '>') continue;   // never let names inject TMP rich-text tags

                if (char.IsWhiteSpace(c))
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                    builder.Append(' ');
                    continue;
                }

                lastWasSpace = false;
                builder.Append(c);

                if (builder.Length >= MaxNameLength) break;
            }

            return builder.ToString().Trim();
        }
    }
}
