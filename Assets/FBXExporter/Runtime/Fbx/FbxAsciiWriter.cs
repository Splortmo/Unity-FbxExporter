using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FbxExporter.Fbx
{
    /// <summary>
    /// Writes an FBX node tree as an ASCII FBX 7.4 file.
    /// Note: the Autodesk FBX SDK (3ds Max, Maya, Unity) reads ASCII FBX, but Blender's importer does NOT.
    /// </summary>
    internal static class FbxAsciiWriter
    {
        private const int ValuesPerLine = 64;

        public static void Write(Stream stream, IList<FbxNode> topLevel, string creator)
        {
            using (var w = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, true))
            {
                w.NewLine = "\n";
                w.WriteLine("; FBX 7.4.0 project file");
                w.WriteLine("; Created by " + (creator ?? "FbxExporter"));
                w.WriteLine("; ----------------------------------------------------");
                w.WriteLine();
                foreach (var node in topLevel)
                {
                    // Binary-only records.
                    if (node.Name == "FileId") continue;
                    WriteNode(w, node, 0);
                    w.WriteLine();
                }
            }
        }

        private static void WriteNode(StreamWriter w, FbxNode node, int depth)
        {
            string indent = new string('\t', depth);
            w.Write(indent);
            w.Write(node.Name);
            w.Write(": ");

            // Array record: "Name: *N {\n a: ...\n}"
            if (node.Properties.Count == 1 && node.Properties[0] is Array arr && !(arr is byte[]))
            {
                w.Write('*');
                w.Write(arr.Length.ToString(CultureInfo.InvariantCulture));
                w.WriteLine(" {");
                w.Write(indent);
                w.Write("\ta: ");
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0)
                    {
                        w.Write(',');
                        if (i % ValuesPerLine == 0) w.Write('\n');
                    }
                    w.Write(FormatScalar(arr.GetValue(i)));
                }
                w.WriteLine();
                w.Write(indent);
                w.WriteLine("}");
                return;
            }

            for (int i = 0; i < node.Properties.Count; i++)
            {
                if (i > 0) w.Write(", ");
                w.Write(FormatProperty(node.Properties[i]));
            }

            if (node.Children.Count > 0 || node.Properties.Count == 0)
            {
                w.WriteLine(" {");
                foreach (var c in node.Children) WriteNode(w, c, depth + 1);
                w.Write(indent);
                w.WriteLine("}");
            }
            else
            {
                w.WriteLine();
            }
        }

        private static string FormatProperty(object p)
        {
            switch (p)
            {
                case string s: return Quote(s);
                case FbxObjectName on: return Quote(on.Class + "::" + on.Name);
                case FbxChar c: return c.Value.ToString();
                case bool b: return b ? "T" : "F";
                case byte[] raw: return Quote(Convert.ToBase64String(raw));
                default: return FormatScalar(p);
            }
        }

        private static string FormatScalar(object v)
        {
            switch (v)
            {
                case double d: return FormatDouble(d);
                case float f: return FormatDouble(f);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case short s: return s.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "1" : "0";
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        private static string FormatDouble(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char ch in s)
            {
                if (ch == '"') sb.Append("&quot;");
                else if (ch == '\n' || ch == '\r') sb.Append(' ');
                else sb.Append(ch);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
