using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FbxExporter.Fbx
{
    /// <summary>
    /// Writes an FBX node tree as a binary FBX file (version 7.4, 32-bit offsets).
    /// This is the format read by Blender and by the Autodesk FBX SDK (3ds Max, Maya, ...).
    /// </summary>
    internal static class FbxBinaryWriter
    {
        public const int Version = 7400;

        // Fixed header/footer values. The FBX SDK validates the footer against FileId/CreationTime,
        // so these must be written as a matching set (same values Blender's exporter uses).
        public static readonly byte[] FileId =
            { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 };
        public const string CreationTime = "1970-01-01 10:00:00:000";

        private static readonly byte[] FootId =
            { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };
        private static readonly byte[] FootMagic =
            { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };

        private const int SentinelLength = 13; // 3 x uint32 + 1 byte name length
        private const int CompressThreshold = 128; // arrays larger than this many bytes are zlib-compressed

        public static void Write(Stream stream, IList<FbxNode> topLevel)
        {
            if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable", nameof(stream));

            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                w.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  "));
                w.Write((byte)0x00);
                w.Write((byte)0x1a);
                w.Write((byte)0x00);
                w.Write((uint)Version);

                foreach (var node in topLevel) WriteNode(w, node);
                w.Write(new byte[SentinelLength]);

                // Footer
                w.Write(FootId);
                w.Write(new byte[4]);
                long ofs = w.BaseStream.Position;
                int pad = (int)(((ofs + 15) & ~15L) - ofs);
                if (pad == 0) pad = 16;
                w.Write(new byte[pad]);
                w.Write((uint)Version);
                w.Write(new byte[120]);
                w.Write(FootMagic);
            }
        }

        private static void WriteNode(BinaryWriter w, FbxNode node)
        {
            var s = w.BaseStream;
            long start = s.Position;
            w.Write(0u); // end offset (patched below)
            w.Write((uint)node.Properties.Count);
            w.Write(0u); // property list length (patched below)

            byte[] name = Encoding.ASCII.GetBytes(node.Name);
            if (name.Length > 255) throw new InvalidDataException("FBX node name too long: " + node.Name);
            w.Write((byte)name.Length);
            w.Write(name);

            long propStart = s.Position;
            foreach (var p in node.Properties) WriteProperty(w, p);
            long propEnd = s.Position;

            if (node.Children.Count > 0)
            {
                foreach (var c in node.Children) WriteNode(w, c);
                w.Write(new byte[SentinelLength]);
            }
            else if (node.Properties.Count == 0)
            {
                w.Write(new byte[SentinelLength]);
            }

            long end = s.Position;
            if (end > uint.MaxValue) throw new InvalidDataException("FBX 7.4 binary files are limited to 4 GB");
            s.Position = start;
            w.Write((uint)end);
            s.Position = start + 8;
            w.Write((uint)(propEnd - propStart));
            s.Position = end;
        }

        private static void WriteProperty(BinaryWriter w, object p)
        {
            switch (p)
            {
                case bool b:
                    w.Write((byte)'C'); w.Write((byte)(b ? 1 : 0)); break;
                case FbxChar c:
                    w.Write((byte)'C'); w.Write((byte)c.Value); break;
                case short sh:
                    w.Write((byte)'Y'); w.Write(sh); break;
                case int i:
                    w.Write((byte)'I'); w.Write(i); break;
                case long l:
                    w.Write((byte)'L'); w.Write(l); break;
                case float f:
                    w.Write((byte)'F'); w.Write(f); break;
                case double d:
                    w.Write((byte)'D'); w.Write(d); break;
                case string str:
                    WriteString(w, Encoding.UTF8.GetBytes(str)); break;
                case FbxObjectName on:
                {
                    var a = Encoding.UTF8.GetBytes(on.Name);
                    var b2 = Encoding.UTF8.GetBytes(on.Class);
                    var buf = new byte[a.Length + 2 + b2.Length];
                    Buffer.BlockCopy(a, 0, buf, 0, a.Length);
                    buf[a.Length] = 0x00;
                    buf[a.Length + 1] = 0x01;
                    Buffer.BlockCopy(b2, 0, buf, a.Length + 2, b2.Length);
                    WriteString(w, buf);
                    break;
                }
                case byte[] raw:
                    w.Write((byte)'R'); w.Write((uint)raw.Length); w.Write(raw); break;
                case int[] ia:
                    WriteArray(w, 'i', ia, ia.Length, 4); break;
                case long[] la:
                    WriteArray(w, 'l', la, la.Length, 8); break;
                case float[] fa:
                    WriteArray(w, 'f', fa, fa.Length, 4); break;
                case double[] da:
                    WriteArray(w, 'd', da, da.Length, 8); break;
                case bool[] ba:
                {
                    var bytes = new byte[ba.Length];
                    for (int k = 0; k < ba.Length; k++) bytes[k] = (byte)(ba[k] ? 1 : 0);
                    WriteArrayBytes(w, 'b', bytes, ba.Length);
                    break;
                }
                default:
                    throw new InvalidDataException("Unsupported FBX property type: " + (p == null ? "null" : p.GetType().Name));
            }
        }

        private static void WriteString(BinaryWriter w, byte[] bytes)
        {
            w.Write((byte)'S');
            w.Write((uint)bytes.Length);
            w.Write(bytes);
        }

        private static void WriteArray(BinaryWriter w, char type, Array data, int count, int elemSize)
        {
            var bytes = new byte[count * elemSize];
            if (bytes.Length > 0) Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < bytes.Length; i += elemSize) Array.Reverse(bytes, i, elemSize);
            }
            WriteArrayBytes(w, type, bytes, count);
        }

        private static void WriteArrayBytes(BinaryWriter w, char type, byte[] bytes, int count)
        {
            w.Write((byte)type);
            w.Write((uint)count);
            if (bytes.Length <= CompressThreshold)
            {
                w.Write(0u);
                w.Write((uint)bytes.Length);
                w.Write(bytes);
            }
            else
            {
                byte[] z = ZlibCompress(bytes);
                w.Write(1u);
                w.Write((uint)z.Length);
                w.Write(z);
            }
        }

        /// <summary>zlib (RFC 1950) wrapper around raw deflate data.</summary>
        internal static byte[] ZlibCompress(byte[] data)
        {
            using (var ms = new MemoryStream(data.Length / 2 + 16))
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x01);
                using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, true))
                {
                    ds.Write(data, 0, data.Length);
                }
                uint adler = Adler32(data);
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        private static uint Adler32(byte[] data)
        {
            const uint Mod = 65521;
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                int n = Math.Min(data.Length - i, 5552);
                for (int k = 0; k < n; k++)
                {
                    a += data[i++];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
            }
            return (b << 16) | a;
        }
    }
}
