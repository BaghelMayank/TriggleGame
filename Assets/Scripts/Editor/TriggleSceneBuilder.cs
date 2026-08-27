using System.Collections.Generic;
using System.IO;
using Triggle.Audio;
using Triggle.Core;
using Triggle.Gameplay;
using Triggle.Net;
using Triggle.Grid;
using Triggle.Interaction;
using Triggle.UI;
using Triggle.Visuals;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Triggle.EditorTools
{
    /// <summary>
    /// One-click scene generator: creates every asset the game needs (materials, token mesh, peg and
    /// token prefabs, particle burst, player palette), builds the full scene - main menu, in-game HUD
    /// and game-over screen, all in TextMeshPro - wires every serialized reference and saves the result.
    /// </summary>
    /// <remarks>
    /// Private <c>[SerializeField]</c> fields are written through <see cref="SerializedObject"/> rather
    /// than reflection, so the values persist correctly and the tool breaks loudly (a console warning
    /// naming the property) if a field is ever renamed.
    /// <para>
    /// Existing assets are reused rather than duplicated, and the tool never silently overwrites an
    /// existing scene - it asks first.
    /// </para>
    /// </remarks>
    public static class TriggleSceneBuilder
    {
        private const string ScenesFolder = "Assets/Scenes";
        private const string SettingsFolder = "Assets/Settings/Triggle";
        private const string MaterialsFolder = "Assets/Materials/Triggle";
        private const string PrefabsFolder = "Assets/Prefabs/Triggle";
        private const string MeshesFolder = "Assets/Meshes/Triggle";
        private const string TexturesFolder = "Assets/Textures/Triggle";

        private const string ScenePath = ScenesFolder + "/Triggle.unity";
        private const string GradientPath = TexturesFolder + "/T_MenuGradient.png";
        private const string PalettePath = SettingsFolder + "/PlayerColorPalette.asset";
        private const string PegPrefabPath = PrefabsFolder + "/Peg.prefab";
        private const string TokenPrefabPath = PrefabsFolder + "/ClaimToken.prefab";
        private const string BurstPrefabPath = PrefabsFolder + "/ClaimBurst.prefab";
        private const string TokenMeshPath = MeshesFolder + "/ClaimTokenMesh.asset";

        private const string SfxFolder = "Assets/Audio/Triggle/SFX";
        private const string MusicFolder = "Assets/Audio/Triggle/Music";
        private const string ThemesFolder = SettingsFolder + "/Themes";

        private const string TmpExamples = "Assets/TextMesh Pro/Examples & Extras/Resources/Fonts & Materials/";
        private const string TmpEssentials = "Assets/TextMesh Pro/Resources/Fonts & Materials/";

        // Board shape used for the generated scene. Camera framing is derived from the radius.
        private const int BoardRadius = 3;

        /// <summary>Collinear pegs per rubber band. 4 is the standard rule (covering 3 unit edges).</summary>
        private const int PegsPerBand = 4;

        /// <summary>Camera tilt. Shared by the starting pose and the runtime rig so the two agree.</summary>
        private const float CameraPitch = 56f;

        // HUD chrome the board must stay clear of, in canvas units at the 1080-tall reference. The
        // canvas scaler matches height, so a canvas unit is always 1/1080 of the screen and these
        // convert straight to viewport fractions.
        private const float UiReferenceHeight = 1080f;

        /// <summary>Bottom edge of the top chrome: title chip, round counter, pause, bands-left label.</summary>
        private const float HudTopChromeUnits = 131f;

        /// <summary>Top edge of the turn banner.</summary>
        private const float HudBottomChromeUnits = 128f;

        /// <summary>Breathing room between the chrome and the board, applied equally at both ends.</summary>
        private const float HudBoardGapUnits = 26f;

        private const float PegSpacing = 1f;
        private const float PegScale = 0.3f;
        private const float PegHeight = 0.35f;

        // The coloured cell fill now carries ownership, so the token is a smaller marker on top of it.
        private const float TokenRadius = 0.19f;
        private const float TokenHeight = 0.34f;
        private const int TokenSides = 3;

        // --- palette -------------------------------------------------------
        private static readonly Color BackgroundColor = new Color(0.055f, 0.063f, 0.090f);
        private static readonly Color TableColor = new Color(0.106f, 0.122f, 0.169f);
        private static readonly Color PegColor = new Color(0.78f, 0.78f, 0.83f);

        private static readonly Color Accent = new Color(0.290f, 0.780f, 0.550f);

        // --- shared sprites / fonts, resolved once per build ----------------
        private static Sprite _gradientSprite;
        private static TMP_FontAsset _displayFont;
        private static TMP_FontAsset _headingFont;
        private static TMP_FontAsset _bodyFont;
        private static TMP_FontAsset _bodyLightFont;

        // ------------------------------------------------------------------ menu entries

        [MenuItem("Tools/Triggle/Build Play Scene", false, 0)]
        public static void BuildPlayScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string scenePath = ResolveScenePath();
            if (string.IsNullOrEmpty(scenePath)) return;

            UnityEngine.SceneManagement.Scene scene =
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolders();
            ResolveSharedResources();

            TriggleAssets assets = CreateOrLoadAssets();
            BuildHierarchy(assets);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                "[Triggle] Scene built and saved.\n" +
                $"  Scene    : {scenePath}\n" +
                $"  Board    : radius {BoardRadius}, {PegsPerBand}-peg bands -> " +
                $"{AxialMath.PegCountForRadius(BoardRadius)} pegs, {6 * BoardRadius * BoardRadius} triangles\n" +
                $"  Fonts    : {FontName(_displayFont)} / {FontName(_headingFont)} / {FontName(_bodyFont)}\n" +
                $"  Palette  : {PalettePath}\n" +
                $"  Prefabs  : {PegPrefabPath}, {TokenPrefabPath}, {BurstPrefabPath}\n" +
                "  Press Play - the main menu comes up first.");
        }

        [MenuItem("Tools/Triggle/Create Assets Only", false, 1)]
        public static void CreateAssetsOnly()
        {
            EnsureFolders();
            ResolveSharedResources();
            CreateOrLoadAssets();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Triggle] Assets created under {SettingsFolder}, {MaterialsFolder}, " +
                      $"{PrefabsFolder} and {MeshesFolder}.");
        }

        private static string FontName(TMP_FontAsset font) => font != null ? font.name : "<missing>";

        /// <summary>
        /// Picks the scene path, asking before touching an existing file. Returns null when the user
        /// cancels.
        /// </summary>
        private static string ResolveScenePath()
        {
            if (!File.Exists(ScenePath)) return ScenePath;

            int choice = EditorUtility.DisplayDialogComplex(
                "Triggle scene already exists",
                $"'{ScenePath}' already exists.\n\nOverwrite it, or save the generated scene under a new name?",
                "Overwrite", "Cancel", "Save as new");

            return choice switch
            {
                0 => ScenePath,
                2 => AssetDatabase.GenerateUniqueAssetPath(ScenePath),
                _ => null
            };
        }

        /// <summary>
        /// Resolves the UI sprites and TextMeshPro fonts once per build.
        /// </summary>
        /// <remarks>
        /// Fonts are generated from the OFL-licensed TTFs bundled under <c>Assets/Fonts/Triggle</c>
        /// (Archivo Black / Chakra Petch / Poppins). If those files are missing, it falls back to TMP's
        /// bundled example fonts, then to the project default - so the build always succeeds and the
        /// console reports exactly which fonts were used.
        /// </remarks>
        private static void ResolveSharedResources()
        {
            EnsureFolder(TexturesFolder);
            _gradientSprite = LoadOrCreateGradientSprite(GradientPath,
                new Color(0.075f, 0.086f, 0.130f, 0.98f),
                new Color(0.020f, 0.024f, 0.039f, 1.00f));

            TMP_FontAsset fallback =
                AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TmpEssentials + "LiberationSans SDF.asset")
                ?? TMP_Settings.defaultFontAsset;

            _displayFont = ResolveFont(TriggleFontSetup.DisplaySource, "Anton SDF", fallback);
            _headingFont = ResolveFont(TriggleFontSetup.HeadingSource, "Oswald Bold SDF", fallback);
            _bodyFont = ResolveFont(TriggleFontSetup.BodySource, "Roboto-Bold SDF", fallback);
            _bodyLightFont = ResolveFont(TriggleFontSetup.BodyLightSource, "Roboto-Bold SDF", _bodyFont);

            if (_displayFont == null)
            {
                Debug.LogWarning("[Triggle] No TextMeshPro font asset could be resolved. Run " +
                                 "Window > TextMeshPro > Import TMP Essential Resources, then rebuild.");
            }
        }

        /// <summary>Bundled font first, then the matching TMP example font, then the project default.</summary>
        private static TMP_FontAsset ResolveFont(string bundledSource, string tmpExampleName,
                                                  TMP_FontAsset fallback)
        {
            TMP_FontAsset generated = TriggleFontSetup.GetOrCreate(bundledSource);
            if (generated != null) return generated;

            var example = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>($"{TmpExamples}{tmpExampleName}.asset");
            return example != null ? example : fallback;
        }

        /// <summary>
        /// Writes a small vertical-gradient PNG and imports it as a sprite, used to give the menu and
        /// game-over backdrops depth instead of a flat fill.
        /// </summary>
        private static Sprite LoadOrCreateGradientSprite(string assetPath, Color top, Color bottom)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (existing != null) return existing;

            const int height = 256;
            const int width = 4;

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            for (int y = 0; y < height; y++)
            {
                Color row = Color.Lerp(bottom, top, y / (float)(height - 1));
                for (int x = 0; x < width; x++) texture.SetPixel(x, y, row);
            }
            texture.Apply();

            // Application.dataPath ends in "/Assets", and assetPath starts with "Assets".
            string absolute = Application.dataPath + assetPath.Substring("Assets".Length);
            File.WriteAllBytes(absolute, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        }

        // ------------------------------------------------------------------ assets

        /// <summary>Every generated asset, passed to the hierarchy builder for wiring.</summary>
        private sealed class TriggleAssets
        {
            public PlayerColorPalette Palette;
            public GameObject PegPrefab;
            public GameObject TokenPrefab;
            public ParticleSystem BurstPrefab;
            public Material BandMaterial;
            public Material PreviewMaterial;
            public Material SlabMaterial;
            public Material RimMaterial;
            public Material LineMaterial;
            public Material SocketMaterial;
            public Material CellFillMaterial;
            public Material PegMaterial;
        }

        private static void EnsureFolders()
        {
            EnsureFolder(ScenesFolder);
            EnsureFolder(SettingsFolder);
            EnsureFolder(MaterialsFolder);
            EnsureFolder(PrefabsFolder);
            EnsureFolder(MeshesFolder);
            EnsureFolder(TexturesFolder);
        }

        /// <summary>Creates a project folder and any missing parents.</summary>
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string[] parts = path.Split('/');
            string current = parts[0];   // "Assets"

            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static TriggleAssets CreateOrLoadAssets()
        {
            var assets = new TriggleAssets
            {
                Palette = LoadOrCreatePalette(),
                BandMaterial = LoadOrCreateMaterial(MaterialsFolder + "/M_Band.mat", Color.white, true, 0f),
                SlabMaterial = LoadOrCreateMaterial(MaterialsFolder + "/M_BoardSlab.mat", TableColor, false, 0.12f),
                RimMaterial = LoadOrCreateMaterial(MaterialsFolder + "/M_BoardRim.mat", Accent, true, 0f),
                LineMaterial = LoadOrCreateMaterial(MaterialsFolder + "/M_BoardLines.mat",
                    new Color(0.32f, 0.36f, 0.46f), true, 0f),
                SocketMaterial = LoadOrCreateMaterial(MaterialsFolder + "/M_PegSocket.mat",
                    new Color(0.055f, 0.063f, 0.090f), true, 0f),

                // These two rely on alpha, so they need a blended surface rather than the opaque default.
                PreviewMaterial = LoadOrCreateMaterial(MaterialsFolder + "/M_BandPreview.mat",
                    Color.white, true, 0f, true),
                CellFillMaterial = LoadOrCreateMaterial(MaterialsFolder + "/M_CellFill.mat",
                    Color.white, true, 0f, true)
            };

            Material pegMaterial = LoadOrCreateMaterial(MaterialsFolder + "/M_Peg.mat", PegColor, false, 0.45f);
            Material tokenMaterial = LoadOrCreateMaterial(MaterialsFolder + "/M_Token.mat", Color.white, false, 0.3f);
            assets.PegMaterial = pegMaterial;

            assets.PegPrefab = LoadOrCreatePegPrefab(pegMaterial);
            assets.TokenPrefab = LoadOrCreateTokenPrefab(tokenMaterial);
            assets.BurstPrefab = LoadOrCreateBurstPrefab();

            return assets;
        }

        private static PlayerColorPalette LoadOrCreatePalette()
        {
            var existing = AssetDatabase.LoadAssetAtPath<PlayerColorPalette>(PalettePath);
            if (existing != null) return existing;

            // The asset's serialized defaults already define four seats (Crimson/Azure/Verdant/Amber).
            var palette = ScriptableObject.CreateInstance<PlayerColorPalette>();
            AssetDatabase.CreateAsset(palette, PalettePath);
            return palette;
        }

        /// <summary>
        /// Creates a material asset for the active render pipeline. Bands and previews use an unlit
        /// shader so they stay readable from a shallow camera angle.
        /// </summary>
        private static Material LoadOrCreateMaterial(string path, Color color, bool unlit, float smoothness,
                                                     bool transparent = false)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Material material = unlit
                ? MaterialUtility.CreateDefaultUnlitMaterial()
                : MaterialUtility.CreateDefaultLitMaterial();

            // Asset materials must be saveable, so clear the runtime-only DontSave flag.
            material.hideFlags = HideFlags.None;
            MaterialUtility.SetColor(material, color);
            if (!unlit) MaterialUtility.SetSmoothness(material, smoothness);
            if (transparent) MaterialUtility.MakeTransparent(material);

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        /// <summary>
        /// Peg prefab: a scaled sphere head with a thin post child. BoardManager only sets the position
        /// of a supplied prefab, so the visual scale is baked into the prefab itself.
        /// </summary>
        private static GameObject LoadOrCreatePegPrefab(Material pegMaterial)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PegPrefabPath);
            if (existing != null) return existing;

            GameObject root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            root.name = "Peg";
            root.transform.localScale = Vector3.one * PegScale;
            root.GetComponent<MeshRenderer>().sharedMaterial = pegMaterial;

            var collider = root.GetComponent<SphereCollider>();
            collider.radius = 0.5f;   // PegComponent widens this at bind time for comfortable picking.

            GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            post.name = "Post";
            Object.DestroyImmediate(post.GetComponent<Collider>());
            post.transform.SetParent(root.transform, false);

            float postHalfHeight = PegHeight / PegScale * 0.5f;
            post.transform.localScale = new Vector3(0.45f, postHalfHeight, 0.45f);
            post.transform.localPosition = new Vector3(0f, -postHalfHeight, 0f);
            post.GetComponent<MeshRenderer>().sharedMaterial = pegMaterial;

            root.AddComponent<PegComponent>();

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PegPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        /// <summary>Token prefab built on a saved pyramid mesh, so it can be edited like any asset.</summary>
        private static GameObject LoadOrCreateTokenPrefab(Material tokenMaterial)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(TokenPrefabPath);
            if (existing != null) return existing;

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(TokenMeshPath);
            if (mesh == null)
            {
                // Reuse the runtime generator so the asset and the procedural fallback never diverge.
                mesh = TokenSpawner.BuildConeMesh(TokenSides, TokenRadius, TokenHeight);
                mesh.name = "ClaimTokenMesh";
                mesh.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(mesh, TokenMeshPath);
            }

            var root = new GameObject("ClaimToken");
            root.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = tokenMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, TokenPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        /// <summary>Authored version of the claim burst, mirroring TokenSpawner's procedural fallback.</summary>
        private static ParticleSystem LoadOrCreateBurstPrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(BurstPrefabPath);
            if (existing != null) return existing.GetComponent<ParticleSystem>();

            var root = new GameObject("ClaimBurst");
            var system = root.AddComponent<ParticleSystem>();
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
            main.maxParticles = 24;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)18) });

            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.12f;

            ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0f));

            // TokenSpawner tints main.startColor per seat, so the material stays neutral white.
            var particleRenderer = root.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            particleRenderer.sharedMaterial =
                LoadOrCreateMaterial(MaterialsFolder + "/M_ClaimBurst.mat", Color.white, true, 0f, true);
            particleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, BurstPrefabPath);
            Object.DestroyImmediate(root);
            return prefab.GetComponent<ParticleSystem>();
        }

        // ------------------------------------------------------------------ hierarchy

        private static void BuildHierarchy(TriggleAssets assets)
        {
            Camera camera = CreateCamera();
            CreateLight();

            // --- Board -------------------------------------------------------
            var boardGo = new GameObject("Board");
            var board = boardGo.AddComponent<BoardManager>();
            var pegRoot = new GameObject("Pegs");
            pegRoot.transform.SetParent(boardGo.transform, false);

            // Replaces the old placeholder ground plane: a bevelled hex slab, an accent rim, lattice
            // lines and peg sockets, all generated from the board data. Also carries the click collider.
            var boardVisuals = boardGo.AddComponent<BoardVisuals>();

            using (var so = new SerializedWiring(board))
            {
                so.Int("radius", BoardRadius);
                so.Int("pegsPerBand", PegsPerBand);
                so.Float("pegSpacing", PegSpacing);
                so.Bool("buildOnAwake", false);   // GameFlowController builds after listeners subscribe.
                so.Ref("pegPrefab", assets.PegPrefab);
                so.Float("pegScale", PegScale);
                so.Float("pegHeight", PegHeight);
                so.Ref("pegRoot", pegRoot.transform);
                so.Bool("drawGizmos", true);
            }

            using (var so = new SerializedWiring(boardVisuals))
            {
                so.Ref("board", board);
                so.Ref("slabMaterial", assets.SlabMaterial);
                so.Ref("rimMaterial", assets.RimMaterial);
                so.Ref("lineMaterial", assets.LineMaterial);
                so.Ref("socketMaterial", assets.SocketMaterial);
                so.Bool("drawRim", true);
                so.Bool("drawLatticeLines", true);
                so.Bool("drawSockets", true);
            }

            // Framing is a runtime job, not a build-time one: board size is a Settings choice, so a
            // baked camera position would clip a radius 4 or 5 board off the edges of the screen.
            var cameraRig = camera.gameObject.AddComponent<BoardCameraRig>();

            using (var so = new SerializedWiring(cameraRig))
            {
                so.Ref("board", board);
                so.Ref("boardVisuals", boardVisuals);
                so.Float("pitch", CameraPitch);
                so.Float("sidePadding", 0.04f);

                // Reserve exactly what the HUD occupies at each end, plus the same breathing gap on
                // both. The rig centres the board in what is left, so the space above the board and
                // the space below it come out equal - which they were not when the board was centred
                // in the whole viewport and the turn banner ended up sitting on top of it.
                so.Float("topMargin", (HudTopChromeUnits + HudBoardGapUnits) / UiReferenceHeight);
                so.Float("bottomMargin", (HudBottomChromeUnits + HudBoardGapUnits) / UiReferenceHeight);
            }

            // --- GameSystems -------------------------------------------------
            var systemsGo = new GameObject("GameSystems");
            var scoreManager = systemsGo.AddComponent<ScoreManager>();
            var flow = systemsGo.AddComponent<GameFlowController>();
            var sound = systemsGo.AddComponent<SoundManager>();
            var match = systemsGo.AddComponent<MatchController>();
            var ai = systemsGo.AddComponent<AiController>();

            // Idle until a transport is handed to it, so a local game is completely unaffected.
            var net = systemsGo.AddComponent<NetworkMatch>();

            // Talks to Unity Lobby and Relay only when the player asks for a room; a local or vs-AI
            // match never signs in and never touches the network.
            var rooms = systemsGo.AddComponent<UgsRoomService>();

            WireAudio(sound);

            using (var so = new SerializedWiring(flow))
            {
                so.Ref("board", board);
                so.Ref("scoreManager", scoreManager);
                so.Bool("autoStart", false);      // the main menu starts the match
                so.Bool("verboseLogging", false);

                so.Int("settings.playerCount", 2);
                so.Bool("settings.requireAtLeastOneNewEdge", true);
                so.Float("settings.bandPlacementDuration", 0.28f);
                so.Float("settings.claimResolveDelay", 0.12f);
                so.Float("settings.turnHandoverDelay", 0.15f);
            }

            using (var so = new SerializedWiring(match))
            {
                so.Ref("flowController", flow);
                so.Int("roundCount", 1);      // the lobby overwrites this
                so.Bool("verboseLogging", false);
            }

            using (var so = new SerializedWiring(net))
            {
                so.Ref("flowController", flow);
                so.Ref("matchController", match);
                so.Bool("verboseLogging", false);
            }

            using (var so = new SerializedWiring(rooms))
            {
                so.Bool("useDtls", true);
                so.Bool("verboseLogging", false);
            }

            using (var so = new SerializedWiring(ai))
            {
                so.Ref("flowController", flow);
                so.Float("pegPickInterval", 0.14f);
                so.Bool("verboseLogging", false);
            }

            // --- Interaction -------------------------------------------------
            var interactionGo = new GameObject("Interaction");
            var input = interactionGo.AddComponent<InputController>();
            var preview = interactionGo.AddComponent<BandPlacementPreview>();

            using (var so = new SerializedWiring(input))
            {
                so.Ref("flowController", flow);
                so.Ref("raycastCamera", camera);
            }

            using (var so = new SerializedWiring(preview))
            {
                so.Ref("flowController", flow);
                so.Ref("inputController", input);
                so.Ref("previewMaterial", assets.PreviewMaterial);
            }

            // --- Visuals -----------------------------------------------------
            var visualsGo = new GameObject("Visuals");
            var bands = visualsGo.AddComponent<RubberBandRenderer>();
            var tokens = visualsGo.AddComponent<TokenSpawner>();
            var highlighter = visualsGo.AddComponent<PegHighlighter>();
            var fills = visualsGo.AddComponent<CellFillRenderer>();
            var claimVfx = visualsGo.AddComponent<ClaimVfx>();

            var bandRoot = new GameObject("Bands");
            bandRoot.transform.SetParent(visualsGo.transform, false);
            var tokenRoot = new GameObject("Tokens");
            tokenRoot.transform.SetParent(visualsGo.transform, false);
            var fillRoot = new GameObject("CellFills");
            fillRoot.transform.SetParent(visualsGo.transform, false);
            var vfxRoot = new GameObject("ClaimVfx");
            vfxRoot.transform.SetParent(visualsGo.transform, false);

            using (var so = new SerializedWiring(bands))
            {
                so.Ref("palette", assets.Palette);
                so.Ref("bandMaterialOverride", assets.BandMaterial);
                so.Ref("bandRoot", bandRoot.transform);
                so.Bool("tintByPlayer", true);
            }

            using (var so = new SerializedWiring(tokens))
            {
                so.Ref("palette", assets.Palette);
                so.Ref("tokenPrefab", assets.TokenPrefab);
                so.Ref("burstPrefab", assets.BurstPrefab);
                so.Ref("tokenRoot", tokenRoot.transform);
                so.Int("generatedSides", TokenSides);
                so.Float("tokenRadius", TokenRadius);
                so.Float("tokenHeight", TokenHeight);
                so.Bool("spawnBurst", true);
            }

            using (var so = new SerializedWiring(highlighter))
            {
                so.Ref("flowController", flow);
                so.Bool("dimUnplayablePegs", true);
                so.Bool("highlightLegalStartPegs", true);
            }

            using (var so = new SerializedWiring(fills))
            {
                so.Ref("palette", assets.Palette);
                so.Ref("fillMaterialOverride", assets.CellFillMaterial);
                so.Ref("fillRoot", fillRoot.transform);
            }

            using (var so = new SerializedWiring(claimVfx))
            {
                so.Ref("palette", assets.Palette);
                so.Ref("vfxRoot", vfxRoot.transform);
                so.Ref("popupFont", _headingFont);
                so.Bool("spawnRing", true);
                so.Bool("spawnPopup", true);
                so.Bool("kickCamera", true);
            }

            // --- Board themes ------------------------------------------------
            var themeLibrary = boardGo.AddComponent<BoardThemeLibrary>();
            BoardTheme[] themes = LoadOrCreateThemes();

            using (var so = new SerializedWiring(themeLibrary))
            {
                so.Ref("boardVisuals", boardVisuals);
                so.Ref("board", board);
                so.Ref("targetCamera", camera);
                so.Ref("pegMaterial", assets.PegMaterial);

                so.ArraySize("themes", themes.Length);
                for (int i = 0; i < themes.Length; i++)
                    so.Ref($"themes.Array.data[{i}]", themes[i]);
            }

            // --- UI ----------------------------------------------------------
            TriggleUIBuilder.Build(new TriggleUIBuilder.Context
            {
                Flow = flow,
                Match = match,
                Net = net,
                Rooms = rooms,
                Palette = assets.Palette,
                Themes = themeLibrary,
                Gradient = _gradientSprite,
                Display = _displayFont,
                Heading = _headingFont,
                Body = _bodyFont,
                BodyLight = _bodyLightFont
            });

            Selection.activeGameObject = systemsGo;
        }

        /// <summary>
        /// Assigns the bundled audio clips. Any clip that is missing is simply left empty, and
        /// SoundManager falls back to a synthesised tone for it.
        /// </summary>
        private static void WireAudio(SoundManager sound)
        {
            using var so = new SerializedWiring(sound);

            so.Ref("pegSelectClip", LoadClip(SfxFolder, "peg-select"));
            so.Ref("bandPlaceClip", LoadClip(SfxFolder, "band-place"));
            so.Ref("cellClaimClip", LoadClip(SfxFolder, "claim-score"));
            so.Ref("tokenLandClip", LoadClip(SfxFolder, "token-land"));
            so.Ref("invalidMoveClip", LoadClip(SfxFolder, "invalid"));
            so.Ref("uiClickClip", LoadClip(SfxFolder, "ui-click"));
            so.Ref("uiBackClip", LoadClip(SfxFolder, "ui-back"));
            so.Ref("winAccentClip", LoadClip(SfxFolder, "win-accent"));

            so.Ref("musicTrack", LoadClip(MusicFolder, "house-in-a-forest-loop"));
            so.Ref("ambienceTrack", LoadClip(MusicFolder, "ambience"));
        }

        private static AudioClip LoadClip(string folder, string fileName)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{folder}/{fileName}.ogg");
            if (clip == null)
                Debug.LogWarning($"[Triggle] Audio clip not found: {folder}/{fileName}.ogg " +
                                 "(a synthesised tone will be used instead).");

            return clip;
        }

        /// <summary>
        /// Creates the six shipped board themes as assets on first run, so the Settings picker has
        /// something to show without hand-authoring ScriptableObjects.
        /// </summary>
        private static BoardTheme[] LoadOrCreateThemes()
        {
            EnsureFolder(ThemesFolder);

            var result = new BoardTheme[BoardTheme.Presets.Length];

            for (int i = 0; i < BoardTheme.Presets.Length; i++)
            {
                BoardTheme.Preset preset = BoardTheme.Presets[i];
                string path = $"{ThemesFolder}/Theme_{i:00}_{preset.Name.Replace(" ", string.Empty)}.asset";

                var theme = AssetDatabase.LoadAssetAtPath<BoardTheme>(path);
                if (theme == null)
                {
                    theme = ScriptableObject.CreateInstance<BoardTheme>();
                    theme.ApplyPreset(preset);
                    AssetDatabase.CreateAsset(theme, path);
                }

                result[i] = theme;
            }

            return result;
        }

        private static Camera CreateCamera()
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";   // InputController falls back to Camera.main.

            var camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = BackgroundColor;
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 200f;

            go.AddComponent<AudioListener>();

            // Starting pose only, so the saved scene looks right in the Scene view. BoardCameraRig
            // recomputes this at runtime from the radius the player actually picked.
            go.transform.position = new Vector3(0f, 4.3f * BoardRadius * PegSpacing, -2.85f * BoardRadius * PegSpacing);
            go.transform.rotation = Quaternion.Euler(CameraPitch, 0f, 0f);

            return camera;
        }

        private static void CreateLight()
        {
            var go = new GameObject("Directional Light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.97f, 0.92f);
            light.intensity = 1.15f;
            light.shadows = LightShadows.Soft;

            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

    }
}
