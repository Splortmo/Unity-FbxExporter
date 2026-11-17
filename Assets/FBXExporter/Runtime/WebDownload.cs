using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FbxExporter
{
    /// <summary>
    /// Hands files to the user as browser downloads on WebGL, where files written by the app only live in the
    /// browser's virtual file system. Several files are packed into one zip that keeps the relative folder layout,
    /// so extracting it gives the .fbx next to its texture folder.
    /// </summary>
    internal static class WebDownload
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void FbxExporter_WebDownload(string fileName, byte[] data, int length, string mime);
#endif

        public static void Download(string fileName, byte[] data, string mime)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            FbxExporter_WebDownload(fileName, data, data.Length, mime);
#else
            throw new PlatformNotSupportedException("Browser downloads are only available in WebGL player builds.");
#endif
        }

        /// <summary>A single file is downloaded as itself, several as one zip. Entry names are relative to baseDir.</summary>
        public static void DownloadFiles(IList<string> files, string baseDir, string zipName)
        {
            if (files.Count == 1)
            {
                Download(Path.GetFileName(files[0]), File.ReadAllBytes(files[0]), "application/octet-stream");
                return;
            }
            using (var stream = new MemoryStream())
            {
                using (var zip = new StoreZipWriter(stream))
                {
                    foreach (string file in files)
                        zip.Add(RelativePath(baseDir, file), File.ReadAllBytes(file));
                }
                Download(zipName, stream.ToArray(), "application/zip");
            }
        }

        private static string RelativePath(string baseDir, string file)
        {
            string root = Path.GetFullPath(baseDir).Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(file).Replace('\\', '/');
            return full.StartsWith(root, StringComparison.Ordinal) ? full.Substring(root.Length) : Path.GetFileName(file);
        }
    }

    /// <summary>
    /// Minimal zip writer that stores entries uncompressed. Avoids System.IO.Compression, whose deflate needs
    /// native zlib that WebGL builds don't have. Images are already compressed, so little is lost.
    /// </summary>
    internal sealed class StoreZipWriter : IDisposable
    {
        private readonly Stream _output;
        private readonly List<(byte[] name, uint crc, uint size, uint offset)> _entries = new List<(byte[], uint, uint, uint)>();
        private readonly ushort _dosTime, _dosDate;
        private bool _finished;

        public StoreZipWriter(Stream output)
        {
            _output = output;
            var now = DateTime.Now;
            _dosTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
            _dosDate = (ushort)(((Math.Max(now.Year, 1980) - 1980) << 9) | (now.Month << 5) | now.Day);
        }

        public void Add(string entryName, byte[] data)
        {
            if (_finished) throw new InvalidOperationException("Zip already finished");
            byte[] name = Encoding.UTF8.GetBytes(entryName.Replace('\\', '/'));
            uint crc = Crc32(data);
            uint offset = (uint)_output.Position;
            var w = new BinaryWriter(_output, Encoding.UTF8, true);
            w.Write(0x04034b50u);       // local file header
            w.Write((ushort)20);        // version needed
            w.Write((ushort)0x0800);    // flags: UTF-8 names
            w.Write((ushort)0);         // method: stored
            w.Write(_dosTime);
            w.Write(_dosDate);
            w.Write(crc);
            w.Write((uint)data.Length); // compressed size
            w.Write((uint)data.Length); // uncompressed size
            w.Write((ushort)name.Length);
            w.Write((ushort)0);         // extra length
            w.Write(name);
            w.Write(data);
            w.Flush();
            _entries.Add((name, crc, (uint)data.Length, offset));
        }

        public void Dispose()
        {
            if (_finished) return;
            _finished = true;
            var w = new BinaryWriter(_output, Encoding.UTF8, true);
            uint dirStart = (uint)_output.Position;
            foreach (var e in _entries)
            {
                w.Write(0x02014b50u);   // central directory header
                w.Write((ushort)20);
                w.Write((ushort)20);
                w.Write((ushort)0x0800);
                w.Write((ushort)0);
                w.Write(_dosTime);
                w.Write(_dosDate);
                w.Write(e.crc);
                w.Write(e.size);
                w.Write(e.size);
                w.Write((ushort)e.name.Length);
                w.Write((ushort)0);     // extra
                w.Write((ushort)0);     // comment
                w.Write((ushort)0);     // disk number
                w.Write((ushort)0);     // internal attributes
                w.Write(0u);            // external attributes
                w.Write(e.offset);
                w.Write(e.name);
            }
            uint dirSize = (uint)_output.Position - dirStart;
            w.Write(0x06054b50u);       // end of central directory
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)_entries.Count);
            w.Write((ushort)_entries.Count);
            w.Write(dirSize);
            w.Write(dirStart);
            w.Write((ushort)0);
            w.Flush();
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] data)
        {
            if (_crcTable == null)
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
                _crcTable = table;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
