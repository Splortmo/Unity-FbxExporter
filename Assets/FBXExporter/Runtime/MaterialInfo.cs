using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FbxExporter
{
    /// <summary>
    /// Pulls the common PBR inputs out of a Unity material. Knows the property names of the
    /// Built-in Standard, URP Lit/Simple Lit/Unlit, HDRP Lit and most custom/toon shaders.
    /// </summary>
    internal sealed class MaterialInfo
    {
        public Color BaseColor = Color.white;
        public Color Emission = Color.black;
        public float Metallic;
        public float Smoothness = 0.5f;
        public float NormalScale = 1f;
        public bool UsesAlpha;

        public Texture MainTex;
        public Vector2 MainScale = Vector2.one, MainOffset;
        public Texture NormalTex;
        public Vector2 NormalTexScale = Vector2.one, NormalTexOffset;
        public Texture EmissionTex;
        public Vector2 EmissionScale = Vector2.one, EmissionOffset;

        private static readonly string[] ColorProps = { "_BaseColor", "_Color", "_MainColor", "_TintColor", "_BaseColorFactor" };
        private static readonly string[] MainTexProps = { "_BaseMap", "_MainTex", "_BaseColorMap", "_AlbedoMap", "_Albedo", "_DiffuseMap", "_Diffuse", "_BaseColorTexture" };
        private static readonly string[] NormalTexProps = { "_BumpMap", "_NormalMap", "_NormalTex", "_NormalTexture", "_Normal" };
        private static readonly string[] EmissionColorProps = { "_EmissionColor", "_EmissiveColor" };
        private static readonly string[] EmissionTexProps = { "_EmissionMap", "_EmissiveColorMap", "_EmissiveTex", "_EmissiveTexture" };
        private static readonly string[] NonColorHints =
            { "normal", "bump", "mask", "metal", "occlusion", "detail", "emiss", "height", "parallax", "spec", "rough", "smooth", "light", "noise", "ramp", "matcap", "shadow", "cube", "reflect", "dissolve", "flow" };

        public static MaterialInfo From(Material m)
        {
            var info = new MaterialInfo();

            foreach (var p in ColorProps)
                if (m.HasProperty(p)) { info.BaseColor = m.GetColor(p); break; }

            string mainProp = FindTexture(m, MainTexProps);
            if (mainProp == null)
            {
                // Shader-declared [MainTexture], then any texture slot that looks like color.
                try
                {
                    if (m.mainTexture != null)
                    {
                        info.MainTex = m.mainTexture;
                        info.MainScale = m.mainTextureScale;
                        info.MainOffset = m.mainTextureOffset;
                    }
                }
                catch (Exception)
                {
                    // Shader has no main texture property.
                }
                if (info.MainTex == null) mainProp = FindColorLikeTexture(m);
            }
            if (mainProp != null)
            {
                info.MainTex = m.GetTexture(mainProp);
                info.MainScale = m.GetTextureScale(mainProp);
                info.MainOffset = m.GetTextureOffset(mainProp);
            }

            string normalProp = FindTexture(m, NormalTexProps);
            if (normalProp != null)
            {
                info.NormalTex = m.GetTexture(normalProp);
                info.NormalTexScale = m.GetTextureScale(normalProp);
                info.NormalTexOffset = m.GetTextureOffset(normalProp);
                if (m.HasProperty("_BumpScale")) info.NormalScale = m.GetFloat("_BumpScale");
            }

            bool emissionKeywordExists = m.shader != null && m.shader.keywordSpace.FindKeyword("_EMISSION").isValid;
            bool emissionOn = !emissionKeywordExists || m.IsKeywordEnabled("_EMISSION");
            if (emissionOn)
            {
                foreach (var p in EmissionColorProps)
                    if (m.HasProperty(p)) { info.Emission = m.GetColor(p); break; }
                string emProp = FindTexture(m, EmissionTexProps);
                if (emProp != null && info.Emission.maxColorComponent > 0f)
                {
                    info.EmissionTex = m.GetTexture(emProp);
                    info.EmissionScale = m.GetTextureScale(emProp);
                    info.EmissionOffset = m.GetTextureOffset(emProp);
                }
            }
            // FBX colors are LDR; fold HDR intensity into the 0..1 range.
            float emMax = info.Emission.maxColorComponent;
            if (emMax > 1f) info.Emission /= emMax;
            info.Emission.a = 1f;

            if (m.HasProperty("_Metallic")) info.Metallic = m.GetFloat("_Metallic");
            if (m.HasProperty("_Smoothness")) info.Smoothness = m.GetFloat("_Smoothness");
            else if (m.HasProperty("_Glossiness")) info.Smoothness = m.GetFloat("_Glossiness");

            info.UsesAlpha = m.renderQueue >= (int)RenderQueue.AlphaTest
                             || m.IsKeywordEnabled("_ALPHATEST_ON")
                             || m.IsKeywordEnabled("_ALPHABLEND_ON")
                             || m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")
                             || (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f)
                             || (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f);
            if (!info.UsesAlpha) info.BaseColor.a = 1f;
            return info;
        }

        private static string FindTexture(Material m, string[] names)
        {
            foreach (var p in names)
                if (m.HasProperty(p) && m.GetTexture(p) != null)
                    return p;
            return null;
        }

        private static string FindColorLikeTexture(Material m)
        {
            var shader = m.shader;
            if (shader == null) return null;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue;
                string name = shader.GetPropertyName(i);
                string lower = name.ToLowerInvariant();
                bool skip = false;
                foreach (var h in NonColorHints)
                    if (lower.Contains(h)) { skip = true; break; }
                if (skip || m.GetTexture(name) == null) continue;
                return name;
            }
            return null;
        }
    }
}
