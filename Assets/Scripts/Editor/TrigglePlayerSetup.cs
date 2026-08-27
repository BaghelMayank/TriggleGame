using UnityEditor;
using UnityEngine;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Applies the project-level player settings the UI is designed against.
    /// </summary>
    /// <remarks>
    /// Kept as its own menu entry rather than folded into the scene builder: these are project
    /// settings, not scene contents, and a command called "Build Play Scene" quietly rewriting the
    /// player's orientation and Android options would be a nasty surprise.
    /// </remarks>
    public static class TrigglePlayerSetup
    {
        [MenuItem("Tools/Triggle/Configure Player Settings (Landscape)", false, 23)]
        public static void Configure()
        {
            // Landscape both ways: the phone can be rotated 180 degrees, but never into portrait.
            // Every panel is authored at a 1080-unit height for a wide screen, and the board itself
            // reads far better wide than tall.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            // Draw into the display cutout rather than letting Android letterbox it away. That is what
            // makes the notch strip usable screen space - and what makes CanvasSafeArea necessary,
            // since nothing else is then keeping the HUD's corner cards clear of the camera.
            PlayerSettings.Android.renderOutsideSafeArea = true;

            AssetDatabase.SaveAssets();

            Debug.Log(
                "[Triggle] Player settings configured.\n" +
                "  Orientation : auto-rotate, landscape left and right only\n" +
                "  Android     : renders outside the safe area (CanvasSafeArea insets the UI)");
        }
    }
}
