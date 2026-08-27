using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.UI;
using TMPro;
using UnityEngine;

namespace Triggle.Visuals
{
    /// <summary>
    /// The "you scored" moment: an expanding shockwave ring on the claimed triangle, a floating score
    /// popup, and a small camera kick that grows with the size of the combo.
    /// </summary>
    /// <remarks>
    /// Claims are counted per band placement (reset on <see cref="GameEvents.OnBandPlaced"/>), so a move
    /// that closes several triangles at once escalates: each popup is larger and reads "x2", "x3" and so
    /// on, and the camera kick scales with it.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ClaimVfx : MonoBehaviour
    {
        [Header("Palette")]
        [SerializeField] private PlayerColorPalette palette;

        [Header("Shockwave Ring")]
        [SerializeField] private bool spawnRing = true;
        [SerializeField, Min(0.05f)] private float ringStartRadius = 0.12f;
        [SerializeField, Min(0.1f)] private float ringEndRadius = 1.15f;
        [SerializeField, Min(0.01f)] private float ringThickness = 0.10f;
        [SerializeField, Min(0.05f)] private float ringDuration = 0.5f;
        [SerializeField] private float ringHeight = 0.05f;
        [SerializeField, Range(8, 64)] private int ringSegments = 40;

        [Header("Score Popup")]
        [SerializeField] private bool spawnPopup = true;

        [Tooltip("Font for the floating '+1'. Falls back to the TMP default when empty.")]
        [SerializeField] private TMP_FontAsset popupFont;

        [SerializeField, Min(1f)] private float popupFontSize = 5.5f;
        [SerializeField, Min(0.1f)] private float popupDuration = 0.95f;
        [SerializeField, Min(0f)] private float popupRise = 1.5f;
        [SerializeField] private float popupStartHeight = 0.55f;

        [Tooltip("Extra size per extra triangle claimed in the same move.")]
        [SerializeField, Range(0f, 0.6f)] private float comboSizeStep = 0.22f;

        [Header("Camera Kick")]
        [SerializeField] private bool kickCamera = true;

        [Tooltip("Displacement of the first claim, in world units. Deliberately tiny.")]
        [SerializeField, Range(0f, 0.3f)] private float kickAmplitude = 0.045f;

        [SerializeField, Min(0.05f)] private float kickDuration = 0.22f;

        [SerializeField] private Transform vfxRoot;

        private readonly List<GameObject> _spawned = new List<GameObject>(32);
        private readonly List<Object> _owned = new List<Object>(16);
        private Mesh _ringMesh;
        private Camera _camera;
        private Coroutine _kickRoutine;
        private Vector3 _cameraBasePosition;
        private int _claimStreak;

        private void Awake()
        {
            if (palette == null) palette = PlayerColorPalette.Fallback;
            EnsureRoot();
        }

        private void OnEnable()
        {
            GameEvents.OnCellClaimed += HandleCellClaimed;
            GameEvents.OnBandPlaced += HandleBandPlaced;
            GameEvents.OnGameReset += HandleGameReset;
            GameEvents.OnCameraReframed += HandleCameraReframed;
        }

        private void OnDisable()
        {
            GameEvents.OnCellClaimed -= HandleCellClaimed;
            GameEvents.OnBandPlaced -= HandleBandPlaced;
            GameEvents.OnGameReset -= HandleGameReset;
            GameEvents.OnCameraReframed -= HandleCameraReframed;

            RestoreCamera();
        }

        private void OnDestroy()
        {
            ClearAll();

            for (int i = 0; i < _owned.Count; i++) DestroySafely(_owned[i]);
            _owned.Clear();

            if (_ringMesh != null) DestroySafely(_ringMesh);
        }

        private void EnsureRoot()
        {
            if (vfxRoot != null) return;

            var root = new GameObject("ClaimVfx");
            root.transform.SetParent(transform, false);
            vfxRoot = root.transform;
        }

        private void HandleBandPlaced(PlayerId player, BandPlacement band) => _claimStreak = 0;

        /// <summary>
        /// Re-reads the camera's resting position after the rig has moved it.
        /// </summary>
        /// <remarks>
        /// The base position is cached so repeated kicks cannot accumulate drift, but that cache is
        /// taken once. When the player changes the board size the rig reframes the camera, and without
        /// this the next kick would settle the camera back to where it sat for the old board - snapping
        /// the view off-centre on the first claim of the round.
        /// </remarks>
        private void HandleCameraReframed()
        {
            if (_camera == null) _camera = Camera.main;
            if (_camera != null) _cameraBasePosition = _camera.transform.position;
        }

        private void HandleGameReset()
        {
            _claimStreak = 0;
            ClearAll();
            RestoreCamera();
        }

        private void HandleCellClaimed(TriangleCell cell)
        {
            if (cell == null || cell.Owner == PlayerId.None) return;

            EnsureRoot();

            _claimStreak++;
            Color color = palette.GetColor(cell.Owner);

            if (spawnRing) SpawnRing(cell.CenterPosition, color);
            if (spawnPopup) SpawnPopup(cell.CenterPosition, color, _claimStreak);
            if (kickCamera) Kick(_claimStreak);
        }

        // ------------------------------------------------------------------ ring

        private void SpawnRing(Vector3 centre, Color color)
        {
            var go = new GameObject("Shockwave");
            go.transform.SetParent(vfxRoot, false);
            go.transform.position = new Vector3(centre.x, ringHeight, centre.z);

            go.AddComponent<MeshFilter>().sharedMesh = GetRingMesh();

            Material material = MaterialUtility.CreateDefaultTransparentMaterial();
            material.name = "Shockwave_Mat";
            MaterialUtility.SetColor(material, color);

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _spawned.Add(go);
            StartCoroutine(RingRoutine(go, material, color));
        }

        /// <summary>Expands the ring while fading it out, then cleans up its GameObject and material.</summary>
        private IEnumerator RingRoutine(GameObject go, Material material, Color color)
        {
            Transform target = go.transform;
            float elapsed = 0f;

            while (elapsed < ringDuration)
            {
                elapsed += Time.deltaTime;
                if (target == null || material == null) break;

                float t = Mathf.Clamp01(elapsed / ringDuration);
                float eased = UITween.EaseOut(t);

                float radius = Mathf.Lerp(ringStartRadius, ringEndRadius, eased);
                target.localScale = Vector3.one * radius;

                // Fade out on a curve so it stays bright at the start and vanishes cleanly.
                Color fading = color;
                fading.a = color.a * (1f - t) * (1f - t);
                MaterialUtility.SetColor(material, fading);

                yield return null;
            }

            _spawned.Remove(go);
            DestroySafely(go);
            DestroySafely(material);
        }

        /// <summary>
        /// Unit-radius annulus in the XZ plane, shared by every shockwave and scaled per instance.
        /// </summary>
        private Mesh GetRingMesh()
        {
            if (_ringMesh != null) return _ringMesh;

            float inner = Mathf.Clamp01(1f - ringThickness / Mathf.Max(ringEndRadius, 0.001f));
            var vertices = new List<Vector3>(ringSegments * 2);
            var triangles = new List<int>(ringSegments * 6);

            for (int i = 0; i < ringSegments; i++)
            {
                float angle = i / (float)ringSegments * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                vertices.Add(dir * inner);
                vertices.Add(dir);
            }

            for (int i = 0; i < ringSegments; i++)
            {
                int a = i * 2;
                int b = i * 2 + 1;
                int next = (i + 1) % ringSegments;
                int c = next * 2;
                int d = next * 2 + 1;

                // Wound so the ring faces up (+Y).
                triangles.Add(a); triangles.Add(d); triangles.Add(b);
                triangles.Add(a); triangles.Add(c); triangles.Add(d);
            }

            _ringMesh = new Mesh { name = "ShockwaveRing", hideFlags = HideFlags.DontSave };
            _ringMesh.SetVertices(vertices);
            _ringMesh.SetTriangles(triangles, 0);
            _ringMesh.RecalculateNormals();
            _ringMesh.RecalculateBounds();

            return _ringMesh;
        }

        // ------------------------------------------------------------------ popup

        private void SpawnPopup(Vector3 centre, Color color, int streak)
        {
            var go = new GameObject("ScorePopup");
            go.transform.SetParent(vfxRoot, false);
            go.transform.position = new Vector3(centre.x, popupStartHeight, centre.z);

            var text = go.AddComponent<TextMeshPro>();
            if (popupFont != null) text.font = popupFont;
            else if (TMP_Settings.defaultFontAsset != null) text.font = TMP_Settings.defaultFontAsset;

            text.text = streak > 1 ? $"+1  x{streak}" : "+1";
            text.fontSize = popupFontSize * (1f + comboSizeStep * (streak - 1));
            text.color = color;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.rectTransform.sizeDelta = new Vector2(8f, 2f);

            // Text rendered in world space needs its own transparent queue slot or it z-fights the fills.
            text.fontMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 10;
            _owned.Add(text.fontMaterial);

            _spawned.Add(go);
            StartCoroutine(PopupRoutine(go, text));
        }

        /// <summary>Rises, scales in, then fades. Billboards to the camera every frame.</summary>
        private IEnumerator PopupRoutine(GameObject go, TMP_Text text)
        {
            Transform target = go.transform;
            Vector3 start = target.position;
            Camera cam = ResolveCamera();
            Color baseColor = text.color;
            float elapsed = 0f;

            while (elapsed < popupDuration)
            {
                elapsed += Time.deltaTime;
                if (target == null || text == null) break;

                float t = Mathf.Clamp01(elapsed / popupDuration);

                target.position = start + Vector3.up * (popupRise * UITween.EaseOut(t));

                // Quick pop in, slow fade out.
                float scale = UITween.EaseOutBack(Mathf.Clamp01(t / 0.25f), 2.2f);
                target.localScale = Vector3.one * scale;

                Color fading = baseColor;
                fading.a = baseColor.a * (1f - Mathf.Clamp01((t - 0.45f) / 0.55f));
                text.color = fading;

                if (cam != null)
                    target.rotation = Quaternion.LookRotation(target.position - cam.transform.position, Vector3.up);

                yield return null;
            }

            _spawned.Remove(go);
            DestroySafely(go);
        }

        // ------------------------------------------------------------------ camera kick

        private void Kick(int streak)
        {
            Camera cam = ResolveCamera();
            if (cam == null) return;

            float amplitude = kickAmplitude * Mathf.Min(streak, 4);

            if (_kickRoutine != null) StopCoroutine(_kickRoutine);
            _kickRoutine = StartCoroutine(KickRoutine(cam.transform, amplitude));
        }

        private IEnumerator KickRoutine(Transform cam, float amplitude)
        {
            float elapsed = 0f;

            while (elapsed < kickDuration)
            {
                elapsed += Time.deltaTime;
                if (cam == null) yield break;

                float t = Mathf.Clamp01(elapsed / kickDuration);

                // A decaying sine: two quick bumps that settle exactly back to the base position.
                float decay = 1f - t;
                float offset = Mathf.Sin(t * Mathf.PI * 2f) * amplitude * decay * decay;

                cam.position = _cameraBasePosition + cam.up * offset;
                yield return null;
            }

            if (cam != null) cam.position = _cameraBasePosition;
            _kickRoutine = null;
        }

        /// <summary>
        /// Caches the camera and its resting position. The base position is captured once so repeated
        /// kicks can never accumulate drift.
        /// </summary>
        private Camera ResolveCamera()
        {
            if (_camera != null) return _camera;

            _camera = Camera.main;
            if (_camera != null) _cameraBasePosition = _camera.transform.position;

            return _camera;
        }

        private void RestoreCamera()
        {
            if (_kickRoutine != null)
            {
                StopCoroutine(_kickRoutine);
                _kickRoutine = null;
            }

            if (_camera != null) _camera.transform.position = _cameraBasePosition;
        }

        /// <summary>Destroys every live effect.</summary>
        public void ClearAll()
        {
            for (int i = 0; i < _spawned.Count; i++) DestroySafely(_spawned[i]);
            _spawned.Clear();
        }

        private static void DestroySafely(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
