using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Prepares the project for the frosted-glass UI shader and creates its materials.
    /// </summary>
    /// <remarks>
    /// The glass shader reads <c>_CameraOpaqueTexture</c>, which only exists when <b>Opaque Texture</b>
    /// is enabled on the active URP Asset. Rather than leave that as a manual step buried in a guide,
    /// this enables it on every URP asset in the project and reports what it changed.
    /// <para>
    /// The companion requirement - the Canvas must be Screen Space - Camera, because a Screen Space -
    /// Overlay canvas is composited outside the camera render loop and cannot read scene textures - is
    /// handled by <c>TriggleUIBuilder</c> when it creates the canvas.
    /// </para>
    /// </remarks>
    public static class TriggleGlassSetup
    {
        public const string ShaderName = "Triggle/UI Glass";
        public const string MaterialsFolder = "Assets/Materials/Triggle";

        public const string PanelGlassPath = MaterialsFolder + "/M_UIGlassPanel.mat";
        public const string ControlGlassPath = MaterialsFolder + "/M_UIGlassControl.mat";
        public const string BackdropGlassPath = MaterialsFolder + "/M_UIGlassBackdrop.mat";

        private static readonly int GlassTintId = Shader.PropertyToID("_GlassTint");
        private static readonly int TintStrengthId = Shader.PropertyToID("_TintStrength");
        private static readonly int BlurRadiusId = Shader.PropertyToID("_BlurRadius");
        private static readonly int BrightnessId = Shader.PropertyToID("_Brightness");
        private static readonly int SaturationId = Shader.PropertyToID("_Saturation");

        [MenuItem("Tools/Triggle/Enable Glass UI (URP Opaque Texture)", false, 22)]
        public static void EnableOpaqueTextureMenu()
        {
            int changed = EnableOpaqueTexture();

            if (changed > 0)
                Debug.Log($"[Triggle] Enabled Opaque Texture on {changed} URP asset(s). " +
                          "The frosted-glass UI can now read the scene behind it.");
            else
                Debug.Log("[Triggle] Opaque Texture was already enabled on every URP asset.");
        }

        /// <summary>
        /// Turns on Opaque Texture for every URP asset in the project. Returns how many were changed.
        /// </summary>
        /// <remarks>
        /// The property is not public API on <c>UniversalRenderPipelineAsset</c>, so it is written
        /// through SerializedObject. That also avoids a hard compile-time dependency on the URP
        /// assembly's internals.
        /// </remarks>
        public static int EnableOpaqueTexture()
        {
            string[] guids = AssetDatabase.FindAssets("t:RenderPipelineAsset");
            int changed = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
                if (asset == null) continue;

                var serialized = new SerializedObject(asset);
                SerializedProperty property = serialized.FindProperty("m_RequireOpaqueTexture");
                if (property == null) continue;   // not a URP asset

                if (property.boolValue) continue;

                property.boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(asset);
                changed++;
            }

            if (changed > 0) AssetDatabase.SaveAssets();
            return changed;
        }

        /// <summary>True when the glass shader is present and compiled.</summary>
        public static bool IsGlassAvailable => Shader.Find(ShaderName) != null;

        /// <summary>
        /// Creates the three glass materials, or returns the existing ones. Returns null when the
        /// shader is missing, so callers can fall back to flat panels.
        /// </summary>
        public static bool TryCreateMaterials(out Material panel, out Material control,
                                               out Material backdrop)
        {
            panel = control = backdrop = null;

            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[Triggle] Shader '{ShaderName}' not found. UI panels will use flat " +
                                 "translucent fills instead of frosted glass.");
                return false;
            }

            // Cards: strong blur, heavy tint - text sits on these, so legibility wins.
            panel = LoadOrCreate(PanelGlassPath, shader,
                new Color(0.055f, 0.067f, 0.106f, 0.90f), 0.66f, 20f, 0.80f, 0.65f);

            // Buttons and chips: lighter, so the board still reads through them in-game.
            control = LoadOrCreate(ControlGlassPath, shader,
                new Color(0.30f, 0.36f, 0.46f, 0.42f), 0.34f, 10f, 1.05f, 0.85f);

            // Full-screen backdrop behind menus: very strong blur, so the board becomes an
            // unrecognisable wash rather than a distracting readable board.
            backdrop = LoadOrCreate(BackdropGlassPath, shader,
                new Color(0.020f, 0.026f, 0.043f, 0.96f), 0.80f, 40f, 0.55f, 0.45f);

            return true;
        }

        private static Material LoadOrCreate(string path, Shader shader, Color tint, float tintStrength,
                                              float blurRadius, float brightness, float saturation)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            var material = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            material.SetColor(GlassTintId, tint);
            material.SetFloat(TintStrengthId, tintStrength);
            material.SetFloat(BlurRadiusId, blurRadius);
            material.SetFloat(BrightnessId, brightness);
            material.SetFloat(SaturationId, saturation);

            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
