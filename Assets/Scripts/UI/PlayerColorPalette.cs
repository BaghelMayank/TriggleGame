using System;
using System.Collections.Generic;
using Triggle.Core;
using UnityEngine;

namespace Triggle.UI
{
    /// <summary>
    /// Authoring asset that maps each seat to a display name, a colour and optional material overrides.
    /// Every visual system reads from here, so re-skinning the game is a single-asset edit.
    /// </summary>
    [CreateAssetMenu(fileName = "PlayerColorPalette", menuName = "Triggle/Player Color Palette", order = 0)]
    public sealed class PlayerColorPalette : ScriptableObject
    {
        /// <summary>Per-seat style entry.</summary>
        [Serializable]
        public sealed class PlayerStyle
        {
            [Tooltip("Name shown in the turn indicator and scoreboard.")]
            public string displayName = "Player";

            [Tooltip("Primary colour: tokens, bands, scoreboard swatch and highlights.")]
            public Color color = Color.white;

            [Tooltip("Optional material for claimed-triangle tokens. Generated from the colour when empty.")]
            public Material tokenMaterial;

            [Tooltip("Optional material for this player's rubber bands. Generated from the colour when empty.")]
            public Material bandMaterial;
        }

        [Header("Seats (index 0 = Player 1)")]
        [SerializeField]
        private PlayerStyle[] players =
        {
            new PlayerStyle { displayName = "Crimson", color = new Color(0.95f, 0.27f, 0.32f) },
            new PlayerStyle { displayName = "Azure",   color = new Color(0.24f, 0.60f, 0.96f) },
            new PlayerStyle { displayName = "Verdant", color = new Color(0.33f, 0.84f, 0.44f) },
            new PlayerStyle { displayName = "Amber",   color = new Color(0.98f, 0.79f, 0.24f) }
        };

        [Header("Neutral")]
        [Tooltip("Colour used for unowned elements and for bands when tinting per player is disabled.")]
        [SerializeField] private Color neutralColor = new Color(0.72f, 0.74f, 0.80f);

        [SerializeField] private string neutralDisplayName = "Neutral";

        // Keyed by palette slot, not seat: players can swap colours in the lobby.
        private readonly Dictionary<int, Material> _tokenMaterialCache = new Dictionary<int, Material>();
        private readonly Dictionary<int, Material> _bandMaterialCache = new Dictionary<int, Material>();

        private static PlayerColorPalette _fallback;

        /// <summary>
        /// A palette instance created on demand, so systems keep working when no asset is wired up.
        /// Not saved to disk.
        /// </summary>
        public static PlayerColorPalette Fallback
        {
            get
            {
                if (_fallback == null)
                {
                    _fallback = CreateInstance<PlayerColorPalette>();
                    _fallback.name = "PlayerColorPalette (runtime fallback)";
                    _fallback.hideFlags = HideFlags.DontSave;
                }

                return _fallback;
            }
        }

        public Color NeutralColor => neutralColor;

        /// <summary>Number of configured seats.</summary>
        public int SeatCount => players?.Length ?? 0;

        [Header("Colour Assignment")]
        [Tooltip("Let players pick which palette colour they use in the lobby. Off pins each seat to " +
                 "its own colour.")]
        [SerializeField] private bool useLobbyColorChoice = true;

        /// <summary>
        /// The seat's colour. With lobby colour choice enabled this is whichever palette slot the player
        /// picked, which is why it goes through <see cref="PlayerProfiles"/> rather than the seat index.
        /// </summary>
        public Color GetColor(PlayerId player)
        {
            if (player == PlayerId.None || players == null) return neutralColor;

            int slot = useLobbyColorChoice
                ? PlayerProfiles.GetColorIndex(player)
                : (int)player - 1;

            return GetColorBySlot(slot);
        }

        /// <summary>The colour in a specific palette slot, independent of who is using it.</summary>
        public Color GetColorBySlot(int slot)
        {
            if (players == null || players.Length == 0) return neutralColor;

            int index = Mathf.Clamp(slot, 0, players.Length - 1);
            return players[index] != null ? players[index].color : neutralColor;
        }

        /// <summary>The name of a palette slot ("Ruby", "Azure", ...), for the lobby swatch labels.</summary>
        public string GetColorName(int slot)
        {
            if (players == null || players.Length == 0) return neutralDisplayName;

            int index = Mathf.Clamp(slot, 0, players.Length - 1);
            PlayerStyle style = players[index];
            return style != null && !string.IsNullOrWhiteSpace(style.displayName)
                ? style.displayName
                : $"Colour {index + 1}";
        }

        /// <summary>
        /// Default display name for a seat. Follows the chosen colour, so a player who picks Azure gets
        /// "Azure" as their placeholder rather than the name of the colour they abandoned.
        /// </summary>
        public string GetDisplayName(PlayerId player)
        {
            if (player == PlayerId.None) return neutralDisplayName;

            int slot = useLobbyColorChoice ? PlayerProfiles.GetColorIndex(player) : (int)player - 1;
            return GetColorName(slot);
        }

        /// <summary>
        /// Material for claimed-triangle tokens. Uses the authored override when present, otherwise a
        /// cached lit material tinted to the player's chosen colour.
        /// </summary>
        public Material GetTokenMaterial(PlayerId player) =>
            GetOrCreateMaterial(SlotOf(player), _tokenMaterialCache, "Token", false);

        /// <summary>Material for this player's rubber bands (unlit so bands read at any camera angle).</summary>
        public Material GetBandMaterial(PlayerId player) =>
            GetOrCreateMaterial(SlotOf(player), _bandMaterialCache, "Band", true);

        private int SlotOf(PlayerId player)
        {
            if (player == PlayerId.None) return 0;
            return useLobbyColorChoice ? PlayerProfiles.GetColorIndex(player) : (int)player - 1;
        }

        /// <summary>
        /// One material per palette <em>slot</em>, not per seat. Keying by colour means two players who
        /// swap colours in the lobby immediately get the right materials, with no stale cache entry.
        /// </summary>
        private Material GetOrCreateMaterial(int slot, Dictionary<int, Material> cache,
                                             string label, bool unlit)
        {
            slot = players != null && players.Length > 0
                ? Mathf.Clamp(slot, 0, players.Length - 1)
                : 0;

            Material authored = players != null && slot < players.Length && players[slot] != null
                ? (unlit ? players[slot].bandMaterial : players[slot].tokenMaterial)
                : null;

            if (authored != null) return authored;

            if (cache.TryGetValue(slot, out Material cached) && cached != null) return cached;

            Material material = unlit
                ? MaterialUtility.CreateDefaultUnlitMaterial()
                : MaterialUtility.CreateDefaultLitMaterial();

            material.name = $"{label}_Slot{slot}_Mat";
            material.hideFlags = HideFlags.DontSave;
            MaterialUtility.SetColor(material, GetColorBySlot(slot));

            cache[slot] = material;
            return material;
        }

        private PlayerStyle GetStyle(PlayerId player)
        {
            if (player == PlayerId.None || players == null) return null;

            int index = (int)player - 1;
            return index >= 0 && index < players.Length ? players[index] : null;
        }

        private void OnDisable()
        {
            // Generated materials are per-session; drop them so editor play cycles do not accumulate.
            ReleaseCache(_tokenMaterialCache);
            ReleaseCache(_bandMaterialCache);
        }

        private static void ReleaseCache(Dictionary<int, Material> cache)
        {
            foreach (KeyValuePair<int, Material> entry in cache)
            {
                if (entry.Value == null) continue;
                if (Application.isPlaying) Destroy(entry.Value);
                else DestroyImmediate(entry.Value);
            }

            cache.Clear();
        }

        private void OnValidate()
        {
            if (players != null && players.Length == 4) return;

            // Keep the array at exactly four seats so index math stays valid.
            var resized = new PlayerStyle[4];
            for (int i = 0; i < 4; i++)
            {
                resized[i] = players != null && i < players.Length && players[i] != null
                    ? players[i]
                    : new PlayerStyle { displayName = $"Player {i + 1}", color = Color.HSVToRGB(i * 0.25f, 0.7f, 0.95f) };
            }

            players = resized;
        }
    }
}
