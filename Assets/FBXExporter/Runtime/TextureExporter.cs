using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FbxExporter
{
    /// <summary>
    /// Turns Unity textures into image files (or embedded bytes) that DCC tools can read.
    /// Works at runtime on non-readable and compressed textures by rendering them through a RenderTexture.
    /// </summary>
    internal sealed class TextureExporter
    {
        internal sealed class Entry
        {
            public string AbsolutePath;
            public string RelativePath;
            public string FileName;
            public byte[] Content; // set when embedding
            public bool HasAlpha;
        }

        private static readonly HashSet<string> CopyableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tif", ".tiff" };

        private readonly FbxExportOptions _options;
        private readonly FbxExportResult _result;
        private readonly string _textureDirAbs;
        private readonly string _textureDirRel;
        private readonly Dictionary<(int, bool), Entry> _cache = new Dictionary<(int, bool), Entry>();
        private readonly HashSet<string> _usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TextureExporter(string fbxPath, FbxExportOptions options, FbxExportResult result)
        {
            _options = options;
            _result = result;
            string dir = Path.GetDirectoryName(Path.GetFullPath(fbxPath)) ?? "";
            string name = Path.GetFileNameWithoutExtension(fbxPath);
            string folder = (options.TextureFolder ?? "").Replace("{name}", name).Trim();
            _textureDirRel = folder;
            _textureDirAbs = string.IsNullOrEmpty(folder) ? dir : Path.Combine(dir, folder);
        }

        public Entry Get(Texture tex, bool isNormalMap)
        {
            if (tex == null) return null;
            var key = (tex.GetInstanceID(), isNormalMap);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            Entry entry = null;
            try
            {
                entry = Export(tex, isNormalMap);
            }
            catch (Exception e)
            {
                _result.Warnings.Add($"Texture '{tex.name}' could not be exported: {e.Message}");
            }
            _cache[key] = entry;
            return entry;
        }

        private Entry Export(Texture tex, bool isNormalMap)
        {
            if (tex.dimension != TextureDimension.Tex2D)
            {
                _result.Warnings.Add($"Texture '{tex.name}' is a {tex.dimension} texture; only 2D textures are exported.");
                return null;
            }

            byte[] bytes = null;
            string ext = ".png";
            bool hasAlpha = GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat) && !isNormalMap;

#if UNITY_EDITOR
            if (_options.PreferSourceTextureFiles && !_options.ForceRuntimeReadback)
            {
                string assetPath = UnityEditor.AssetDatabase.GetAssetPath(tex);
                string srcExt = string.IsNullOrEmpty(assetPath) ? "" : Path.GetExtension(assetPath);
                bool generatedNormal = isNormalMap &&
                                       UnityEditor.AssetImporter.GetAtPath(assetPath) is UnityEditor.TextureImporter ti &&
                                       ti.convertToNormalmap;
                if (CopyableExtensions.Contains(srcExt) && !generatedNormal && File.Exists(assetPath))
                {
                    bytes = File.ReadAllBytes(assetPath);
                    ext = srcExt.ToLowerInvariant();
                }
            }
#endif
            if (bytes == null)
            {
                Color32[] pixels = ReadPixels(tex, out int w, out int h);
                if (isNormalMap) DecodeNormalMap(pixels);
                var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
                try
                {
                    t.SetPixels32(pixels);
                    t.Apply(false);
                    bytes = t.EncodeToPNG();
                }
                finally
                {
                    DestroyObj(t);
                }
            }

            string baseName = Sanitize(string.IsNullOrEmpty(tex.name) ? "Texture" : tex.name);
            if (isNormalMap && ext == ".png" && !baseName.ToLowerInvariant().Contains("normal")) baseName += "_Normal";
            string fileName = baseName + ext;
            for (int i = 2; _usedNames.Contains(fileName); i++) fileName = baseName + "_" + i + ext;
            _usedNames.Add(fileName);

            var entry = new Entry
            {
                FileName = fileName,
                AbsolutePath = Path.Combine(_textureDirAbs, fileName),
                RelativePath = string.IsNullOrEmpty(_textureDirRel) ? fileName : _textureDirRel + "/" + fileName,
                HasAlpha = hasAlpha
            };

            if (_options.EmbedTextures && _options.Format == FbxFormat.Binary)
            {
                entry.Content = bytes;
            }
            else
            {
                Directory.CreateDirectory(_textureDirAbs);
                File.WriteAllBytes(entry.AbsolutePath, bytes);
                _result.TextureFiles.Add(entry.AbsolutePath);
            }
            return entry;
        }

        /// <summary>Reads the raw (as stored, i.e. sRGB-encoded for color textures) pixels of any 2D texture.</summary>
        private Color32[] ReadPixels(Texture tex, out int w, out int h)
        {
            w = tex.width;
            h = tex.height;

            if (!_options.ForceRuntimeReadback && tex is Texture2D t2 && t2.isReadable)
            {
                try
                {
                    return t2.GetPixels32(0);
                }
                catch (Exception)
                {
                    // Some compressed formats can't be decoded on the CPU; fall through to the GPU path.
                }
            }

            bool srgb = tex.isDataSRGB;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            var readback = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgb);
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readback.Apply(false);
                return readback.GetPixels32(0);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                DestroyObj(readback);
            }
        }

        /// <summary>
        /// Converts Unity's packed normal map encodings (DXT5nm "AG", BC5 "RG", or plain RGB) into a
        /// standard tangent-space RGB normal map, using the same rule as UnpackNormalmapRGorAG.
        /// </summary>
        private static void DecodeNormalMap(Color32[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                float x = (c.r / 255f) * (c.a / 255f) * 2f - 1f;
                float y = c.g / 255f * 2f - 1f;
                float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                px[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((y * 0.5f + 0.5f) * 255f), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f), 0, 255),
                    255);
            }
        }

        internal static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (char ch in s) sb.Append(Array.IndexOf(invalid, ch) >= 0 || ch == ' ' ? '_' : ch);
            return sb.ToString();
        }

        private static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
