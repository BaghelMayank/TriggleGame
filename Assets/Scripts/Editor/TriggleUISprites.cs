using System.IO;
using UnityEditor;
using UnityEngine;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Generates the 9-sliced sprites the neon UI is built from: a rounded pill fill, a crisp outline
    /// ring, a soft outer glow, and matching panel variants.
    /// </summary>
    /// <remarks>
    /// Everything is drawn white so the UI can tint it per element, and every shape is rendered from a
    /// signed distance field, which gives clean anti-aliased edges at any size.
    /// <para>
    /// Sprites are authored at 96x96 with a 47px 9-slice border, leaving a 2px stretchable middle. That
    /// keeps the corner radius a true half-height pill for controls up to ~96px tall, and degrades to a
    /// generous rounded rect above that.
    /// </para>
    /// </remarks>
    public static class TriggleUISprites
    {
        public const string Folder = "Assets/Textures/Triggle/UI";

        public const string PillFill = Folder + "/T_PillFill.png";
        public const string PillOutline = Folder + "/T_PillOutline.png";
        public const string PillGlow = Folder + "/T_PillGlow.png";
        public const string PanelFill = Folder + "/T_PanelFill.png";
        public const string PanelOutline = Folder + "/T_PanelOutline.png";
        public const string PanelGlow = Folder + "/T_PanelGlow.png";
        public const string CircleFill = Folder + "/T_CircleFill.png";
        public const string CircleOutline = Folder + "/T_CircleOutline.png";

        /// <summary>Number of generated player emblems.</summary>
        public const int AvatarCount = 4;

        /// <summary>Path of one of the generated player emblems.</summary>
        public static string AvatarPath(int index) => $"{Folder}/T_Avatar{index}.png";

        private const int PillSize = 96;
        private const int PillRadius = 47;
        private const int PillBorder = 47;

        private const int PanelSize = 96;
        private const int PanelRadius = 26;
        private const int PanelBorder = 30;

        private const int CircleSize = 128;

        private enum Shape { Fill, Outline, Glow }

        [MenuItem("Tools/Triggle/Rebuild UI Sprites", false, 21)]
        public static void RebuildAll()
        {
            EnsureFolder();

            Generate(PillFill, PillSize, PillRadius, PillBorder, Shape.Fill, 0f, 0f, true);
            Generate(PillOutline, PillSize, PillRadius, PillBorder, Shape.Outline, 5f, 0f, true);
            Generate(PillGlow, PillSize, PillRadius, PillBorder, Shape.Glow, 5f, 22f, true);

            Generate(PanelFill, PanelSize, PanelRadius, PanelBorder, Shape.Fill, 0f, 0f, true);
            Generate(PanelOutline, PanelSize, PanelRadius, PanelBorder, Shape.Outline, 3f, 0f, true);
            Generate(PanelGlow, PanelSize, PanelRadius, PanelBorder, Shape.Glow, 3f, 18f, true);

            Generate(CircleFill, CircleSize, CircleSize / 2, 0, Shape.Fill, 0f, 0f, false);
            Generate(CircleOutline, CircleSize, CircleSize / 2, 0, Shape.Outline, 6f, 0f, false);

            for (int i = 0; i < AvatarCount; i++) GenerateAvatar(i);

            AssetDatabase.Refresh();
            Debug.Log($"[Triggle] Rebuilt {8 + AvatarCount} neon UI sprites in {Folder}.");
        }

        /// <summary>
        /// Player emblems: four distinct geometric glyphs drawn white so the UI can tint them to the
        /// player's colour.
        /// </summary>
        /// <remarks>
        /// These stand in for character portraits. They are deliberately geometric rather than
        /// figurative - triangles and polygons read as part of the game's own language, whereas a bad
        /// placeholder character would just look unfinished.
        /// </remarks>
        private static void GenerateAvatar(int index)
        {
            const int size = 128;
            Vector2[] polygon = AvatarPolygon(index);

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[size * size];
            float half = size * 0.5f;

            // Glyph occupies the middle ~72% of the sprite, leaving breathing room inside its chip.
            float scale = half * 0.72f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2((x + 0.5f - half) / scale, (y + 0.5f - half) / scale);
                    float distance = PolygonDistance(p, polygon) * scale;   // back to pixel units
                    float alpha = Mathf.Clamp01(0.5f - distance);

                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            string assetPath = AvatarPath(index);
            string absolute = Application.dataPath + assetPath.Substring("Assets".Length);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            File.WriteAllBytes(absolute, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer) return;

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        /// <summary>Outline of each emblem in normalised -1..1 space, wound counter-clockwise.</summary>
        private static Vector2[] AvatarPolygon(int index)
        {
            switch (index)
            {
                case 0:   // upward triangle
                    return Regular(3, 1f, 90f);

                case 1:   // diamond
                    return Regular(4, 1f, 90f);

                case 2:   // hexagon
                    return Regular(6, 1f, 0f);

                default:  // chevron
                    return new[]
                    {
                        new Vector2(0f, 1f), new Vector2(1f, -0.2f), new Vector2(0.45f, -0.2f),
                        new Vector2(0f, 0.35f), new Vector2(-0.45f, -0.2f), new Vector2(-1f, -0.2f)
                    };
            }
        }

        private static Vector2[] Regular(int sides, float radius, float startDegrees)
        {
            var points = new Vector2[sides];
            for (int i = 0; i < sides; i++)
            {
                float angle = (startDegrees + i * 360f / sides) * Mathf.Deg2Rad;
                points[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            }

            return points;
        }

        /// <summary>
        /// Signed distance from a point to a polygon: negative inside, positive outside. Distance is to
        /// the nearest edge segment; the sign comes from a crossing test accumulated per edge.
        /// </summary>
        private static float PolygonDistance(Vector2 p, Vector2[] v)
        {
            int n = v.Length;
            float squared = Vector2.Dot(p - v[0], p - v[0]);
            float sign = 1f;

            for (int i = 0, j = n - 1; i < n; j = i, i++)
            {
                Vector2 edge = v[j] - v[i];
                Vector2 toPoint = p - v[i];

                float t = Mathf.Clamp01(Vector2.Dot(toPoint, edge) / Vector2.Dot(edge, edge));
                Vector2 offset = toPoint - edge * t;
                squared = Mathf.Min(squared, Vector2.Dot(offset, offset));

                bool aboveStart = p.y >= v[i].y;
                bool belowEnd = p.y < v[j].y;
                bool leftOfEdge = edge.x * toPoint.y > edge.y * toPoint.x;

                // Flip the sign on each edge the ray crosses.
                if ((aboveStart && belowEnd && leftOfEdge) || (!aboveStart && !belowEnd && !leftOfEdge))
                    sign = -sign;
            }

            return sign * Mathf.Sqrt(squared);
        }

        /// <summary>Returns a generated sprite, creating the whole set on first use.</summary>
        public static Sprite Get(string assetPath)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
            if (sprite != null) return sprite;

            RebuildAll();
            return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        }

        private static void EnsureFolder()
        {
            string[] parts = Folder.Split('/');
            string current = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        /// <summary>
        /// Renders one shape to a PNG and imports it as a 9-sliced sprite.
        /// </summary>
        /// <param name="thickness">Outline width in pixels (Outline and Glow only).</param>
        /// <param name="glowDistance">How far the glow falls off outside the shape, in pixels.</param>
        private static void Generate(string assetPath, int size, float radius, int border, Shape shape,
                                      float thickness, float glowDistance, bool sliced)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear
            };

            float half = size * 0.5f;
            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Pixel centre relative to the middle of the texture.
                    float px = x + 0.5f - half;
                    float py = y + 0.5f - half;

                    float distance = RoundedRectDistance(px, py, half, half, radius);
                    float alpha = shape switch
                    {
                        Shape.Fill => Mathf.Clamp01(0.5f - distance),
                        Shape.Outline => OutlineAlpha(distance, thickness),
                        _ => GlowAlpha(distance, thickness, glowDistance)
                    };

                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            // Application.dataPath ends in "/Assets"; assetPath starts with "Assets".
            string absolute = Application.dataPath + assetPath.Substring("Assets".Length);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            File.WriteAllBytes(absolute, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer) return;

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            if (sliced)
            {
                // 9-slice border: left, bottom, right, top.
                importer.spriteBorder = new Vector4(border, border, border, border);
            }

            importer.SaveAndReimport();
        }

        /// <summary>
        /// Signed distance from a point to a rounded rectangle: negative inside, positive outside, and
        /// measured in pixels, which is what makes a one-pixel anti-aliased edge trivial.
        /// </summary>
        private static float RoundedRectDistance(float px, float py, float halfWidth, float halfHeight,
                                                  float radius)
        {
            radius = Mathf.Min(radius, Mathf.Min(halfWidth, halfHeight));

            float qx = Mathf.Abs(px) - (halfWidth - radius);
            float qy = Mathf.Abs(py) - (halfHeight - radius);

            float outsideX = Mathf.Max(qx, 0f);
            float outsideY = Mathf.Max(qy, 0f);
            float outside = Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY);
            float inside = Mathf.Min(Mathf.Max(qx, qy), 0f);

            return outside + inside - radius;
        }

        /// <summary>A band of the given thickness sitting just inside the shape's edge.</summary>
        private static float OutlineAlpha(float distance, float thickness)
        {
            // Centre the band on the edge, then take a soft 1px falloff either side.
            float fromBandCentre = Mathf.Abs(distance + thickness * 0.5f) - thickness * 0.5f;
            return Mathf.Clamp01(0.5f - fromBandCentre);
        }

        /// <summary>Solid inside the outline band, falling off quadratically outward.</summary>
        private static float GlowAlpha(float distance, float thickness, float glowDistance)
        {
            if (glowDistance <= 0f) return OutlineAlpha(distance, thickness);

            if (distance <= 0f) return 1f;

            float t = Mathf.Clamp01(distance / glowDistance);
            float falloff = 1f - t;
            return falloff * falloff;
        }
    }
}
