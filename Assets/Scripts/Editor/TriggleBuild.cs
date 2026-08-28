using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Builds the Android player, from the menu or from a command line.
    /// </summary>
    /// <remarks>
    /// The scene list is passed to the build explicitly rather than read from Build Settings, so a
    /// player always contains the generated scene whether or not anyone remembered to add it to the
    /// list - the one mistake that produces a working build of an empty game.
    /// </remarks>
    public static class TriggleBuild
    {
        private const string ScenePath = "Assets/Scenes/Triggle.unity";
        private const string OutputArgument = "-triggleApkPath";

        [MenuItem("Tools/Triggle/Build Android APK", false, 60)]
        public static void BuildAndroidFromMenu()
        {
            string path = EditorUtility.SaveFilePanel("Build Triggle APK", "", "Triggle", "apk");
            if (string.IsNullOrEmpty(path)) return;

            Build(path);
        }

        /// <summary>
        /// Batch-mode entry point. Reads the destination from <c>-triggleApkPath</c>.
        /// </summary>
        public static void BuildAndroidFromCommandLine()
        {
            string path = ReadArgument(OutputArgument);

            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError($"[Triggle] {OutputArgument} was not supplied; nothing to build to.");
                EditorApplication.Exit(2);
                return;
            }

            EditorApplication.Exit(Build(path) ? 0 : 1);
        }

        private static bool Build(string outputPath)
        {
            if (!File.Exists(ScenePath))
            {
                Debug.LogError($"[Triggle] {ScenePath} does not exist. Run Tools > Triggle > " +
                               "Build Play Scene first.");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

            // An APK, not an app bundle: this is for installing on a handset directly.
            EditorUserBuildSettings.buildAppBundle = false;

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.Log("[Triggle] Switching the active build target to Android; this reimports " +
                          "assets and takes a while on the first run.");

                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android,
                                                                BuildTarget.Android);
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[Triggle] APK built.\n" +
                          $"  Output : {outputPath}\n" +
                          $"  Size   : {summary.totalSize / (1024f * 1024f):0.0} MB\n" +
                          $"  Time   : {summary.totalTime.TotalMinutes:0.0} min\n" +
                          $"  Scene  : {ScenePath}");

                return true;
            }

            Debug.LogError($"[Triggle] APK build {summary.result} with {summary.totalErrors} errors.");
            return false;
        }

        private static string ReadArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];

            return null;
        }
    }
}
