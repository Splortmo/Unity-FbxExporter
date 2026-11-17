using System;
using System.Collections.Generic;
using System.IO;
using FbxExporter.Fbx;
using UnityEngine;

namespace FbxExporter
{
    /// <summary>
    /// Runtime FBX exporter. Exports static (MeshRenderer) and skinned (SkinnedMeshRenderer) meshes with their
    /// hierarchy, materials, textures, skeletons, skin weights and blend shapes.
    ///
    /// Usage:
    /// <code>
    /// var result = FbxExport.Export("C:/out/character.fbx", new[] { myGameObject });
    /// if (!result.Success) Debug.LogError(result.Error);
    /// </code>
    /// Must be called from the main thread (it reads meshes/textures through Unity APIs).
    /// </summary>
    public static class FbxExport
    {
        public static FbxExportResult Export(string path, params GameObject[] objects)
        {
            return Export(path, objects, null);
        }

        public static FbxExportResult Export(string path, IEnumerable<GameObject> objects, FbxExportOptions options)
        {
            options = options ?? new FbxExportOptions();
            var result = new FbxExportResult();
            string tmp = null;
            try
            {
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("Output path is empty", nameof(path));
                if (objects == null) throw new ArgumentNullException(nameof(objects));
                if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) path += ".fbx";
                path = Path.GetFullPath(path);
                result.FilePath = path;

                if (options.Format == FbxFormat.Ascii && options.EmbedTextures)
                    result.Warnings.Add("Texture embedding is only supported for binary FBX; textures were written as separate files.");

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var builder = new FbxSceneBuilder(path, options, result);
                List<FbxNode> document = builder.Build(objects, path);

                // Write to a temp file first so a failed export never leaves a truncated .fbx behind.
                tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                {
                    if (options.Format == FbxFormat.Binary) FbxBinaryWriter.Write(fs, document);
                    else FbxAsciiWriter.Write(fs, document, "Unity Runtime FBX Exporter");
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                tmp = null;

                result.Success = true;
            }
            catch (Exception e)
            {
                result.Success = false;
                result.Error = e;
                // Don't leave partial output behind.
                TryDelete(tmp);
                foreach (var f in result.TextureFiles) TryDelete(f);
                result.TextureFiles.Clear();
            }
            return result;
        }

        private static void TryDelete(string file)
        {
            try { if (!string.IsNullOrEmpty(file) && File.Exists(file)) File.Delete(file); }
            catch (Exception) { /* best effort */ }
        }
    }
}
