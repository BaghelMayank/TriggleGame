using System.Collections.Generic;
using System.Text;
using TMPro;
using Triggle.Core;
using Triggle.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Drives the generated UI through every landscape shape the game ships on and checks two things:
    /// nothing is clipped, and every button is actually clickable.
    /// </summary>
    /// <remarks>
    /// The second check exists because the first one is not enough on its own. An earlier version of
    /// this audit measured only rectangle containment, passed with zero violations, and shipped a build
    /// where a full-screen scrim was drawn after the content and swallowed every tap - the buttons were
    /// perfectly positioned and completely dead. Containment says where a control is; it says nothing
    /// about whether anything is sitting on top of it.
    /// <para>
    /// The editor cannot be resized to a phone from a script and <see cref="Screen.safeArea"/> is
    /// read-only, so the components read <see cref="ScreenMetrics"/> instead - which this overrides. The
    /// canvas is put into world space for the duration, the one render mode where its RectTransform is
    /// not driven by the real backbuffer, and sized to whatever Unity's scaler maths says it would be on
    /// the device under test. Children resolve through their normal anchors from there, so what is
    /// measured is the real layout rather than a model of it.
    /// </para>
    /// </remarks>
    public static class TriggleUiVerification
    {
        private const string ScenePath = "Assets/Scenes/Triggle.unity";

        /// <summary>Corners may sit this far outside the safe rect before it counts as clipped.</summary>
        private const float ToleranceUnits = 0.5f;

        /// <summary>
        /// How far two controls may intersect before it counts. Small, because overlap is measured
        /// against rendered glyph bounds rather than text rects.
        /// </summary>
        private const float OverlapTolerance = 2f;

        /// <summary>
        /// Alpha at or above which an image is treated as hiding whatever is behind it.
        /// </summary>
        /// <remarks>
        /// The panels are built from translucent fills and glows that are meant to be seen through, so a
        /// low bar would flag the entire neon style. This is the level at which text underneath stops
        /// being legible.
        /// </remarks>
        private const float OpaqueAlpha = 0.6f;

        private readonly struct Device
        {
            public readonly string Name;
            public readonly Vector2Int Size;
            public readonly Rect Safe;

            public Device(string name, int width, int height, float left, float bottom,
                          float right, float top)
            {
                Name = name;
                Size = new Vector2Int(width, height);
                Safe = new Rect(left, bottom, width - left - right, height - bottom - top);
            }
        }

        /// <summary>
        /// Landscape only, which is what the build is locked to. Insets are pixels: left, bottom, right,
        /// top - a phone held sideways puts its cutout on one side and its gesture bar along the bottom.
        /// </summary>
        private static readonly Device[] Devices =
        {
            new Device("1280x1024 5:4", 1280, 1024, 0, 0, 0, 0),
            new Device("1440x1080 4:3", 1440, 1080, 0, 0, 0, 0),
            new Device("1920x1080 16:9", 1920, 1080, 0, 0, 0, 0),
            new Device("2560x1080 21:9", 2560, 1080, 0, 0, 0, 0),
            new Device("2340x1080 phone", 2340, 1080, 100, 20, 0, 0),
            new Device("2532x1170 iPhone 13", 2532, 1170, 141, 21, 141, 0),
            new Device("2400x1080 notch right", 2400, 1080, 0, 24, 110, 0),
            new Device("3200x1440 tablet", 3200, 1440, 0, 0, 0, 0)
        };

        [MenuItem("Tools/Triggle/Verify UI Layout", false, 42)]
        public static void Run()
        {
            Canvas canvas = FindCanvas();
            if (canvas == null)
            {
                Debug.LogError("[Triggle] No UI canvas found. Build the play scene first " +
                               "(Tools > Triggle > Build Play Scene).");
                return;
            }

            var scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null)
            {
                Debug.LogError("[Triggle] The canvas has no CanvasScaler.");
                return;
            }

            var report = new StringBuilder();
            report.AppendLine("[Triggle] UI layout verification - landscape shapes.");
            report.AppendLine();
            report.AppendLine("  Device                  Canvas units   Controls   Clip margin   Blocked");
            report.AppendLine("  ----------------------------------------------------------------------");

            var clipped = new List<string>();
            var blocked = new List<string>();

            RectTransform root = canvas.GetComponent<RectTransform>();
            RenderMode originalMode = canvas.renderMode;
            Vector2 originalSize = root.sizeDelta;
            Vector3 originalPosition = root.position;
            Quaternion originalRotation = root.rotation;
            Vector3 originalScale = root.localScale;
            bool scalerWasEnabled = scaler.enabled;

            try
            {
                canvas.renderMode = RenderMode.WorldSpace;
                scaler.enabled = false;

                root.position = Vector3.zero;
                root.rotation = Quaternion.identity;
                root.localScale = Vector3.one;
                root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);

                for (int d = 0; d < Devices.Length; d++)
                {
                    Device device = Devices[d];

                    ScreenMetrics.SizeOverride = device.Size;
                    ScreenMetrics.SafeAreaOverride = device.Safe;

                    // Match height: the shipped setting, and the reason the canvas is always 1080 tall.
                    Vector2 canvasSize = CanvasSizeFor(device.Size, scaler.referenceResolution,
                                                        scaler.matchWidthOrHeight);
                    root.sizeDelta = canvasSize;

                    int controls = 0;
                    int blockedBefore = blocked.Count;
                    float tightest = float.PositiveInfinity;

                    foreach (RectTransform panel in Panels(root))
                        Audit(panel, root, device.Name, ref controls, ref tightest, clipped, blocked);

                    report.AppendLine(
                        $"  {device.Name,-22} {canvasSize.x,6:0}x{canvasSize.y,-6:0} {controls,10} " +
                        $"{tightest,13:0.0} {blocked.Count - blockedBefore,9}");
                }
            }
            finally
            {
                ScreenMetrics.ClearOverrides();

                canvas.renderMode = originalMode;
                scaler.enabled = scalerWasEnabled;

                root.sizeDelta = originalSize;
                root.position = originalPosition;
                root.rotation = originalRotation;
                root.localScale = originalScale;
            }

            report.AppendLine();
            report.AppendLine("  Clip margin: smallest distance from any control to its safe-area edge,");
            report.AppendLine("  in canvas units. Positive means nothing is cut.");
            report.AppendLine("  Blocked: interactive controls with something drawn over their centre.");
            report.AppendLine();
            report.AppendLine($"  Clipped controls: {clipped.Count}");
            report.AppendLine($"  Blocked controls: {blocked.Count}");

            Append(report, clipped);
            Append(report, blocked);

            if (clipped.Count > 0 || blocked.Count > 0) Debug.LogError(report.ToString());
            else Debug.Log(report.ToString());
        }

        private static void Append(StringBuilder report, List<string> entries)
        {
            for (int i = 0; i < entries.Count && i < 25; i++) report.AppendLine($"    {entries[i]}");
            if (entries.Count > 25) report.AppendLine($"    ... and {entries.Count - 25} more");
        }

        /// <summary>Canvas size in UI units for a screen, matching Unity's own scaler maths.</summary>
        private static Vector2 CanvasSizeFor(Vector2Int screen, Vector2 reference, float match)
        {
            if (screen.x <= 0 || screen.y <= 0 || reference.x <= 0f || reference.y <= 0f) return Vector2.zero;

            // Unity interpolates the two scale factors in log space, so a match of 0.5 is the geometric
            // mean rather than the arithmetic one.
            float logWidth = Mathf.Log(screen.x / reference.x, 2f);
            float logHeight = Mathf.Log(screen.y / reference.y, 2f);
            float scale = Mathf.Pow(2f, Mathf.Lerp(logWidth, logHeight, match));

            return scale <= 0f ? Vector2.zero : new Vector2(screen.x / scale, screen.y / scale);
        }

        private static Canvas FindCanvas()
        {
            Canvas canvas = Object.FindObjectOfType<Canvas>();
            if (canvas != null) return canvas;

            if (!System.IO.File.Exists(ScenePath)) return null;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return null;

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            return Object.FindObjectOfType<Canvas>();
        }

        /// <summary>The panel roots: every direct child of the canvas that owns a CanvasGroup.</summary>
        private static IEnumerable<RectTransform> Panels(RectTransform root)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i) as RectTransform;
                if (child != null && child.GetComponent<CanvasGroup>() != null) yield return child;
            }
        }

        /// <summary>
        /// Measures one panel in two passes, because the two questions need opposite setups.
        /// </summary>
        /// <remarks>
        /// <b>Clipping</b> wants everything switched on at once: seat rows 3 and 4, the difficulty
        /// stepper and the pause confirmation are exactly the things most likely to overflow, and a pass
        /// that only measured the default two-player lobby would miss them. Showing more than the player
        /// ever sees is harmless here - containment is per-control.
        /// <para>
        /// <b>Reachability</b> wants the authored visibility, because "what is on top of this button"
        /// only means anything if both things are really on screen together. Forcing every child on puts
        /// the Settings panel's Audio and Board tabs up simultaneously, and the theme chips then sit over
        /// the volume sliders - a collision that cannot happen in the running game. Only the panel root
        /// is opened for this pass; everything inside keeps the state it was authored with.
        /// </para>
        /// </remarks>
        private static void Audit(RectTransform panel, RectTransform root, string device,
                                   ref int controls, ref float tightest,
                                   List<string> clipped, List<string> blocked)
        {
            AuditClipping(panel, root, device, ref controls, ref tightest, clipped);
            AuditReachability(panel, root, device, blocked);
            AuditOverlap(panel, root, device, blocked);
            AuditCoveredText(panel, root, device, blocked);
        }

        /// <summary>
        /// Flags two controls that are both on screen and sitting on top of each other.
        /// </summary>
        /// <remarks>
        /// Containment and reachability between them still miss a whole class of fault: two things that
        /// are each fully inside the safe area, neither covering the other's centre, but visibly
        /// colliding. Adding a sixth button to the main menu pushed the column into the tagline exactly
        /// that way, and both other checks passed.
        /// <para>
        /// Only pairs where neither is an ancestor of the other are compared - a button's own label sits
        /// on top of it by design - and only text that would actually render, since an empty label's rect
        /// overlaps plenty without ever being seen.
        /// </para>
        /// </remarks>
        private static void AuditOverlap(RectTransform panel, RectTransform root, string device,
                                          List<string> problems)
        {
            bool wasActive = panel.gameObject.activeSelf;
            if (!wasActive) panel.gameObject.SetActive(true);

            try
            {
                var safeArea = panel.GetComponentInChildren<CanvasSafeArea>(true);
                if (safeArea == null) return;

                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                Canvas.ForceUpdateCanvases();

                var boxes = new List<(string Path, Transform Node, Rect Bounds)>();

                foreach (Component control in VisibleControls(safeArea.GetComponent<RectTransform>()))
                {
                    var rect = control.transform as RectTransform;
                    if (rect == null) continue;

                    boxes.Add((Path(control.transform, panel), control.transform, BoundsOf(control, rect, root)));
                }

                for (int i = 0; i < boxes.Count; i++)
                {
                    for (int j = i + 1; j < boxes.Count; j++)
                    {
                        if (boxes[i].Node.IsChildOf(boxes[j].Node)) continue;
                        if (boxes[j].Node.IsChildOf(boxes[i].Node)) continue;
                        if (IsDeliberateStack(boxes[i], boxes[j])) continue;

                        if (!Overlaps(boxes[i].Bounds, boxes[j].Bounds)) continue;

                        problems.Add($"{device}: {boxes[i].Path} overlaps {boxes[j].Path}");
                    }
                }
            }
            finally
            {
                if (!wasActive) panel.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// What a control actually covers on screen: its rect, or for text the glyphs inside it.
        /// </summary>
        /// <remarks>
        /// A label's rect is routinely far wider than its words - a left-aligned score sits in a
        /// 300-unit box holding the single character "0" - so comparing rects reports collisions between
        /// things that are nowhere near each other. Measuring the rendered mesh instead is what makes an
        /// overlap report worth reading.
        /// </remarks>
        private static Rect BoundsOf(Component control, RectTransform rect, RectTransform space)
        {
            if (control is not TMP_Text label) return LocalRect(rect, space);

            label.ForceMeshUpdate();
            Bounds bounds = label.textBounds;

            if (bounds.size.x <= 0f || bounds.size.y <= 0f) return LocalRect(rect, space);

            // textBounds is in the label's own local space; lift its corners into the shared space.
            Vector3 min = rect.TransformPoint(bounds.min);
            Vector3 max = rect.TransformPoint(bounds.max);

            Vector3 a = space.InverseTransformPoint(min);
            Vector3 b = space.InverseTransformPoint(max);

            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                                   Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        /// <summary>
        /// Flags a label with a solid image painted over it.
        /// </summary>
        /// <remarks>
        /// The gap the other passes leave. Reachability only considers Selectables, and overlap only
        /// compares controls with each other - so an <see cref="Image"/> drawn after a label and sitting
        /// on top of it is invisible to both, which is precisely the "the text is set but I cannot see
        /// it" failure. uGUI draws in hierarchy order, so anything later that covers the glyphs and is
        /// solid enough to read through hides them.
        /// </remarks>
        private static void AuditCoveredText(RectTransform panel, RectTransform root, string device,
                                              List<string> problems)
        {
            bool wasActive = panel.gameObject.activeSelf;
            if (!wasActive) panel.gameObject.SetActive(true);

            try
            {
                var safeArea = panel.GetComponentInChildren<CanvasSafeArea>(true);
                if (safeArea == null) return;

                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                Canvas.ForceUpdateCanvases();

                List<Graphic> order = DrawOrder(panel);

                for (int i = 0; i < order.Count; i++)
                {
                    if (order[i] is not TMP_Text label) continue;
                    if (string.IsNullOrWhiteSpace(label.text) || label.color.a < 0.05f) continue;

                    Rect glyphs = BoundsOf(label, label.rectTransform, root);

                    for (int j = i + 1; j < order.Count; j++)
                    {
                        if (order[j] is not Image cover) continue;
                        if (cover.color.a < OpaqueAlpha || IsHollow(cover.sprite)) continue;
                        if (cover.transform.IsChildOf(label.transform)) continue;
                        if (label.transform.IsChildOf(cover.transform)) continue;

                        Rect over = LocalRect(cover.rectTransform, root);
                        if (!Contains(over, glyphs)) continue;

                        problems.Add($"{device}: {Path(label.transform, panel)} is covered by " +
                                     $"{Path(cover.transform, panel)}");
                        break;
                    }
                }
            }
            finally
            {
                if (!wasActive) panel.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// True for the generated sprites whose middle is empty - outlines and glows.
        /// </summary>
        /// <remarks>
        /// Their rects cover whatever they frame, but their pixels are only the border, so treating them
        /// as covers reports every label inside a neon control. Recognised by name because they all come
        /// from one generator; sampling the texture instead would need it marked readable.
        /// </remarks>
        private static bool IsHollow(Sprite sprite)
        {
            if (sprite == null) return false;   // no sprite at all draws a solid quad

            string name = sprite.name;
            return name.EndsWith("Outline") || name.EndsWith("Glow");
        }

        /// <summary>True when <paramref name="inner"/> is entirely inside <paramref name="outer"/>.</summary>
        /// <remarks>
        /// Full containment, not intersection: a solid panel clipping the corner of a label is usually
        /// the design, whereas one swallowing it whole is the fault being looked for.
        /// </remarks>
        private static bool Contains(Rect outer, Rect inner) =>
            outer.xMin <= inner.xMin + OverlapTolerance && outer.xMax >= inner.xMax - OverlapTolerance &&
            outer.yMin <= inner.yMin + OverlapTolerance && outer.yMax >= inner.yMax - OverlapTolerance;

        /// <summary>
        /// True when two labels are stacked on purpose rather than by accident.
        /// </summary>
        /// <remarks>
        /// Text laid over text is sometimes the design: the title is three offset copies of "TRIGGLE"
        /// making the neon glow, and every input field puts its placeholder exactly where the typed text
        /// will go. Both are authored as <b>siblings</b> - one parent whose whole job is to hold the
        /// stack - whereas an accidental collision is between labels from different branches of the
        /// panel. That distinction is what separates the two without needing a list of exceptions.
        /// </remarks>
        private static bool IsDeliberateStack(
            (string Path, Transform Node, Rect Bounds) a,
            (string Path, Transform Node, Rect Bounds) b)
        {
            bool bothText = a.Node.GetComponent<TMP_Text>() != null &&
                            b.Node.GetComponent<TMP_Text>() != null;

            return bothText && a.Node.parent == b.Node.parent;
        }

        private static bool Overlaps(Rect a, Rect b) =>
            a.xMin < b.xMax - OverlapTolerance && b.xMin < a.xMax - OverlapTolerance &&
            a.yMin < b.yMax - OverlapTolerance && b.yMin < a.yMax - OverlapTolerance;

        /// <summary>Active controls that would actually draw something.</summary>
        private static IEnumerable<Component> VisibleControls(RectTransform safeArea)
        {
            Selectable[] selectables = safeArea.GetComponentsInChildren<Selectable>(false);
            for (int i = 0; i < selectables.Length; i++) yield return selectables[i];

            TMP_Text[] labels = safeArea.GetComponentsInChildren<TMP_Text>(false);
            for (int i = 0; i < labels.Length; i++)
                if (!string.IsNullOrWhiteSpace(labels[i].text)) yield return labels[i];
        }

        private static void AuditClipping(RectTransform panel, RectTransform root, string device,
                                           ref int controls, ref float tightest, List<string> clipped)
        {
            var reactivate = new List<GameObject>();

            if (!panel.gameObject.activeSelf)
            {
                panel.gameObject.SetActive(true);
                reactivate.Add(panel.gameObject);
            }

            Transform[] all = panel.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.activeSelf) continue;

                all[i].gameObject.SetActive(true);
                reactivate.Add(all[i].gameObject);
            }

            // CanvasGroup alpha and raycast flags are deliberately left alone: neither affects a rect's
            // geometry, and this pass only measures geometry.
            try
            {
                var safeArea = panel.GetComponentInChildren<CanvasSafeArea>(true);
                if (safeArea == null) return;

                safeArea.Apply();
                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                Canvas.ForceUpdateCanvases();

                var safeRect = safeArea.GetComponent<RectTransform>();
                Rect bounds = LocalRect(safeRect, root);

                foreach (Component control in Controls(safeRect))
                {
                    var rect = control.transform as RectTransform;
                    if (rect == null) continue;

                    Rect local = LocalRect(rect, root);

                    float margin = Mathf.Min(
                        Mathf.Min(local.xMin - bounds.xMin, bounds.xMax - local.xMax),
                        Mathf.Min(local.yMin - bounds.yMin, bounds.yMax - local.yMax));

                    controls++;
                    tightest = Mathf.Min(tightest, margin);

                    if (margin < -ToleranceUnits)
                        clipped.Add($"{device}: {Path(control.transform, panel)} " +
                                    $"clipped by {-margin:0.0} units");
                }
            }
            finally
            {
                for (int i = reactivate.Count - 1; i >= 0; i--) reactivate[i].SetActive(false);
            }
        }

        /// <summary>
        /// Opens the panel exactly as the game would - root only - and checks nothing covers a control.
        /// </summary>
        private static void AuditReachability(RectTransform panel, RectTransform root, string device,
                                               List<string> blocked)
        {
            bool wasActive = panel.gameObject.activeSelf;
            var group = panel.GetComponent<CanvasGroup>();

            float alpha = group != null ? group.alpha : 1f;
            bool blocks = group != null && group.blocksRaycasts;
            bool interactable = group != null && group.interactable;

            if (!wasActive) panel.gameObject.SetActive(true);

            if (group != null)
            {
                group.alpha = 1f;
                group.blocksRaycasts = true;
                group.interactable = true;
            }

            try
            {
                var safeArea = panel.GetComponentInChildren<CanvasSafeArea>(true);
                if (safeArea == null) return;

                safeArea.Apply();
                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);
                Canvas.ForceUpdateCanvases();

                // Draw order is depth-first hierarchy order, and hit-testing walks it in reverse.
                List<Graphic> order = DrawOrder(panel);

                Selectable[] selectables =
                    safeArea.GetComponentsInChildren<Selectable>(false);

                for (int i = 0; i < selectables.Length; i++)
                    CheckReachable(selectables[i], order, root, device, panel, blocked);
            }
            finally
            {
                if (group != null)
                {
                    group.alpha = alpha;
                    group.blocksRaycasts = blocks;
                    group.interactable = interactable;
                }

                if (!wasActive) panel.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Flags a control that something else is drawn on top of, at its own centre.
        /// </summary>
        /// <remarks>
        /// uGUI hit-tests the draw order in reverse, so the last raycast target covering a point wins.
        /// Anything later in the hierarchy than the button, overlapping its centre, and not part of the
        /// button itself, takes the click.
        /// </remarks>
        private static void CheckReachable(Selectable selectable, List<Graphic> order, RectTransform root,
                                            string device, RectTransform panel, List<string> blocked)
        {
            var rect = selectable.transform as RectTransform;
            if (rect == null) return;

            Rect local = LocalRect(rect, root);
            Vector2 centre = local.center;

            int selfIndex = -1;
            for (int i = 0; i < order.Count; i++)
            {
                if (!order[i].transform.IsChildOf(selectable.transform)) continue;

                selfIndex = Mathf.Max(selfIndex, i);
            }

            if (selfIndex < 0) return;   // no graphic of its own: nothing to be covered

            for (int i = selfIndex + 1; i < order.Count; i++)
            {
                Graphic other = order[i];
                if (!other.raycastTarget) continue;
                if (other.transform.IsChildOf(selectable.transform)) continue;

                // A CanvasGroup with raycasts off lets clicks through regardless of what it draws.
                var group = other.GetComponentInParent<CanvasGroup>();
                if (group != null && !group.blocksRaycasts) continue;

                if (!LocalRect(other.rectTransform, root).Contains(centre)) continue;

                blocked.Add($"{device}: {Path(selectable.transform, panel)} " +
                            $"blocked by {Path(other.transform, panel)}");
                return;
            }
        }

        /// <summary>Every graphic under a panel, in uGUI draw order (depth-first, hierarchy order).</summary>
        private static List<Graphic> DrawOrder(RectTransform panel)
        {
            var order = new List<Graphic>(64);
            Collect(panel, order);
            return order;
        }

        private static void Collect(Transform node, List<Graphic> order)
        {
            // Inactive branches draw nothing and hit-test nothing, so they cannot block anything.
            if (!node.gameObject.activeSelf) return;

            var graphic = node.GetComponent<Graphic>();
            if (graphic != null) order.Add(graphic);

            for (int i = 0; i < node.childCount; i++) Collect(node.GetChild(i), order);
        }

        /// <summary>Controls that must never be clipped: anything interactive, and anything with text.</summary>
        private static IEnumerable<Component> Controls(RectTransform safeArea)
        {
            Selectable[] selectables = safeArea.GetComponentsInChildren<Selectable>(true);
            for (int i = 0; i < selectables.Length; i++) yield return selectables[i];

            TMP_Text[] labels = safeArea.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++) yield return labels[i];
        }

        /// <summary>A rect's world corners expressed in <paramref name="space"/>'s local coordinates.</summary>
        private static Rect LocalRect(RectTransform rect, RectTransform space)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);

            Vector3 first = space.InverseTransformPoint(corners[0]);
            float xMin = first.x, xMax = first.x, yMin = first.y, yMax = first.y;

            for (int i = 1; i < 4; i++)
            {
                Vector3 p = space.InverseTransformPoint(corners[i]);

                if (p.x < xMin) xMin = p.x;
                if (p.x > xMax) xMax = p.x;
                if (p.y < yMin) yMin = p.y;
                if (p.y > yMax) yMax = p.y;
            }

            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private static string Path(Transform node, Transform stopAt)
        {
            var builder = new StringBuilder(node.name);

            for (Transform t = node.parent; t != null && t != stopAt; t = t.parent)
                builder.Insert(0, t.name + "/");

            return $"{stopAt.name}/{builder}";
        }
    }
}
