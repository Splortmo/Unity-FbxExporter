using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FbxExporter
{
    /// <summary>CPU-side copy of everything the exporter needs from a Mesh.</summary>
    internal sealed class MeshData
    {
        public sealed class SubMesh
        {
            public MeshTopology Topology;
            public int[] Indices; // base vertex already applied
        }

        public sealed class BlendShape
        {
            public string Name;
            public float FrameWeight;
            public Vector3[] DeltaVertices;
            public Vector3[] DeltaNormals;
        }

        public string Name;
        public int VertexCount;
        public Vector3[] Positions;
        public Vector3[] Normals;              // may be null
        public Color[] Colors;                 // may be null
        public Vector2[][] UVs = new Vector2[8][]; // per channel, null if absent
        public readonly List<SubMesh> SubMeshes = new List<SubMesh>();
        public byte[] BonesPerVertex;          // may be null
        public BoneWeight1[] Weights;          // may be null
        public Matrix4x4[] BindPoses;          // may be null
        public readonly List<BlendShape> BlendShapes = new List<BlendShape>();

        public bool HasSkin => BonesPerVertex != null && Weights != null && BonesPerVertex.Length == VertexCount && Weights.Length > 0;

        public static MeshData Read(Mesh mesh, bool forceGpuReadback, List<string> warnings)
        {
            if (mesh == null) return null;
            bool cpu = !forceGpuReadback && (mesh.isReadable || Application.isEditor);
            if (cpu)
            {
                try
                {
                    return ReadCpu(mesh);
                }
                catch (Exception e)
                {
                    warnings.Add($"Mesh '{mesh.name}': CPU read failed ({e.Message}), trying GPU readback.");
                }
            }

            try
            {
                var data = ReadGpu(mesh, warnings);
                if (data != null && !forceGpuReadback)
                    warnings.Add($"Mesh '{mesh.name}' is not Read/Write enabled; geometry was read back from the GPU (blend shapes unavailable).");
                return data;
            }
            catch (Exception e)
            {
                warnings.Add($"Mesh '{mesh.name}' could not be read ({e.Message}). Enable Read/Write in its import settings.");
                return null;
            }
        }

        // ---------------------------------------------------------------- CPU path

        private static MeshData ReadCpu(Mesh mesh)
        {
            var d = new MeshData { Name = mesh.name, VertexCount = mesh.vertexCount };
            d.Positions = mesh.vertices;
            if (d.Positions.Length != d.VertexCount) throw new Exception("vertex data not accessible");

            var n = mesh.normals;
            if (n != null && n.Length == d.VertexCount) d.Normals = n;
            var c = mesh.colors;
            if (c != null && c.Length == d.VertexCount) d.Colors = c;

            var uvList = new List<Vector2>();
            for (int ch = 0; ch < 8; ch++)
            {
                if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + ch)) continue;
                uvList.Clear();
                mesh.GetUVs(ch, uvList);
                if (uvList.Count == d.VertexCount) d.UVs[ch] = uvList.ToArray();
            }

            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var desc = mesh.GetSubMesh(i);
                d.SubMeshes.Add(new SubMesh { Topology = desc.topology, Indices = mesh.GetIndices(i, true) });
            }

            var bpv = mesh.GetBonesPerVertex();
            if (bpv.IsCreated && bpv.Length == d.VertexCount)
            {
                d.BonesPerVertex = bpv.ToArray();
                d.Weights = mesh.GetAllBoneWeights().ToArray();
            }
            var bp = mesh.bindposes;
            if (bp != null && bp.Length > 0) d.BindPoses = bp;

            int shapeCount = mesh.blendShapeCount;
            for (int s = 0; s < shapeCount; s++)
            {
                int frames = mesh.GetBlendShapeFrameCount(s);
                if (frames <= 0) continue;
                int f = frames - 1; // full-weight frame
                var dv = new Vector3[d.VertexCount];
                var dn = new Vector3[d.VertexCount];
                var dt = new Vector3[d.VertexCount];
                mesh.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                d.BlendShapes.Add(new BlendShape
                {
                    Name = mesh.GetBlendShapeName(s),
                    FrameWeight = mesh.GetBlendShapeFrameWeight(s, f),
                    DeltaVertices = dv,
                    DeltaNormals = dn
                });
            }
            return d;
        }

        // ---------------------------------------------------------------- GPU path

        private static MeshData ReadGpu(Mesh mesh, List<string> warnings)
        {
            var d = new MeshData { Name = mesh.name, VertexCount = mesh.vertexCount };
            int vc = d.VertexCount;

            mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;

            var streams = new byte[mesh.vertexBufferCount][];
            var strides = new int[mesh.vertexBufferCount];
            for (int s = 0; s < streams.Length; s++)
            {
                using (var buf = mesh.GetVertexBuffer(s))
                {
                    if (buf == null) continue;
                    strides[s] = buf.stride;
                    var bytes = new byte[buf.count * buf.stride];
                    buf.GetData(bytes);
                    streams[s] = bytes;
                }
            }

            float[] tmp = new float[4];
            Vector4[] Read(VertexAttribute attr, out int dim)
            {
                dim = 0;
                if (!mesh.HasVertexAttribute(attr)) return null;
                int stream = mesh.GetVertexAttributeStream(attr);
                if (stream < 0 || streams[stream] == null) return null;
                int offset = mesh.GetVertexAttributeOffset(attr);
                var fmt = mesh.GetVertexAttributeFormat(attr);
                dim = mesh.GetVertexAttributeDimension(attr);
                int compSize = FormatSize(fmt);
                var data = streams[stream];
                int stride = strides[stream];
                var result = new Vector4[vc];
                for (int v = 0; v < vc; v++)
                {
                    int baseOfs = v * stride + offset;
                    for (int k = 0; k < 4; k++) tmp[k] = 0f;
                    for (int k = 0; k < dim && k < 4; k++) tmp[k] = ReadComponent(data, baseOfs + k * compSize, fmt);
                    result[v] = new Vector4(tmp[0], tmp[1], tmp[2], tmp[3]);
                }
                return result;
            }

            var pos = Read(VertexAttribute.Position, out _);
            if (pos == null) throw new Exception("no position data");
            d.Positions = new Vector3[vc];
            for (int v = 0; v < vc; v++) d.Positions[v] = pos[v];

            var nrm = Read(VertexAttribute.Normal, out _);
            if (nrm != null)
            {
                d.Normals = new Vector3[vc];
                for (int v = 0; v < vc; v++) d.Normals[v] = nrm[v];
            }

            var col = Read(VertexAttribute.Color, out int colDim);
            if (col != null)
            {
                d.Colors = new Color[vc];
                for (int v = 0; v < vc; v++) d.Colors[v] = new Color(col[v].x, col[v].y, col[v].z, colDim >= 4 ? col[v].w : 1f);
            }

            for (int ch = 0; ch < 8; ch++)
            {
                var uv = Read(VertexAttribute.TexCoord0 + ch, out _);
                if (uv == null) continue;
                var arr = new Vector2[vc];
                for (int v = 0; v < vc; v++) arr[v] = uv[v];
                d.UVs[ch] = arr;
            }

            // Index buffer
            byte[] ib;
            using (var ibuf = mesh.GetIndexBuffer())
            {
                ib = new byte[ibuf.count * ibuf.stride];
                ibuf.GetData(ib);
            }
            bool is32 = mesh.indexFormat == IndexFormat.UInt32;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var desc = mesh.GetSubMesh(i);
                var idx = new int[desc.indexCount];
                for (int k = 0; k < idx.Length; k++)
                {
                    int at = desc.indexStart + k;
                    int raw = is32 ? BitConverter.ToInt32(ib, at * 4) : BitConverter.ToUInt16(ib, at * 2);
                    idx[k] = raw + desc.baseVertex;
                }
                d.SubMeshes.Add(new SubMesh { Topology = desc.topology, Indices = idx });
            }

            // Skin weights (up to 4 per vertex live in the vertex stream).
            var bw = Read(VertexAttribute.BlendWeight, out int bwDim);
            var bi = Read(VertexAttribute.BlendIndices, out int biDim);
            if (bi != null)
            {
                var bpvList = new byte[vc];
                var wList = new List<BoneWeight1>(vc * 4);
                var pair = new List<BoneWeight1>(4);
                for (int v = 0; v < vc; v++)
                {
                    pair.Clear();
                    for (int k = 0; k < biDim && k < 4; k++)
                    {
                        float w = bw != null ? (k < bwDim ? bw[v][k] : 0f) : (k == 0 ? 1f : 0f);
                        if (bw != null && bwDim == 1 && biDim == 1) w = 1f;
                        if (w <= 0f) continue;
                        pair.Add(new BoneWeight1 { boneIndex = Mathf.RoundToInt(bi[v][k]), weight = w });
                    }
                    pair.Sort((a, b) => b.weight.CompareTo(a.weight));
                    bpvList[v] = (byte)pair.Count;
                    wList.AddRange(pair);
                }
                d.BonesPerVertex = bpvList;
                d.Weights = wList.ToArray();
            }

            try
            {
                var bp = mesh.bindposes;
                if (bp != null && bp.Length > 0) d.BindPoses = bp;
            }
            catch (Exception)
            {
                // Bind poses not available for this mesh.
            }

            if (mesh.blendShapeCount > 0)
                warnings.Add($"Mesh '{mesh.name}': blend shapes are skipped because the mesh is not Read/Write enabled.");
            return d;
        }

        private static int FormatSize(VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16:
                    return 2;
                default:
                    return 1;
            }
        }

        private static float ReadComponent(byte[] d, int o, VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(d, o);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(d, o));
                case VertexAttributeFormat.UNorm8: return d[o] / 255f;
                case VertexAttributeFormat.SNorm8: return Mathf.Max((sbyte)d[o] / 127f, -1f);
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(d, o) / 65535f;
                case VertexAttributeFormat.SNorm16: return Mathf.Max(BitConverter.ToInt16(d, o) / 32767f, -1f);
                case VertexAttributeFormat.UInt8: return d[o];
                case VertexAttributeFormat.SInt8: return (sbyte)d[o];
                case VertexAttributeFormat.UInt16: return BitConverter.ToUInt16(d, o);
                case VertexAttributeFormat.SInt16: return BitConverter.ToInt16(d, o);
                case VertexAttributeFormat.UInt32: return BitConverter.ToUInt32(d, o);
                case VertexAttributeFormat.SInt32: return BitConverter.ToInt32(d, o);
                default: return 0f;
            }
        }
    }
}
