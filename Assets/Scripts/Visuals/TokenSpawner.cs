using System.Collections;
using System.Collections.Generic;
using Triggle.Core;
using Triggle.UI;
using UnityEngine;

namespace Triggle.Visuals
{
    /// <summary>
    /// Instantiates a coloured 3D token for every claimed triangle and drops it into place with an
    /// eased bounce plus an optional particle burst.
    /// </summary>
    /// <remarks>
    /// With no prefab assigned a flat-shaded cone/pyramid mesh is generated once and shared by every
    /// token, so a fully playable board costs a single mesh allocation.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class TokenSpawner : MonoBehaviour
    {
        [Header("Palette")]
        [SerializeField] private PlayerColorPalette palette;

        [Header("Token")]
        [Tooltip("Optional token prefab. Leave empty to generate a pyramid mesh at runtime.")]
        [SerializeField] private GameObject tokenPrefab;

        [Tooltip("Number of sides of the generated pyramid. 3 mirrors the triangular cell.")]
        [SerializeField, Range(3, 16)] private int generatedSides = 3;

        [SerializeField, Min(0.01f)] private float tokenRadius = 0.26f;
        [SerializeField, Min(0.01f)] private float tokenHeight = 0.45f;

        [Tooltip("Vertical offset of the token's resting position above the cell centre.")]
        [SerializeField] private float restingHeight = 0.02f;

        [Header("Drop Animation")]
        [Tooltip("Height above the cell centre the token spawns at.")]
        [SerializeField, Min(0f)] private float dropHeight = 3f;

        [SerializeField, Min(0.01f)] private float dropDuration = 0.42f;

        [Tooltip("Number of decaying bounces after the first impact. 0 = a clean landing.")]
        [SerializeField, Range(0, 4)] private int bounceCount = 2;

        [Tooltip("Fraction of the remaining height regained by each bounce.")]
        [SerializeField, Range(0f, 0.8f)] private float bounceDamping = 0.32f;

        [Tooltip("Squash applied at the moment of impact, released as the token settles.")]
        [SerializeField, Range(0f, 0.6f)] private float impactSquash = 0.28f;

        [Tooltip("Full spins performed during the drop.")]
        [SerializeField, Range(0f, 3f)] private float spinTurns = 1f;

        [Header("Impact Burst")]
        [Tooltip("Optional particle prefab. When empty a small procedural burst is generated.")]
        [SerializeField] private ParticleSystem burstPrefab;

        [SerializeField] private bool spawnBurst = true;
        [SerializeField, Range(2, 60)] private int burstParticleCount = 18;

        [SerializeField] private Transform tokenRoot;

        private readonly List<GameObject> _spawned = new List<GameObject>(64);
        private readonly List<Material> _ownedMaterials = new List<Material>(16);
        private Mesh _generatedMesh;

        private void Awake()
        {
            if (palette == null) palette = PlayerColorPalette.Fallback;
            EnsureRoot();
        }

        private void OnEnable()
        {
            GameEvents.OnCellClaimed += HandleCellClaimed;
            GameEvents.OnGameReset += ClearAll;
        }

        private void OnDisable()
        {
            GameEvents.OnCellClaimed -= HandleCellClaimed;
            GameEvents.OnGameReset -= ClearAll;
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _ownedMaterials.Count; i++) DestroySafely(_ownedMaterials[i]);
            _ownedMaterials.Clear();

            if (_generatedMesh != null) DestroySafely(_generatedMesh);
        }

        private void EnsureRoot()
        {
            if (tokenRoot != null) return;

            var root = new GameObject("Tokens");
            root.transform.SetParent(transform, false);
            tokenRoot = root.transform;
        }

        private void HandleCellClaimed(TriangleCell cell)
        {
            if (cell == null || cell.Owner == PlayerId.None) return;

            EnsureRoot();

            Vector3 resting = cell.CenterPosition + Vector3.up * restingHeight;
            Vector3 start = resting + Vector3.up * dropHeight;
            Color color = palette.GetColor(cell.Owner);

            GameObject token = CreateToken(cell, color);
            token.transform.SetParent(tokenRoot, false);
            token.transform.position = start;

            // Point-down cells get their token rotated 180 degrees so it visually nests in the triangle.
            float baseYaw = cell.Orientation == CellOrientation.Up ? 0f : 180f;
            token.transform.rotation = Quaternion.Euler(0f, baseYaw, 0f);

            _spawned.Add(token);

            if (Application.isPlaying)
                StartCoroutine(DropRoutine(token.transform, start, resting, baseYaw, color));
            else
                token.transform.position = resting;
        }

        private GameObject CreateToken(TriangleCell cell, Color color)
        {
            GameObject token;

            if (tokenPrefab != null)
            {
                token = Instantiate(tokenPrefab);
            }
            else
            {
                token = new GameObject("Token");
                var filter = token.AddComponent<MeshFilter>();
                var renderer = token.AddComponent<MeshRenderer>();

                filter.sharedMesh = GetGeneratedMesh();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.sharedMaterial = palette.GetTokenMaterial(cell.Owner);
            }

            token.name = $"Token_Cell{cell.Id}_{cell.Owner}";
            TintToken(token, cell.Owner, color);
            return token;
        }

        /// <summary>Applies the seat colour to every renderer on the token, instancing as needed.</summary>
        private void TintToken(GameObject token, PlayerId owner, Color color)
        {
            Renderer[] renderers = token.GetComponentsInChildren<Renderer>();

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];

                if (tokenPrefab == null)
                {
                    // Palette materials are already tinted and shared between tokens of one seat.
                    renderer.sharedMaterial = palette.GetTokenMaterial(owner);
                    continue;
                }

                Material instance = MaterialUtility.Instantiate(renderer.sharedMaterial, $"Token_{owner}_Mat");
                MaterialUtility.SetColor(instance, color);
                renderer.material = instance;
                _ownedMaterials.Add(instance);
            }
        }

        /// <summary>
        /// Drops the token with a decaying bounce. The vertical curve is evaluated analytically rather
        /// than integrated, so the landing time is exact regardless of frame rate.
        /// </summary>
        private IEnumerator DropRoutine(Transform token, Vector3 start, Vector3 resting, float baseYaw, Color color)
        {
            float fallDistance = Mathf.Max(0.0001f, start.y - resting.y);
            float elapsed = 0f;
            bool burstFired = false;

            // The first segment is the fall itself; each bounce is shorter than the one before.
            float totalDuration = dropDuration;
            for (int b = 0; b < bounceCount; b++)
                totalDuration += dropDuration * 0.5f * Mathf.Pow(bounceDamping, b);

            Vector3 baseScale = token.localScale;

            while (elapsed < totalDuration)
            {
                elapsed += Time.deltaTime;

                float height = EvaluateBounceHeight(elapsed, fallDistance);
                token.position = new Vector3(resting.x, resting.y + height, resting.z);

                float fallProgress = Mathf.Clamp01(elapsed / dropDuration);
                token.rotation = Quaternion.Euler(0f, baseYaw + 360f * spinTurns * Mathf.SmoothStep(0f, 1f, fallProgress), 0f);

                // Squash on contact, then ease back to the neutral scale.
                float squash = height <= 0.001f && elapsed >= dropDuration
                    ? impactSquash * Mathf.Clamp01(1f - (elapsed - dropDuration) / Mathf.Max(0.0001f, dropDuration * 0.5f))
                    : 0f;

                token.localScale = new Vector3(
                    baseScale.x * (1f + squash * 0.5f),
                    baseScale.y * (1f - squash),
                    baseScale.z * (1f + squash * 0.5f));

                // First contact with the board: fire the burst exactly once.
                if (!burstFired && elapsed >= dropDuration)
                {
                    burstFired = true;
                    if (spawnBurst) SpawnBurst(resting, color);
                }

                yield return null;
            }

            token.position = resting;
            token.localScale = baseScale;
            token.rotation = Quaternion.Euler(0f, baseYaw + 360f * spinTurns, 0f);

            if (spawnBurst && !burstFired) SpawnBurst(resting, color);
        }

        /// <summary>
        /// Height above the resting position at time <paramref name="time"/>. Segment 0 is the fall
        /// (accelerating), each later segment is a parabolic hop of decaying amplitude.
        /// </summary>
        private float EvaluateBounceHeight(float time, float fallDistance)
        {
            if (time < dropDuration)
            {
                // Quadratic ease-in reads as gravity.
                float t = time / dropDuration;
                return fallDistance * (1f - t * t);
            }

            float cursor = time - dropDuration;
            float amplitude = fallDistance * bounceDamping;
            float duration = dropDuration * 0.5f;

            for (int b = 0; b < bounceCount; b++)
            {
                if (cursor <= duration)
                {
                    // Parabola peaking at the segment midpoint.
                    float t = cursor / duration;
                    return amplitude * 4f * t * (1f - t);
                }

                cursor -= duration;
                amplitude *= bounceDamping;
                duration *= bounceDamping;
            }

            return 0f;
        }

        /// <summary>Fires the impact burst, using the prefab when provided and a procedural system otherwise.</summary>
        private void SpawnBurst(Vector3 position, Color color)
        {
            ParticleSystem system;

            if (burstPrefab != null)
            {
                system = Instantiate(burstPrefab, position, Quaternion.identity, tokenRoot);
            }
            else
            {
                var go = new GameObject("ClaimBurst");
                go.transform.SetParent(tokenRoot, false);
                go.transform.position = position;

                system = go.AddComponent<ParticleSystem>();
                ConfigureProceduralBurst(system, color);
            }

            ParticleSystem.MainModule main = system.main;
            main.startColor = color;

            system.Clear();
            system.Play();

            float lifetime = main.duration + main.startLifetime.constantMax + 0.25f;
            Destroy(system.gameObject, lifetime);
        }

        /// <summary>Configures a short, self-contained radial spark burst with no authored assets.</summary>
        private void ConfigureProceduralBurst(ParticleSystem system, Color color)
        {
            system.Stop();

            ParticleSystem.MainModule main = system.main;
            main.duration = 0.5f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.28f, 0.55f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 3.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.12f);
            main.gravityModifier = 0.9f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = Mathf.Max(burstParticleCount, 8);
            main.startColor = color;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)burstParticleCount) });

            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.12f;

            ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0f));

            var renderer = system.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                // Build the material first, then assign it once: reading renderer.material back would
                // instantiate a second copy and leak the original.
                // Particles must blend, or every spark renders as an opaque quad.
                Material particleMaterial = MaterialUtility.CreateDefaultTransparentMaterial();
                particleMaterial.name = "ClaimBurst_Mat";
                MaterialUtility.SetColor(particleMaterial, color);

                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sharedMaterial = particleMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                _ownedMaterials.Add(particleMaterial);
            }
        }

        /// <summary>Lazily builds (and caches) the shared pyramid mesh used when no prefab is set.</summary>
        private Mesh GetGeneratedMesh()
        {
            if (_generatedMesh != null) return _generatedMesh;

            _generatedMesh = BuildConeMesh(generatedSides, tokenRadius, tokenHeight);
            _generatedMesh.name = $"TriggleToken_{generatedSides}gon";
            _generatedMesh.hideFlags = HideFlags.DontSave;
            return _generatedMesh;
        }

        /// <summary>
        /// Flat-shaded cone/pyramid with its base on the XZ plane and its apex at +Y. Vertices are
        /// duplicated per triangle so each face gets a hard normal.
        /// </summary>
        public static Mesh BuildConeMesh(int sides, float radius, float height)
        {
            sides = Mathf.Max(3, sides);

            var vertices = new List<Vector3>(sides * 6);
            var triangles = new List<int>(sides * 6);

            Vector3 apex = new Vector3(0f, height, 0f);
            Vector3 baseCentre = Vector3.zero;

            for (int i = 0; i < sides; i++)
            {
                float a0 = i / (float)sides * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)sides * Mathf.PI * 2f;

                Vector3 p0 = new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius);
                Vector3 p1 = new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius);

                // Side face, wound counter-clockwise when viewed from outside.
                int sideStart = vertices.Count;
                vertices.Add(p0);
                vertices.Add(p1);
                vertices.Add(apex);
                triangles.Add(sideStart);
                triangles.Add(sideStart + 2);
                triangles.Add(sideStart + 1);

                // Base face, wound the other way so it faces -Y.
                int baseStart = vertices.Count;
                vertices.Add(p0);
                vertices.Add(p1);
                vertices.Add(baseCentre);
                triangles.Add(baseStart);
                triangles.Add(baseStart + 1);
                triangles.Add(baseStart + 2);
            }

            var mesh = new Mesh { name = "Cone" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Destroys every spawned token. Wired to <see cref="GameEvents.OnGameReset"/>.</summary>
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
