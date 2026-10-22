using System.Collections.Generic;

namespace FbxExporter.Fbx
{
    /// <summary>
    /// An FBX object name such as "Model::Hips". Binary files store it as "Hips\0\x01Model",
    /// ASCII files as "Model::Hips".
    /// </summary>
    public readonly struct FbxObjectName
    {
        public readonly string Name;
        public readonly string Class;

        public FbxObjectName(string name, string cls)
        {
            Name = name ?? string.Empty;
            Class = cls ?? string.Empty;
        }
    }

    /// <summary>A single-byte char property (binary type 'C'), e.g. "Shading: Y".</summary>
    public readonly struct FbxChar
    {
        public readonly char Value;
        public FbxChar(char value) { Value = value; }
    }

    /// <summary>
    /// One record of the FBX document tree. Property values may be:
    /// bool, FbxChar, short, int, long, float, double, string, FbxObjectName, byte[] (raw),
    /// int[], long[], float[], double[], bool[].
    /// </summary>
    public sealed class FbxNode
    {
        public readonly string Name;
        public readonly List<object> Properties = new List<object>();
        public readonly List<FbxNode> Children = new List<FbxNode>();

        public FbxNode(string name, params object[] props)
        {
            Name = name;
            if (props != null) Properties.AddRange(props);
        }

        /// <summary>Adds a child node and returns it.</summary>
        public FbxNode Add(string name, params object[] props)
        {
            var node = new FbxNode(name, props);
            Children.Add(node);
            return node;
        }

        public FbxNode Add(FbxNode child)
        {
            Children.Add(child);
            return child;
        }

        /// <summary>Adds a child carrying a single array property (arrays must not be expanded by params).</summary>
        public FbxNode AddArray(string name, object array)
        {
            var node = new FbxNode(name);
            node.Properties.Add(array);
            Children.Add(node);
            return node;
        }
    }

    /// <summary>Helper for writing a "Properties70" block.</summary>
    public sealed class FbxProps70
    {
        private readonly FbxNode _node;

        public FbxProps70(FbxNode parent)
        {
            _node = parent.Add("Properties70");
        }

        public bool IsEmpty => _node.Children.Count == 0;

        private FbxNode P(string name, string type, string label, string flags)
        {
            return _node.Add("P", name, type, label, flags);
        }

        public void Int(string name, int v) => P(name, "int", "Integer", "").Properties.Add(v);
        public void Bool(string name, bool v) => P(name, "bool", "", "").Properties.Add(v ? 1 : 0);
        public void Enum(string name, int v) => P(name, "enum", "", "").Properties.Add(v);
        public void Double(string name, double v) => P(name, "double", "Number", "").Properties.Add(v);
        public void Number(string name, double v) => P(name, "Number", "", "A").Properties.Add(v);
        public void String(string name, string v) => P(name, "KString", "", "").Properties.Add(v ?? "");
        public void Url(string name, string v) => P(name, "KString", "XRefUrl", "").Properties.Add(v ?? "");
        public void DocUrl(string name, string v) => P(name, "KString", "Url", "").Properties.Add(v ?? "");
        public void DateTime(string name, string v) => P(name, "DateTime", "", "").Properties.Add(v ?? "");
        public void Time(string name, long v) => P(name, "KTime", "Time", "").Properties.Add(v);
        public void Compound(string name) => P(name, "Compound", "", "");
        public void Object(string name) => P(name, "object", "", "");

        public void Color(string name, double r, double g, double b) => Add3(P(name, "Color", "", "A"), r, g, b);
        public void ColorRGB(string name, double r, double g, double b) => Add3(P(name, "ColorRGB", "Color", ""), r, g, b);
        public void Vector(string name, double x, double y, double z) => Add3(P(name, "Vector", "", "A"), x, y, z);
        public void Vector3D(string name, double x, double y, double z) => Add3(P(name, "Vector3D", "Vector", ""), x, y, z);
        public void Lcl(string name, double x, double y, double z) => Add3(P(name, name, "", "A"), x, y, z);
        public void Visibility(double v) => P("Visibility", "Visibility", "", "A").Properties.Add(v);

        private static void Add3(FbxNode p, double x, double y, double z)
        {
            p.Properties.Add(x);
            p.Properties.Add(y);
            p.Properties.Add(z);
        }
    }
}
