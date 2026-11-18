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
    /// The same call works on every platform. In WebGL player builds the output is handed to the user as a
    /// browser download instead of being written to a folder.
    /// Must be called from the main thread (it reads meshes/textures through Unity APIs).
    /// </summary>
    public static class FbxExport
    {
        public static FbxExportResult Export(string path, params GameObject[] objects)
        {
            return Export(path, objects, null);
        }

        /// <summary>
        /// Exports <paramref name="objects"/> (each with its children) to an FBX file.
        /// <para>
        /// Editor and desktop/mobile players: writes the .fbx (plus its texture folder) to <paramref name="path"/>.
        /// The Editor always does this, also with the build target set to WebGL.
        /// </para>
        /// <para>
        /// WebGL player builds: a page can't write into a folder, so the export is written to the browser's temporary
        /// file system and then downloaded. Only the file name of <paramref name="path"/> is used. With separate texture
        /// files the download is a zip holding the .fbx and its texture folder (extracting it anywhere keeps the texture
        /// links working); otherwise it is the plain .fbx. <see cref="FbxExportResult.FilePath"/> is the downloaded file
        /// name and <see cref="FbxExportResult.TextureFiles"/> is empty, since the temporary files are deleted afterwards.
        /// </para>
        /// </summary>
        public static FbxExportResult Export(string path, IEnumerable<GameObject> objects, FbxExportOptions options)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return ExportAndDownload(path, objects, options);
#else
            return ExportToFile(path, objects, options);
#endif
        }

        /// <summary>Shared export used on every platform: builds the FBX document and writes it (and texture files) to disk.</summary>
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

#if UNITY_WEBGL && !UNITY_EDITOR
        /// <summary>WebGL player: the shared export into a temporary folder, followed by a browser download of the output.</summary>
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
                result.TextureFiles.Clear(); // they lived in the temporary folder, which is deleted below
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
                // The download already copied the bytes out; free the virtual file system.
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
