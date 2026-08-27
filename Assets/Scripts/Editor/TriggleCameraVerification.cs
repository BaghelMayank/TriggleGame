using System.Text;
using Triggle.Core;
using Triggle.Grid;
using Triggle.Visuals;
using UnityEditor;
using UnityEngine;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Checks that <see cref="BoardCameraRig"/> keeps the whole board on screen at every board size the
    /// Settings panel offers, across a spread of window shapes.
    /// </summary>
    /// <remarks>
    /// This is the regression test for the bug it was written against: board radius became a runtime
    /// setting while the camera position stayed baked for radius 3, so radius 4 and 5 boards were
    /// clipped off the edges of the screen. Every peg and every point on the slab rim is projected to
    /// viewport space and must land inside 0..1 on both axes, in front of the camera.
    /// </remarks>
    public static class TriggleCameraVerification
    {
        /// <summary>Window shapes to test: ultrawide through to a portrait phone.</summary>
        private static readonly (string Name, float Aspect)[] Aspects =
        {
            ("21:9 ultrawide", 21f / 9f),
            ("16:9 desktop", 16f / 9f),
            ("4:3 tablet", 4f / 3f),
            ("1:1 square", 1f),
            ("9:16 portrait", 9f / 16f),
            ("9:19.5 phone", 9f / 19.5f)
        };

        private const int RimSamples = 180;

        // Must match what TriggleSceneBuilder wires onto the rig, or this measures a different rig
        // from the one that ships.
        private const float SidePadding = 0.04f;
        private const float TopMargin = (131f + 26f) / 1080f;
        private const float BottomMargin = (128f + 26f) / 1080f;

        /// <summary>How unequal the gaps above and below the board may be, in viewport units.</summary>
        private const float SkewTolerance = 0.01f;

        private const float Tolerance = 0.002f;

        [MenuItem("Tools/Triggle/Verify Camera Framing", false, 41)]
        public static void Run()
        {
            var report = new StringBuilder();
            report.AppendLine("[Triggle] Camera framing verification.");
            report.AppendLine();
            report.AppendLine("  Radius  Aspect             Board h   Gap below  Gap above    Skew");
            report.AppendLine("  ------------------------------------------------------------------");

            GameObject boardHost = null;
            GameObject cameraHost = null;
            int failures = 0;
            int skewed = 0;

            try
            {
                boardHost = new GameObject("~TriggleCameraVerifyBoard")
                { hideFlags = HideFlags.HideAndDontSave };

                var board = boardHost.AddComponent<BoardManager>();
                var visuals = boardHost.AddComponent<BoardVisuals>();

                // Awake does not run in edit mode, so the dependency it would resolve has to be wired
                // by hand. Without this Rebuild bails out, OuterRadius stays zero, and the audit
                // silently measures the peg ring instead of the slab.
                using (var so = new SerializedWiring(visuals)) so.Ref("board", board);

                cameraHost = new GameObject("~TriggleCameraVerifyCamera")
                { hideFlags = HideFlags.HideAndDontSave };

                var camera = cameraHost.AddComponent<Camera>();
                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 200f;

                var rig = cameraHost.AddComponent<BoardCameraRig>();
                Wire(rig, board, visuals);

                for (int radius = TrigglePrefs.MinBoardRadius; radius <= TrigglePrefs.MaxBoardRadius; radius++)
                {
                    board.SetRadius(radius);
                    board.Build();

                    // OnEnable does not run in edit mode, so the geometry is rebuilt by hand - this is
                    // what publishes the slab radius the rig frames to.
                    visuals.Rebuild();

                    if (visuals.OuterRadius <= 0f)
                    {
                        Debug.LogError("[Triggle] BoardVisuals produced no slab radius - the audit " +
                                       "would measure nothing. Aborting.");
                        return;
                    }

                    for (int a = 0; a < Aspects.Length; a++)
                    {
                        camera.aspect = Aspects[a].Aspect;
                        rig.Frame();

                        Measure(camera, board, visuals, out float low, out float high, out bool clipped);

                        // Distance from the board to the strip the HUD occupies, at each end. Equal
                        // gaps is the whole point of the band fit, so the skew between them is the
                        // number that actually matters here.
                        float gapBelow = low - BottomMargin;
                        float gapAbove = 1f - TopMargin - high;
                        float skew = Mathf.Abs(gapAbove - gapBelow);

                        if (clipped) failures++;
                        if (skew > SkewTolerance) skewed++;

                        string verdict = clipped ? "  CLIPPED" : skew > SkewTolerance ? "  SKEWED" : "";

                        report.AppendLine(
                            $"  {radius,6}  {Aspects[a].Name,-18} {high - low,7:0.000} " +
                            $"{gapBelow,10:0.000} {gapAbove,10:0.000} {skew,7:0.000}" +
                            $"   [fit {rig.FittedLow:0.000}-{rig.FittedHigh:0.000} rise {rig.FittedRise:0.00}]" +
                            $"{verdict}");
                    }
                }

                camera.ResetAspect();
            }
            finally
            {
                if (cameraHost != null) Object.DestroyImmediate(cameraHost);
                if (boardHost != null) Object.DestroyImmediate(boardHost);
            }

            report.AppendLine();
            report.AppendLine("  Board h: fraction of screen height the board occupies.");
            report.AppendLine("  Gaps: viewport distance from the board to the HUD strip, below and above.");
            report.AppendLine($"  Skew: difference between the two gaps. Tolerance {SkewTolerance:0.000}.");
            report.AppendLine($"  Clipped configurations: {failures}");
            report.AppendLine($"  Skewed configurations:  {skewed}");

            if (failures > 0 || skewed > 0) Debug.LogError(report.ToString());
            else Debug.Log(report.ToString());
        }

        private static void Wire(BoardCameraRig rig, BoardManager board, BoardVisuals visuals)
        {
            using var so = new SerializedWiring(rig);

            so.Ref("board", board);
            so.Ref("boardVisuals", visuals);
            so.Float("pitch", 56f);
            so.Float("sidePadding", SidePadding);
            so.Float("topMargin", TopMargin);
            so.Float("bottomMargin", BottomMargin);
        }

        /// <summary>
        /// Vertical viewport span the board occupies, and whether any of it escapes the band.
        /// </summary>
        /// <remarks>
        /// Samples the real content - every peg at both its base and post height, plus the slab outline
        /// walked edge by edge - rather than the twelve corners the rig fits to. Measuring the same
        /// twelve points the rig optimised would only prove it can do arithmetic; walking the actual
        /// hexagon catches a corner poking out between them.
        /// </remarks>
        private static void Measure(Camera camera, BoardManager board, BoardVisuals visuals,
                                     out float low, out float high, out bool clipped)
        {
            low = float.PositiveInfinity;
            high = float.NegativeInfinity;
            clipped = false;

            for (int i = 0; i < board.Pegs.Count; i++)
            {
                Accumulate(camera, board.Pegs[i].WorldPosition, ref low, ref high, ref clipped);
                Accumulate(camera, board.Pegs[i].WorldPosition + Vector3.up * 0.35f,
                           ref low, ref high, ref clipped);
            }

            float radius = visuals.OuterRadius;
            Vector3 center = board.transform.position;

            // The slab is a hexagon, so walk its edges. A circle of the same radius bulges past the flat
            // sides and would report clipping where there is no slab at all.
            const int PerEdge = RimSamples / 6;

            for (int edge = 0; edge < 6; edge++)
            {
                Vector3 from = center + HexCorner(edge, radius);
                Vector3 to = center + HexCorner(edge + 1, radius);

                for (int s = 0; s < PerEdge; s++)
                    Accumulate(camera, Vector3.Lerp(from, to, s / (float)PerEdge),
                               ref low, ref high, ref clipped);
            }

            if (low > high) { low = 0f; high = 1f; }
        }

        /// <summary>Matches <c>BoardVisuals.HexCorner</c>, so these are the slab's real corners.</summary>
        private static Vector3 HexCorner(int index, float radius)
        {
            float angle = index * 60f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        }

        private static void Accumulate(Camera camera, Vector3 worldPoint,
                                        ref float low, ref float high, ref bool clipped)
        {
            Vector3 viewport = camera.WorldToViewportPoint(worldPoint);

            if (viewport.z <= 0f)
            {
                clipped = true;
                return;
            }

            low = Mathf.Min(low, viewport.y);
            high = Mathf.Max(high, viewport.y);

            // Off the screen sideways, or into a strip the HUD owns.
            if (viewport.x < -Tolerance || viewport.x > 1f + Tolerance) clipped = true;
            if (viewport.y < BottomMargin - Tolerance) clipped = true;
            if (viewport.y > 1f - TopMargin + Tolerance) clipped = true;
        }
    }
}
