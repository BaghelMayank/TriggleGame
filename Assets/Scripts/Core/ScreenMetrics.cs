using UnityEngine;

namespace Triggle.Core
{
    /// <summary>
    /// The screen dimensions and display cutout the UI lays itself out against.
    /// </summary>
    /// <remarks>
    /// A thin wrapper over <see cref="Screen"/> rather than a direct call, because
    /// <see cref="Screen.width"/> and <see cref="Screen.safeArea"/> are read-only and driven by the
    /// device - which makes a notched phone impossible to test without one in your hand. In the editor
    /// these can be overridden, so the layout audit can drive the real components through a phone's
    /// resolution and cutout and measure what comes out. The overrides are compiled out of a player
    /// build entirely, so shipping code always reads the device.
    /// </remarks>
    public static class ScreenMetrics
    {
#if UNITY_EDITOR
        /// <summary>Editor-only override for <see cref="Size"/>. Null means "ask the device".</summary>
        public static Vector2Int? SizeOverride;

        /// <summary>Editor-only override for <see cref="SafeArea"/>, in pixels. Null means "ask the device".</summary>
        public static Rect? SafeAreaOverride;

        /// <summary>Drops both overrides. Always call this when a test finishes.</summary>
        public static void ClearOverrides()
        {
            SizeOverride = null;
            SafeAreaOverride = null;
        }
#endif

        /// <summary>Backbuffer size in pixels.</summary>
        public static Vector2Int Size
        {
            get
            {
#if UNITY_EDITOR
                if (SizeOverride.HasValue) return SizeOverride.Value;
#endif
                return new Vector2Int(Screen.width, Screen.height);
            }
        }

        /// <summary>
        /// The part of the screen not covered by a notch, punch-hole or gesture bar, in pixels.
        /// </summary>
        public static Rect SafeArea
        {
            get
            {
#if UNITY_EDITOR
                if (SafeAreaOverride.HasValue) return SafeAreaOverride.Value;
#endif
                return Screen.safeArea;
            }
        }
    }
}
