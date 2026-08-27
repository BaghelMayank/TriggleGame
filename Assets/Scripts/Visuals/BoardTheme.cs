using UnityEngine;

namespace Triggle.Visuals
{
    /// <summary>
    /// A named colour scheme for the board surface. Selected in Settings before a match starts.
    /// </summary>
    /// <remarks>
    /// Themes only recolour the board and its background; they never change geometry, so switching one
    /// mid-match would be safe. The Settings screen still gates them alongside board size because the
    /// two controls sit together and board <em>size</em> genuinely cannot change mid-match.
    /// </remarks>
    [CreateAssetMenu(fileName = "BoardTheme", menuName = "Triggle/Board Theme", order = 1)]
    public sealed class BoardTheme : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Shown under the swatch in the Settings theme picker.")]
        public string displayName = "Classic Dark";

        [Header("Environment")]
        [Tooltip("Camera clear colour behind the board.")]
        public Color backgroundColor = new Color(0.055f, 0.063f, 0.090f);

        [Header("Board Surface")]
        public Color slabColor = new Color(0.106f, 0.122f, 0.169f);

        [Tooltip("Thin accent band around the slab's top edge.")]
        public Color rimColor = new Color(0.29f, 0.78f, 0.55f);

        [Tooltip("Lines drawn along every unit edge. The main readability cue - keep it visible.")]
        public Color latticeLineColor = new Color(0.32f, 0.36f, 0.46f);

        [Tooltip("Disc under each peg.")]
        public Color socketColor = new Color(0.055f, 0.063f, 0.090f);

        [Header("Pegs")]
        public Color pegColor = new Color(0.86f, 0.87f, 0.90f);

        [Header("Board Slab Finish")]
        [Range(0f, 1f)] public float slabSmoothness = 0.12f;

        /// <summary>
        /// Built-in theme definitions, used to author the asset set on first run so the picker is
        /// populated without hand-making six ScriptableObjects.
        /// </summary>
        public struct Preset
        {
            public string Name;
            public Color Background, Slab, Rim, Lines, Socket, Peg;
            public float Smoothness;
        }

        /// <summary>The six shipped themes, in picker order.</summary>
        public static readonly Preset[] Presets =
        {
            new Preset
            {
                Name = "Classic Dark",
                Background = new Color(0.055f, 0.063f, 0.090f),
                Slab = new Color(0.106f, 0.122f, 0.169f),
                Rim = new Color(0.29f, 0.78f, 0.55f),
                Lines = new Color(0.32f, 0.36f, 0.46f),
                Socket = new Color(0.043f, 0.051f, 0.075f),
                Peg = new Color(0.86f, 0.87f, 0.90f),
                Smoothness = 0.12f
            },
            new Preset
            {
                Name = "Neon Grid",
                Background = new Color(0.035f, 0.020f, 0.070f),
                Slab = new Color(0.075f, 0.047f, 0.145f),
                Rim = new Color(0.05f, 0.95f, 0.95f),
                Lines = new Color(0.55f, 0.25f, 0.85f),
                Socket = new Color(0.028f, 0.016f, 0.055f),
                Peg = new Color(0.90f, 0.92f, 1.00f),
                Smoothness = 0.35f
            },
            new Preset
            {
                Name = "Pastel Chill",
                Background = new Color(0.87f, 0.86f, 0.93f),
                Slab = new Color(0.95f, 0.94f, 0.97f),
                Rim = new Color(0.55f, 0.72f, 0.95f),
                Lines = new Color(0.72f, 0.70f, 0.82f),
                Socket = new Color(0.80f, 0.79f, 0.88f),
                Peg = new Color(0.99f, 0.99f, 1.00f),
                Smoothness = 0.20f
            },
            new Preset
            {
                Name = "Deep Space",
                Background = new Color(0.020f, 0.024f, 0.055f),
                Slab = new Color(0.055f, 0.070f, 0.130f),
                Rim = new Color(0.45f, 0.55f, 0.95f),
                Lines = new Color(0.28f, 0.34f, 0.55f),
                Socket = new Color(0.016f, 0.020f, 0.043f),
                Peg = new Color(0.88f, 0.91f, 0.98f),
                Smoothness = 0.28f
            },
            new Preset
            {
                Name = "Ember",
                Background = new Color(0.075f, 0.043f, 0.035f),
                Slab = new Color(0.145f, 0.086f, 0.070f),
                Rim = new Color(0.98f, 0.52f, 0.22f),
                Lines = new Color(0.50f, 0.32f, 0.26f),
                Socket = new Color(0.055f, 0.031f, 0.024f),
                Peg = new Color(0.96f, 0.92f, 0.86f),
                Smoothness = 0.15f
            },
            new Preset
            {
                Name = "Slate Mono",
                Background = new Color(0.086f, 0.090f, 0.098f),
                Slab = new Color(0.165f, 0.173f, 0.184f),
                Rim = new Color(0.82f, 0.84f, 0.86f),
                Lines = new Color(0.38f, 0.40f, 0.43f),
                Socket = new Color(0.063f, 0.067f, 0.075f),
                Peg = new Color(0.92f, 0.93f, 0.94f),
                Smoothness = 0.08f
            }
        };

        /// <summary>Copies a preset's values into this asset.</summary>
        public void ApplyPreset(Preset preset)
        {
            displayName = preset.Name;
            backgroundColor = preset.Background;
            slabColor = preset.Slab;
            rimColor = preset.Rim;
            latticeLineColor = preset.Lines;
            socketColor = preset.Socket;
            pegColor = preset.Peg;
            slabSmoothness = preset.Smoothness;
        }
    }
}
