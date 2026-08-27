using UnityEngine;

namespace Triggle.Core
{
    /// <summary>
    /// Render-pipeline-agnostic material helpers. The project ships without art assets, so every
    /// visual system can fall back to a runtime-created material. URP and the Built-in pipeline use
    /// different shader names and property IDs, so all writes go through here.
    /// </summary>
    public static class MaterialUtility
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int GlossinessId = Shader.PropertyToID("_Glossiness");

        private static readonly string[] LitShaderCandidates =
        {
            "Universal Render Pipeline/Lit",
            "HDRP/Lit",
            "Standard",
            "Legacy Shaders/Diffuse"
        };

        private static readonly string[] UnlitShaderCandidates =
        {
            "Universal Render Pipeline/Unlit",
            "Unlit/Color",
            "Sprites/Default"
        };

        /// <summary>Creates an opaque lit material valid for the active render pipeline.</summary>
        public static Material CreateDefaultLitMaterial()
        {
            var material = new Material(FindShader(LitShaderCandidates)) { hideFlags = HideFlags.DontSave };
            SetSmoothness(material, 0.35f);
            return material;
        }

        /// <summary>Creates an unlit material, the right choice for LineRenderer rubber bands.</summary>
        public static Material CreateDefaultUnlitMaterial()
        {
            return new Material(FindShader(UnlitShaderCandidates)) { hideFlags = HideFlags.DontSave };
        }

        /// <summary>
        /// Creates an unlit material set up for alpha blending. Use this whenever the colour's alpha is
        /// meant to be respected - translucent cell fills, preview ghosts, particles.
        /// </summary>
        public static Material CreateDefaultTransparentMaterial()
        {
            Material material = CreateDefaultUnlitMaterial();
            MakeTransparent(material);
            return material;
        }

        /// <summary>
        /// Switches a material to alpha-blended transparency.
        /// </summary>
        /// <remarks>
        /// Required because both URP's Unlit/Lit and the Built-in Standard shader default to opaque:
        /// writing an alpha into the base colour has no visible effect until the surface type, blend
        /// factors, ZWrite and render queue are all changed together. URP reads <c>_Surface</c>/
        /// <c>_Blend</c>, Built-in reads <c>_Mode</c>, so both are set and whichever exists wins.
        /// </remarks>
        public static void MakeTransparent(Material material)
        {
            if (material == null) return;

            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);   // URP: Transparent
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);       // URP: Alpha
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);         // Built-in: Transparent
            if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 0f);

            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);

            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);

            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);

            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>Clones <paramref name="source"/>, or creates a lit material when it is null.</summary>
        public static Material Instantiate(Material source, string debugName)
        {
            Material material = source != null
                ? new Material(source)
                : CreateDefaultLitMaterial();

            material.name = debugName;
            material.hideFlags = HideFlags.DontSave;
            return material;
        }

        /// <summary>Writes the base colour, covering both URP (<c>_BaseColor</c>) and Built-in (<c>_Color</c>).</summary>
        public static void SetColor(Material material, Color color)
        {
            if (material == null) return;
            if (material.HasProperty(BaseColorId)) material.SetColor(BaseColorId, color);
            if (material.HasProperty(ColorId)) material.SetColor(ColorId, color);
        }

        /// <summary>Enables and writes emission when the shader supports it; a no-op otherwise.</summary>
        public static void SetEmission(Material material, Color emission)
        {
            if (material == null || !material.HasProperty(EmissionColorId)) return;

            bool lit = emission.maxColorComponent > 0.001f;
            if (lit) material.EnableKeyword("_EMISSION");
            else material.DisableKeyword("_EMISSION");
            // Note: EnableKeyword/DisableKeyword are Unity's own Material members.

            material.SetColor(EmissionColorId, emission);
            material.globalIlluminationFlags = lit
                ? MaterialGlobalIlluminationFlags.RealtimeEmissive
                : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }

        public static void SetSmoothness(Material material, float smoothness)
        {
            if (material == null) return;
            if (material.HasProperty(SmoothnessId)) material.SetFloat(SmoothnessId, smoothness);
            if (material.HasProperty(GlossinessId)) material.SetFloat(GlossinessId, smoothness);
        }

        /// <summary>Multiplies the alpha of the current base colour, used for ghost/preview visuals.</summary>
        public static void SetAlpha(Material material, float alpha)
        {
            if (material == null) return;

            Color current = Color.white;
            if (material.HasProperty(BaseColorId)) current = material.GetColor(BaseColorId);
            else if (material.HasProperty(ColorId)) current = material.GetColor(ColorId);

            current.a = alpha;
            SetColor(material, current);
        }

        private static Shader FindShader(string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                Shader shader = Shader.Find(candidates[i]);
                if (shader != null) return shader;
            }

            // Guaranteed to exist in every pipeline; keeps the game running instead of throwing.
            return Shader.Find("Hidden/InternalErrorShader");
        }
    }
}
