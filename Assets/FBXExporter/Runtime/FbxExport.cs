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

        /// <summary>
        /// WebGL: exports into the browser's temporary file system and hands the result to the user as a
        /// browser download. With textures that is a zip (the .fbx plus its texture folder, so extracting it into any
        /// folder keeps the texture links working); without textures it is the plain .fbx.
        /// Browsers don't let a page write into an arbitrary folder, so the user picks the folder when saving/extracting.
        /// In the Editor (e.g. build target set to WebGL) it opens a save dialog and writes the .fbx and textures to the chosen folder instead.
        /// Elsewhere the result fails with a NotSupportedException.
        /// </summary>
        /// <param name="fileName">Download name, e.g. "character" or "character.fbx".</param>
        public static FbxExportResult ExportAndDownload(string fileName, IEnumerable<GameObject> objects, FbxExportOptions options = null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string name = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(fileName) ? "export" : fileName);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (string.IsNullOrWhiteSpace(name)) name = "export";

            string dir = Path.Combine(Application.temporaryCachePath, "fbx_" + Guid.NewGuid().ToString("N"));
            FbxExportResult result = null;
            try
            {
                result = Export(Path.Combine(dir, name + ".fbx"), objects, options);
                if (!result.Success) return result;

                var files = new List<string> { result.FilePath };
                files.AddRange(result.TextureFiles);
                WebDownload.DownloadFiles(files, dir, name + ".zip");
            }
            catch (Exception e)
            {
                if (result == null) result = new FbxExportResult();
                result.Success = false;
                result.Error = e;
            }
            finally
            {
                // The download already copied the bytes out; free the virtual file system.
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception) { /* best effort */ }
            }
            return result;
#elif UNITY_EDITOR
            // Editor (e.g. with the build target set to WebGL): no browser, so save to disk instead.
            string defaultName = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(fileName) ? "export" : fileName);
            string path = UnityEditor.EditorUtility.SaveFilePanel("Export FBX", "", defaultName + ".fbx", "fbx");
            if (string.IsNullOrEmpty(path))
                return new FbxExportResult { Success = false, Error = new OperationCanceledException("Export cancelled.") };
            return Export(path, objects, options);
#else
            return new FbxExportResult
            {
                Success = false,
                Error = new NotSupportedException("ExportAndDownload is only available in WebGL player builds and the Editor. Use Export() with a file path.")
            };
#endif
        }

        private static void TryDelete(string file)
        {
            try { if (!string.IsNullOrEmpty(file) && File.Exists(file)) File.Delete(file); }
            catch (Exception) { /* best effort */ }
        }
    }
}
