using System;
using System.Collections.Generic;
using System.IO;
using FbxExporter.Fbx;
using UnityEngine;

namespace FbxExporter
{
    /// <summary>
    /// Exports GameObject hierarchies (MeshRenderer and SkinnedMeshRenderer) to FBX, including materials,
    /// textures, skeletons, skin weights and blend shapes. Main thread only.
    /// <code>
    /// var result = FbxExport.Export("C:/out/character.fbx", new[] { myGameObject });
    /// if (!result.Success) Debug.LogError(result.Error);
    /// </code>
    /// </summary>
    public static class FbxExport
    {
        public static FbxExportResult Export(string path, params GameObject[] objects)
        {
            return Export(path, objects, null);
        }

        /// <summary>
        /// Exports <paramref name="objects"/> and their children to an FBX file.
        /// <para>Editor, desktop and mobile (including the Editor with a WebGL build target): writes the .fbx and its texture folder to <paramref name="path"/>.</para>
        /// <para>WebGL player: only the file name of <paramref name="path"/> is used. The result is downloaded through the browser as a zip (.fbx plus texture folder) or as a plain .fbx when there are no separate texture files.
        /// <see cref="FbxExportResult.FilePath"/> is the downloaded file name and <see cref="FbxExportResult.TextureFiles"/> is empty.</para>
        /// </summary>
        public static FbxExportResult Export(string path, IEnumerable<GameObject> objects, FbxExportOptions options)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return ExportAndDownload(path, objects, options);
#else
            return ExportToFile(path, objects, options);
#endif
        }

        /// <summary>Builds the FBX document and writes it and its texture files to disk.</summary>
        private static FbxExportResult ExportToFile(string path, IEnumerable<GameObject> objects, FbxExportOptions options)
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

                // Write to a temp file, then move it into place.
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
                // Remove partial output.
                TryDelete(tmp);
                foreach (var f in result.TextureFiles) TryDelete(f);
                result.TextureFiles.Clear();
            }
            return result;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>WebGL player: exports into a temporary folder, then downloads the output through the browser.</summary>
        private static FbxExportResult ExportAndDownload(string path, IEnumerable<GameObject> objects, FbxExportOptions options)
        {
            FbxExportResult result = null;
            string dir = null;
            try
            {
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("Output path is empty", nameof(path));
                string name = Path.GetFileName(path.Replace('\\', '/'));
                if (name.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                if (string.IsNullOrWhiteSpace(name)) name = "export";

                dir = Path.Combine(Application.temporaryCachePath, "fbx_" + Guid.NewGuid().ToString("N"));
                result = ExportToFile(Path.Combine(dir, name + ".fbx"), objects, options);
                if (!result.Success) return result;

                var files = new List<string> { result.FilePath };
                files.AddRange(result.TextureFiles);
                result.FilePath = WebDownload.DownloadFiles(files, dir, name + ".zip");
                result.TextureFiles.Clear(); // temporary files are deleted below
            }
            catch (Exception e)
            {
                if (result == null) result = new FbxExportResult();
                result.Success = false;
                result.Error = e;
                result.TextureFiles.Clear();
            }
            finally
            {
                // Free the temporary files in the browser's virtual file system.
                try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception) { /* best effort */ }
            }
            return result;
        }
#endif

        private static void TryDelete(string file)
        {
            try { if (!string.IsNullOrEmpty(file) && File.Exists(file)) File.Delete(file); }
            catch (Exception) { /* best effort */ }
        }
    }
}
