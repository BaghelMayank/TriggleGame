using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Turns the bundled TrueType files into TextMeshPro font assets.
    /// </summary>
    /// <remarks>
    /// The three roles are chosen to suit a geometric board game:
    /// <list type="bullet">
    /// <item><b>Display</b> - Archivo Black: a very heavy geometric grotesque for the title, the winner
    /// banner and the PLAY button.</item>
    /// <item><b>Heading</b> - Chakra Petch Bold: chamfered, angular letterforms that echo the triangular
    /// lattice, with tabular-feeling numerals for scores.</item>
    /// <item><b>Body</b> - Poppins: a clean geometric sans that stays legible at small sizes on a phone.</item>
    /// </list>
    /// All three are licensed under the SIL Open Font License (see the LICENSE-*.txt files next to the
    /// TTFs), which permits commercial use and redistribution inside an app.
    /// <para>
    /// Assets are generated in <see cref="AtlasPopulationMode.Dynamic"/> mode, so glyphs are rasterised
    /// on demand instead of being baked into a fixed atlas. That keeps every character available at any
    /// size; the trade-off is that the source TTF must ship with the build, which is why the importer's
    /// <c>includeFontData</c> flag is forced on.
    /// </para>
    /// </remarks>
    public static class TriggleFontSetup
    {
        public const string FontsFolder = "Assets/Fonts/Triggle";
        public const string GeneratedFolder = FontsFolder + "/Generated";

        public const string DisplaySource = "ArchivoBlack-Regular";
        public const string HeadingSource = "ChakraPetch-Bold";
        public const string HeadingLightSource = "ChakraPetch-SemiBold";
        public const string BodySource = "Poppins-SemiBold";
        public const string BodyLightSource = "Poppins-Medium";

        // TextMeshPro's own defaults for a 1024x1024 SDF atlas; proven across a wide size range.
        private const int SamplingPointSize = 90;
        private const int AtlasPadding = 9;
        private const int AtlasDimension = 1024;

        [MenuItem("Tools/Triggle/Rebuild Font Assets", false, 20)]
        public static void RebuildFontAssets()
        {
            EnsureGeneratedFolder();

            int created = 0;
            foreach (string source in new[]
                     { DisplaySource, HeadingSource, HeadingLightSource, BodySource, BodyLightSource })
            {
                if (GetOrCreate(source, true) != null) created++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Triggle] Rebuilt {created} TextMeshPro font asset(s) in {GeneratedFolder}.");
        }

        /// <summary>
        /// Returns the generated font asset for a bundled TTF, creating it on first use.
        /// Returns null (with a warning) when the TTF is missing.
        /// </summary>
        /// <param name="sourceName">File name of the TTF without extension.</param>
        /// <param name="forceRebuild">Regenerate even if the asset already exists.</param>
        public static TMP_FontAsset GetOrCreate(string sourceName, bool forceRebuild = false)
        {
            string assetPath = $"{GeneratedFolder}/{sourceName} SDF.asset";

            if (!forceRebuild)
            {
                var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
                if (existing != null) return existing;
            }

            string ttfPath = $"{FontsFolder}/{sourceName}.ttf";
            var sourceFont = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);

            if (sourceFont == null)
            {
                Debug.LogWarning($"[Triggle] Font file not found at '{ttfPath}'. " +
                                 "The scene builder will fall back to TextMeshPro's bundled fonts.");
                return null;
            }

            EnsureGeneratedFolder();
            ConfigureImporter(ttfPath);

            TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
                sourceFont,
                SamplingPointSize,
                AtlasPadding,
                GlyphRenderMode.SDFAA,
                AtlasDimension,
                AtlasDimension,
                AtlasPopulationMode.Dynamic);

            if (fontAsset == null)
            {
                Debug.LogWarning($"[Triggle] TextMeshPro could not build a font asset from '{ttfPath}'.");
                return null;
            }

            fontAsset.name = $"{sourceName} SDF";

            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);

            AssetDatabase.CreateAsset(fontAsset, assetPath);

            // The atlas texture and material must live inside the font asset, or they are lost on reload.
            if (fontAsset.material != null)
            {
                fontAsset.material.name = $"{fontAsset.name} Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            if (fontAsset.atlasTextures != null)
            {
                for (int i = 0; i < fontAsset.atlasTextures.Length; i++)
                {
                    Texture2D atlas = fontAsset.atlasTextures[i];
                    if (atlas == null) continue;

                    atlas.name = $"{fontAsset.name} Atlas {i}";
                    AssetDatabase.AddObjectToAsset(atlas, fontAsset);
                }
            }

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();

            return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
        }

        /// <summary>
        /// Dynamic font assets rasterise from the source TTF at runtime, so the font data has to be
        /// included in the player build.
        /// </summary>
        private static void ConfigureImporter(string ttfPath)
        {
            if (AssetImporter.GetAtPath(ttfPath) is not TrueTypeFontImporter importer) return;
            if (importer.fontTextureCase == FontTextureCase.Dynamic && importer.includeFontData) return;

            importer.fontTextureCase = FontTextureCase.Dynamic;
            importer.includeFontData = true;
            importer.SaveAndReimport();
        }

        private static void EnsureGeneratedFolder()
        {
            if (AssetDatabase.IsValidFolder(GeneratedFolder)) return;

            if (!AssetDatabase.IsValidFolder(FontsFolder))
            {
                if (!AssetDatabase.IsValidFolder("Assets/Fonts"))
                    AssetDatabase.CreateFolder("Assets", "Fonts");

                AssetDatabase.CreateFolder("Assets/Fonts", "Triggle");
            }

            AssetDatabase.CreateFolder(FontsFolder, "Generated");
        }
    }
}
