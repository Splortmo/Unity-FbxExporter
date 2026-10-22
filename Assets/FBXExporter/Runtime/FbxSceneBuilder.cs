using System;
using System.Collections.Generic;
using System.IO;
using FbxExporter.Fbx;
using UnityEngine;

namespace FbxExporter
{
    /// <summary>
    /// Converts a set of Unity GameObject hierarchies into an FBX node tree.
    ///
    /// Coordinate conversion: Unity is left-handed Y-up (meters); the FBX file is right-handed Y-up
    /// with UnitScaleFactor = 100 (1 unit = 1 m). Conversion mirrors the X axis:
    ///   position (x,y,z) -> (-x,y,z),  rotation (x,y,z,w) -> (x,-y,-z,w),  triangle winding reversed.
    /// </summary>
    internal sealed class FbxSceneBuilder
    {
        private const string Creator = "Unity Runtime FBX Exporter 1.0";

        private readonly FbxExportOptions _o;
        private readonly FbxExportResult _r;
        private readonly TextureExporter _textures;

        private long _nextId = 1000000;
        private readonly FbxNode _objects = new FbxNode("Objects");
        private readonly FbxNode _connections = new FbxNode("Connections");
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        private readonly List<Transform> _order = new List<Transform>();
        private readonly HashSet<Transform> _exported = new HashSet<Transform>();
        private readonly Dictionary<Transform, Transform> _rootOf = new Dictionary<Transform, Transform>();
        private readonly Dictionary<Transform, Matrix4x4> _basis = new Dictionary<Transform, Matrix4x4>();
        private readonly Dictionary<Transform, long> _modelIds = new Dictionary<Transform, long>();
        private readonly HashSet<Transform> _limbs = new HashSet<Transform>();

        // One bind (rest) matrix per bone, shared by every skinned mesh (see ComputeCanonicalBinds).
        private readonly Dictionary<Transform, Matrix4x4> _canonicalBind = new Dictionary<Transform, Matrix4x4>();

        private readonly Dictionary<Material, long> _materials = new Dictionary<Material, long>();
        private long _defaultMaterialId;
        private readonly Dictionary<string, long> _textureNodes = new Dictionary<string, long>();
        private readonly Dictionary<TextureExporter.Entry, long> _videos = new Dictionary<TextureExporter.Entry, long>();
        private readonly Dictionary<string, long> _sharedGeometry = new Dictionary<string, long>();

        private sealed class GeometryInfo
        {
            public long Id;
            public int[] VertexToControlPoint;
            public int ControlPointCount;
        }

        public FbxSceneBuilder(string fbxPath, FbxExportOptions options, FbxExportResult result)
        {
            _o = options;
            _r = result;
            _textures = new TextureExporter(fbxPath, options, result);
        }

        // ================================================================== entry point

        public List<FbxNode> Build(IEnumerable<GameObject> gameObjects, string fbxPath)
        {
            CollectHierarchy(gameObjects);
            if (_order.Count == 0) throw new InvalidOperationException("Nothing to export (no GameObjects selected, or all inactive).");

            FindLimbNodes();
            ComputeCanonicalBinds();

            foreach (var t in _order) _modelIds[t] = NewId();
            foreach (var t in _order) BuildNode(t);

            _r.ModelCount = _order.Count;
            _r.BoneCount = _limbs.Count;
            _r.MaterialCount = _materials.Count + (_defaultMaterialId != 0 ? 1 : 0);

            var top = new List<FbxNode>();
            top.Add(BuildHeader(fbxPath));
            top.Add(new FbxNode("FileId", FbxBinaryWriter.FileId));
            top.Add(new FbxNode("CreationTime", FbxBinaryWriter.CreationTime));
            top.Add(new FbxNode("Creator", Creator));
            top.Add(BuildGlobalSettings());
            top.Add(BuildDocuments());
            top.Add(new FbxNode("References"));
            top.Add(BuildDefinitions());
            top.Add(_objects);
            top.Add(_connections);
            var takes = new FbxNode("Takes");
            takes.Add("Current", "");
            top.Add(takes);
            return top;
        }

        // ================================================================== hierarchy

        private void CollectHierarchy(IEnumerable<GameObject> gameObjects)
        {
            var selected = new List<Transform>();
            var selectedSet = new HashSet<Transform>();
            foreach (var go in gameObjects)
            {
                if (go == null || !selectedSet.Add(go.transform)) continue;
                selected.Add(go.transform);
            }

            foreach (var t in selected)
            {
                // Skip objects whose ancestor is also selected; they are exported as part of that hierarchy.
                bool nested = false;
                for (var p = t.parent; p != null; p = p.parent)
                    if (selectedSet.Contains(p)) { nested = true; break; }
                if (nested) continue;

                _basis[t] = _o.PreserveWorldTransform ? Matrix4x4.identity : t.worldToLocalMatrix;
                Collect(t, t);
            }
        }

        private void Collect(Transform t, Transform root)
        {
            if (!_o.IncludeInactive && !t.gameObject.activeInHierarchy) return;
            _order.Add(t);
            _exported.Add(t);
            _rootOf[t] = root;
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), root);
        }

        /// <summary>Bones, the transforms between bones, and renderer-free leaf children of bones become LimbNodes.</summary>
        private void FindLimbNodes()
        {
            var bones = new HashSet<Transform>();
            var meshBones = new List<Transform>();
            foreach (var t in _order)
            {
                var smr = t.GetComponent<SkinnedMeshRenderer>();
                if (smr == null || smr.sharedMesh == null) continue;
                int missing = 0;
                meshBones.Clear();
                foreach (var b in smr.bones)
                {
                    if (b == null) continue;
                    if (_exported.Contains(b)) meshBones.Add(b);
                    else missing++;
                }
                if (smr.rootBone != null && _exported.Contains(smr.rootBone)) meshBones.Add(smr.rootBone);
                if (missing > 0)
                    _r.Warnings.Add($"'{smr.name}': {missing} bone(s) are outside the exported hierarchy and were skipped. Select the common parent of the mesh and its skeleton.");

                foreach (var b in meshBones) bones.Add(b);

                // Everything between this mesh's bones and their lowest common ancestor is part of the skeleton
                // (e.g. hips/spine nodes that carry no weights). Otherwise importers split the skeleton apart.
                var lca = CommonAncestor(meshBones);
                if (lca == null) continue;
                bool lcaIsBone = !HasRenderer(lca) || bones.Contains(lca);
                if (lcaIsBone) bones.Add(lca);
                foreach (var b in meshBones)
                    for (var p = b == lca ? null : b.parent; p != null && p != lca && _exported.Contains(p); p = p.parent)
                        bones.Add(p);
            }

            foreach (var b in bones) _limbs.Add(b);

            // Transforms sitting between two bones.
            var path = new List<Transform>();
            foreach (var b in bones)
            {
                path.Clear();
                for (var p = b.parent; p != null && _exported.Contains(p); p = p.parent)
                {
                    if (bones.Contains(p))
                    {
                        foreach (var x in path) _limbs.Add(x);
                        break;
                    }
                    path.Add(p);
                }
            }

            // Renderer-free leaves under bones (end effectors, sockets).
            foreach (var b in new List<Transform>(_limbs)) MarkLeafChildren(b);
        }

        private void MarkLeafChildren(Transform t)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (!_exported.Contains(c) || _limbs.Contains(c) || SubtreeHasRenderer(c)) continue;
                _limbs.Add(c);
                MarkLeafChildren(c);
            }
        }

        private Transform CommonAncestor(List<Transform> nodes)
        {
            if (nodes.Count == 0) return null;
            var chain = new List<Transform>();
            for (var p = nodes[0]; p != null; p = p.parent) chain.Add(p);
            int best = 0; // index into chain of the current common ancestor
            var set = new HashSet<Transform>();
            for (int i = 1; i < nodes.Count; i++)
            {
                set.Clear();
                for (var p = nodes[i]; p != null; p = p.parent) set.Add(p);
                while (best < chain.Count && !set.Contains(chain[best])) best++;
                if (best >= chain.Count) return null;
            }
            var lca = chain[best];
            return _exported.Contains(lca) ? lca : null;
        }

        private static bool HasRenderer(Transform t) =>
            t.GetComponent<SkinnedMeshRenderer>() != null || t.GetComponent<MeshFilter>() != null;

        private static bool SubtreeHasRenderer(Transform t)
        {
            if (HasRenderer(t)) return true;
            for (int i = 0; i < t.childCount; i++)
                if (SubtreeHasRenderer(t.GetChild(i))) return true;
            return false;
        }

        /// <summary>World matrix in export space (Unity coordinates).</summary>
        private Matrix4x4 World(Transform t) => _basis[_rootOf[t]] * t.localToWorldMatrix;

        // ================================================================== nodes

        private void BuildNode(Transform t)
        {
            long modelId = _modelIds[t];
            var go = t.gameObject;

            Mesh mesh = null;
            Renderer renderer = null;
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                mesh = smr.sharedMesh;
                renderer = smr;
            }
            else
            {
                var mf = t.GetComponent<MeshFilter>();
                var mr = t.GetComponent<MeshRenderer>();
                if (mf != null && mf.sharedMesh != null)
                {
                    mesh = mf.sharedMesh;
                    renderer = mr;
                    smr = null;
                }
            }

            MeshData md = null;
            if (mesh != null)
            {
                md = MeshData.Read(mesh, _o.ForceRuntimeReadback, _r.Warnings);
                if (md != null && !HasPolygons(md))
                {
                    _r.Warnings.Add($"Mesh '{mesh.name}' has no triangle/quad faces; exported as an empty node.");
                    md = null;
                }
            }

            bool deformed = md != null && smr != null &&
                            ((md.HasSkin && smr.bones.Length > 0) || (_o.ExportBlendShapes && md.BlendShapes.Count > 0));

            // Importers (Blender in particular) re-parent deformed meshes under their armature, which breaks any
            // children of that mesh. So a deformed mesh with children becomes an empty node (keeping the name and
            // children) plus a separate "<name>_Mesh" child carrying the geometry.
            bool split = deformed && HasExportedChildren(t);

            string type = md != null && !split ? "Mesh" : (_limbs.Contains(t) ? "LimbNode" : "Null");
            bool visible = go.activeInHierarchy && (renderer == null || renderer.enabled);
            WriteModel(t, modelId, type, visible);

            long parentId = (_rootOf[t] == t || t.parent == null) ? 0 : _modelIds[t.parent];
            ConnectOO(modelId, parentId);

            if (md == null || split)
            {
                WriteNodeAttribute(t.name, modelId, type == "LimbNode");
                if (md == null) return;

                long meshModelId = NewId();
                WriteModel(t.name + "_Mesh", meshModelId, "Mesh", visible, Vector3.zero, Quaternion.identity, Vector3.one);
                ConnectOO(meshModelId, modelId);
                _r.ModelCount++;
                modelId = meshModelId;
            }

            // Materials: map each submesh to a model-local material slot.
            var mats = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
            var localMats = new List<long>();
            var subToLocal = new int[md.SubMeshes.Count];
            var sigParts = new string[md.SubMeshes.Count];
            for (int i = 0; i < subToLocal.Length; i++)
            {
                Material m = mats.Length == 0 ? null : mats[Math.Min(i, mats.Length - 1)];
                long mid = GetMaterial(m);
                int idx = localMats.IndexOf(mid);
                if (idx < 0) { idx = localMats.Count; localMats.Add(mid); }
                subToLocal[i] = idx;
                sigParts[i] = idx.ToString();
            }

            bool skinned = smr != null && md.HasSkin && smr.bones.Length > 0;
            bool hasShapes = smr != null && _o.ExportBlendShapes && md.BlendShapes.Count > 0;

            long geoId;
            if (!deformed)
            {
                // Static meshes with identical material layout can share one Geometry (instancing).
                string key = mesh.GetInstanceID() + ":" + string.Join(",", sigParts);
                if (!_sharedGeometry.TryGetValue(key, out geoId))
                {
                    var gi = BuildGeometry(md, subToLocal, mesh.name, false);
                    geoId = gi.Id;
                    _sharedGeometry[key] = geoId;
                    _r.MeshCount++;
                }
                ConnectOO(geoId, modelId);
            }
            else
            {
                if (skinned) ApplySkinCorrection(md, smr);
                var gi = BuildGeometry(md, subToLocal, mesh.name, skinned);
                geoId = gi.Id;
                ConnectOO(geoId, modelId);
                _r.MeshCount++;
                if (skinned && BuildSkin(smr, md, gi, modelId)) _r.SkinnedMeshCount++;
                if (hasShapes) BuildBlendShapes(smr, md, gi);
            }

            foreach (long mid in localMats) ConnectOO(mid, modelId);
        }

        private bool HasExportedChildren(Transform t)
        {
            for (int i = 0; i < t.childCount; i++)
                if (_exported.Contains(t.GetChild(i))) return true;
            return false;
        }

        private void WriteNodeAttribute(string name, long modelId, bool limb)
        {
            long attrId = NewId();
            var attr = AddObject("NodeAttribute", attrId, new FbxObjectName(name, "NodeAttribute"), limb ? "LimbNode" : "Null");
            if (limb)
            {
                var p = new FbxProps70(attr);
                p.Double("Size", 1.0);
            }
            attr.Add("TypeFlags", limb ? "Skeleton" : "Null");
            ConnectOO(attrId, modelId);
        }

        private static bool HasPolygons(MeshData md)
        {
            foreach (var s in md.SubMeshes)
                if ((s.Topology == MeshTopology.Triangles || s.Topology == MeshTopology.Quads) && s.Indices.Length > 0)
                    return true;
            return false;
        }

        private void WriteModel(Transform t, long id, string type, bool visible)
        {
            Vector3 lp, ls;
            Quaternion lr;
            if (_rootOf[t] == t)
            {
                if (_o.PreserveWorldTransform)
                {
                    lp = t.position;
                    lr = t.rotation;
                    ls = t.lossyScale;
                }
                else
                {
                    lp = Vector3.zero;
                    lr = Quaternion.identity;
                    ls = Vector3.one;
                }
            }
            else
            {
                lp = t.localPosition;
                lr = t.localRotation;
                ls = t.localScale;
            }
            WriteModel(t.name, id, type, visible, lp, lr, ls);
        }

        private void WriteModel(string name, long id, string type, bool visible, Vector3 lp, Quaternion lr, Vector3 ls)
        {
            var model = AddObject("Model", id, new FbxObjectName(name, "Model"), type);
            model.Add("Version", 232);
            var p = new FbxProps70(model);
            var pos = Conv.Position(lp);
            var rot = Conv.EulerXYZ(Conv.Rotation(lr));
            p.Lcl("Lcl Translation", pos.x, pos.y, pos.z);
            p.Lcl("Lcl Rotation", rot.x, rot.y, rot.z);
            p.Lcl("Lcl Scaling", ls.x, ls.y, ls.z);
            p.Visibility(visible ? 1.0 : 0.0);
            p.Int("DefaultAttributeIndex", 0);
            p.Enum("InheritType", 1); // RSrs: child = parent matrix * local matrix, same as Unity
            model.Add("MultiLayer", 0);
            model.Add("MultiTake", 0);
            model.Add("Shading", new FbxChar('Y'));
            model.Add("Culling", "CullingOff");
        }

        // ================================================================== geometry

        private GeometryInfo BuildGeometry(MeshData md, int[] subToLocal, string name, bool skinned)
        {
            int vc = md.VertexCount;
            var v2cp = new int[vc];
            var cps = new List<Vector3>(vc);

            int[] weightStart = null;
            if (md.HasSkin)
            {
                weightStart = new int[vc];
                int acc = 0;
                for (int v = 0; v < vc; v++) { weightStart[v] = acc; acc += md.BonesPerVertex[v]; }
            }

            if (_o.WeldVertices)
            {
                var map = new Dictionary<WeldKey, int>(vc);
                for (int v = 0; v < vc; v++)
                {
                    var p = md.Positions[v];
                    int wh = 0;
                    if (weightStart != null && skinned)
                    {
                        unchecked
                        {
                            wh = 17;
                            int n = md.BonesPerVertex[v], s = weightStart[v];
                            for (int k = 0; k < n; k++)
                            {
                                var bw = md.Weights[s + k];
                                wh = wh * 31 + bw.boneIndex;
                                wh = wh * 31 + BitConverter.SingleToInt32Bits(bw.weight);
                            }
                        }
                    }
                    var key = new WeldKey(p, wh);
                    if (!map.TryGetValue(key, out int cp))
                    {
                        cp = cps.Count;
                        cps.Add(p);
                        map.Add(key, cp);
                    }
                    v2cp[v] = cp;
                }
            }
            else
            {
                for (int v = 0; v < vc; v++) { v2cp[v] = v; cps.Add(md.Positions[v]); }
            }

            // Polygons (winding reversed for the handedness flip).
            var pvi = new List<int>();
            var pvVert = new List<int>();
            var polyMat = new List<int>();
            bool warnedTopology = false, warnedRange = false;
            for (int s = 0; s < md.SubMeshes.Count; s++)
            {
                var sm = md.SubMeshes[s];
                int n;
                if (sm.Topology == MeshTopology.Triangles) n = 3;
                else if (sm.Topology == MeshTopology.Quads) n = 4;
                else
                {
                    if (!warnedTopology) _r.Warnings.Add($"Mesh '{name}': submesh {s} uses {sm.Topology} topology and was skipped.");
                    warnedTopology = true;
                    continue;
                }

                var idx = sm.Indices;
                for (int f = 0; f + n <= idx.Length; f += n)
                {
                    bool ok = true;
                    for (int k = 0; k < n; k++)
                        if ((uint)idx[f + k] >= (uint)vc) ok = false;
                    if (!ok)
                    {
                        if (!warnedRange) _r.Warnings.Add($"Mesh '{name}': skipped faces with out-of-range indices.");
                        warnedRange = true;
                        continue;
                    }
                    // a,b,c -> a,c,b   |   a,b,c,d -> a,d,c,b
                    for (int k = 0; k < n; k++)
                    {
                        int src = k == 0 ? idx[f] : idx[f + n - k];
                        int cp = v2cp[src];
                        pvi.Add(k == n - 1 ? ~cp : cp);
                        pvVert.Add(src);
                    }
                    polyMat.Add(subToLocal[s]);
                }
            }

            long id = NewId();
            var g = AddObject("Geometry", id, new FbxObjectName(name, "Geometry"), "Mesh");

            var verts = new double[cps.Count * 3];
            for (int i = 0; i < cps.Count; i++)
            {
                verts[i * 3] = -cps[i].x;
                verts[i * 3 + 1] = cps[i].y;
                verts[i * 3 + 2] = cps[i].z;
            }
            g.AddArray("Vertices", verts);
            g.AddArray("PolygonVertexIndex", pvi.ToArray());
            g.Add("GeometryVersion", 124);

            int pvCount = pvVert.Count;
            var pvVertArr = pvVert.ToArray();

            bool hasNormals = md.Normals != null;
            if (hasNormals)
            {
                var nrm = new double[pvCount * 3];
                for (int i = 0; i < pvCount; i++)
                {
                    var nv = md.Normals[pvVertArr[i]];
                    nrm[i * 3] = -nv.x;
                    nrm[i * 3 + 1] = nv.y;
                    nrm[i * 3 + 2] = nv.z;
                }
                var ln = g.Add("LayerElementNormal", 0);
                ln.Add("Version", 101);
                ln.Add("Name", "");
                ln.Add("MappingInformationType", "ByPolygonVertex");
                ln.Add("ReferenceInformationType", "Direct");
                ln.AddArray("Normals", nrm);
            }

            bool hasColors = _o.ExportVertexColors && md.Colors != null;
            if (hasColors)
            {
                var col = new double[vc * 4];
                for (int v = 0; v < vc; v++)
                {
                    var c = md.Colors[v];
                    col[v * 4] = c.r; col[v * 4 + 1] = c.g; col[v * 4 + 2] = c.b; col[v * 4 + 3] = c.a;
                }
                var lc = g.Add("LayerElementColor", 0);
                lc.Add("Version", 101);
                lc.Add("Name", "Col");
                lc.Add("MappingInformationType", "ByPolygonVertex");
                lc.Add("ReferenceInformationType", "IndexToDirect");
                lc.AddArray("Colors", col);
                lc.AddArray("ColorIndex", pvVertArr);
            }

            int uvLayers = 0;
            for (int ch = 0; ch < 8; ch++)
            {
                var uv = md.UVs[ch];
                if (uv == null) continue;
                var arr = new double[vc * 2];
                for (int v = 0; v < vc; v++) { arr[v * 2] = uv[v].x; arr[v * 2 + 1] = uv[v].y; }
                var lu = g.Add("LayerElementUV", uvLayers);
                lu.Add("Version", 101);
                lu.Add("Name", "UVChannel_" + (uvLayers + 1));
                lu.Add("MappingInformationType", "ByPolygonVertex");
                lu.Add("ReferenceInformationType", "IndexToDirect");
                lu.AddArray("UV", arr);
                lu.AddArray("UVIndex", pvVertArr);
                uvLayers++;
            }

            bool allSame = true;
            for (int i = 1; i < polyMat.Count; i++) if (polyMat[i] != polyMat[0]) { allSame = false; break; }
            var lm = g.Add("LayerElementMaterial", 0);
            lm.Add("Version", 101);
            lm.Add("Name", "");
            if (allSame)
            {
                lm.Add("MappingInformationType", "AllSame");
                lm.Add("ReferenceInformationType", "IndexToDirect");
                lm.AddArray("Materials", new[] { polyMat.Count > 0 ? polyMat[0] : 0 });
            }
            else
            {
                lm.Add("MappingInformationType", "ByPolygon");
                lm.Add("ReferenceInformationType", "IndexToDirect");
                lm.AddArray("Materials", polyMat.ToArray());
            }

            var layer0 = g.Add("Layer", 0);
            layer0.Add("Version", 100);
            if (hasNormals) AddLayerElement(layer0, "LayerElementNormal", 0);
            AddLayerElement(layer0, "LayerElementMaterial", 0);
            if (hasColors) AddLayerElement(layer0, "LayerElementColor", 0);
            if (uvLayers > 0) AddLayerElement(layer0, "LayerElementUV", 0);
            for (int i = 1; i < uvLayers; i++)
            {
                var layer = g.Add("Layer", i);
                layer.Add("Version", 100);
                AddLayerElement(layer, "LayerElementUV", i);
            }

            return new GeometryInfo { Id = id, VertexToControlPoint = v2cp, ControlPointCount = cps.Count };
        }

        private static void AddLayerElement(FbxNode layer, string type, int index)
        {
            var le = layer.Add("LayerElement");
            le.Add("Type", type);
            le.Add("TypedIndex", index);
        }

        private readonly struct WeldKey : IEquatable<WeldKey>
        {
            private readonly int _x, _y, _z, _w;

            public WeldKey(Vector3 p, int weightHash)
            {
                // +0f normalises -0 to 0 so they weld together.
                _x = BitConverter.SingleToInt32Bits(p.x + 0f);
                _y = BitConverter.SingleToInt32Bits(p.y + 0f);
                _z = BitConverter.SingleToInt32Bits(p.z + 0f);
                _w = weightHash;
            }

            public bool Equals(WeldKey o) => _x == o._x && _y == o._y && _z == o._z && _w == o._w;
            public override bool Equals(object obj) => obj is WeldKey k && Equals(k);
            public override int GetHashCode()
            {
                unchecked { return ((_x * 397 ^ _y) * 397 ^ _z) * 397 ^ _w; }
            }
        }

        // ================================================================== skinning

        /// <summary>
        /// Unity stores a bind pose per mesh, so meshes sharing bones may have been bound with the skeleton in
        /// different places (e.g. a head mesh authored at the origin, then attached to the body). FBX importers like
        /// Blender keep one rest pose per bone, which stretches such meshes. Here every bone gets a single bind
        /// matrix: the largest mesh defines it, other meshes are fitted through shared bones (or, with none shared,
        /// through the hierarchy), and ApplySkinCorrection moves their vertices to match.
        /// </summary>
        private void ComputeCanonicalBinds()
        {
            var smrs = new List<SkinnedMeshRenderer>();
            foreach (var t in _order)
            {
                var s = t.GetComponent<SkinnedMeshRenderer>();
                if (s != null && s.sharedMesh != null && s.bones.Length > 0) smrs.Add(s);
            }

            // The largest mesh (usually the body) defines the rest pose; the others are fitted to it.
            // Where the whole rest frame ends up is fixed afterwards by AlignBindFramesToArmatures.
            var orderIndex = new Dictionary<SkinnedMeshRenderer, int>();
            for (int i = 0; i < smrs.Count; i++) orderIndex[smrs[i]] = i;
            smrs.Sort((a, b) =>
            {
                int c = b.sharedMesh.vertexCount.CompareTo(a.sharedMesh.vertexCount);
                if (c == 0) c = b.bones.Length.CompareTo(a.bones.Length);
                return c != 0 ? c : orderIndex[a].CompareTo(orderIndex[b]);
            });

            var binds = new List<(Transform bone, Matrix4x4 bind)>();
            var seen = new HashSet<Transform>();
            var meshSkeleton = new Dictionary<SkinnedMeshRenderer, Transform>(); // mesh -> one of its bones
            foreach (var smr in smrs)
            {
                Matrix4x4[] bp;
                try { bp = smr.sharedMesh.bindposes; }
                catch (Exception) { continue; }
                if (bp == null || bp.Length == 0) continue;

                var bones = smr.bones;
                var meshWorld = World(smr.transform);
                binds.Clear();
                seen.Clear();
                for (int i = 0; i < bones.Length && i < bp.Length; i++)
                {
                    var b = bones[i];
                    if (b == null || !_exported.Contains(b) || !seen.Add(b)) continue;
                    binds.Add((b, meshWorld * bp[i].inverse));
                }

                // Offset mapping this mesh's bind skeleton onto the bones already placed.
                Matrix4x4 offset = Matrix4x4.identity;
                bool hasOffset = false;
                float spread = 0f;
                foreach (var (b, bind) in binds)
                {
                    if (!_canonicalBind.TryGetValue(b, out var canon)) continue;
                    var ti = canon * bind.inverse;
                    if (!hasOffset) { offset = ti; hasOffset = true; }
                    else spread = Mathf.Max(spread, MaxDifference(offset, ti));
                }
                // No shared bones: fit through the hierarchy instead (e.g. an ear/accessory prefab skinned to its
                // own bones, attached under the head and bound in a different pose than the body).
                if (!hasOffset && _canonicalBind.Count > 0 && TryHierarchyOffset(binds, out var hierarchyOffset))
                {
                    offset = hierarchyOffset;
                    hasOffset = true;
                }
                if (spread > 1e-4f)
                    _r.Warnings.Add($"'{smr.name}' was bound to its bones in a different pose than other meshes sharing them; vertices blended between those bones may deform slightly differently outside Unity.");

                foreach (var (b, bind) in binds)
                    if (!_canonicalBind.ContainsKey(b)) _canonicalBind[b] = offset * bind;
            }

            FillUnboundLimbBinds();
            AlignBindFramesToArmatures();
            NormalizeRelativeBindScales();
        }

        /// <summary>
        /// Removes scale between a bone's rest pose and its parent's (e.g. a head scaled 1.13x when the hair
        /// was bound), which Blender bones can't store. Each such bone's whole subtree is scaled about the bone,
        /// so meshes within the subtree stay exact; the current scale then shows up as pose scale instead.
        /// </summary>
        private void NormalizeRelativeBindScales()
        {
            foreach (var t in _order)
            {
                if (!_canonicalBind.TryGetValue(t, out var c)) continue;
                var p = t.parent;
                if (p == null || !_canonicalBind.TryGetValue(p, out var cp)) continue;

                var rel = cp.inverse * c;
                float s = (((Vector3)rel.GetColumn(0)).magnitude + ((Vector3)rel.GetColumn(1)).magnitude + ((Vector3)rel.GetColumn(2)).magnitude) / 3f;
                if (s < 1e-6f || Mathf.Abs(s - 1f) < 1e-4f) continue;

                Vector3 pivot = c.GetColumn(3);
                var g = Matrix4x4.Translate(pivot) * Matrix4x4.Scale(Vector3.one / s) * Matrix4x4.Translate(-pivot);
                ApplyToSubtree(t, g);
            }
        }

        private void ApplyToSubtree(Transform t, Matrix4x4 g)
        {
            if (_canonicalBind.TryGetValue(t, out var c)) _canonicalBind[t] = g * c;
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (_exported.Contains(child)) ApplyToSubtree(child, g);
            }
        }

        /// <summary>
        /// Moves each skeleton's whole bind frame so that, relative to its armature node (the parent of the
        /// topmost bone), the rest pose has no scale and the top bone rests where it currently is.
        /// Blender bones can't store scale in their rest pose, so a skeleton bound at scale 1 under an armature
        /// node that is now scaled (e.g. 1.0078 for a character's height) would otherwise import slightly off.
        /// Meshes follow automatically through ApplySkinCorrection, so the skinned result is unchanged.
        /// </summary>
        private void AlignBindFramesToArmatures()
        {
            var groupFix = new Dictionary<Transform, Matrix4x4>(); // top bone -> world-space bind frame fix
            Transform TopLimb(Transform b)
            {
                var top = b;
                while (top.parent != null && _limbs.Contains(top.parent) && _exported.Contains(top.parent) && _rootOf[top] != top)
                    top = top.parent;
                return top;
            }

            Matrix4x4 FixFor(Transform top)
            {
                if (groupFix.TryGetValue(top, out var g)) return g;
                g = Matrix4x4.identity;
                if (_canonicalBind.TryGetValue(top, out var cTop))
                {
                    var arm = top.parent != null && _exported.Contains(top.parent) && _rootOf[top] != top ? top.parent : null;
                    Matrix4x4 armNow = arm != null ? World(arm) : Matrix4x4.identity;

                    // Uniform scale of the bind frame relative to the armature node.
                    var rel = armNow.inverse * cTop;
                    float k = (((Vector3)rel.GetColumn(0)).magnitude + ((Vector3)rel.GetColumn(1)).magnitude + ((Vector3)rel.GetColumn(2)).magnitude) / 3f;
                    if (k > 1e-6f && Mathf.Abs(k - 1f) > 1e-5f)
                        g = armNow * Matrix4x4.Scale(Vector3.one / k) * armNow.inverse;

                    // Rest the top bone where it currently is (translation only, keep orientation).
                    Vector3 restPos = (g * cTop).GetColumn(3);
                    Vector3 nowPos = World(top).GetColumn(3);
                    g = Matrix4x4.Translate(nowPos - restPos) * g;
                }
                groupFix[top] = g;
                return g;
            }

            // Fix every bind matrix of each skeleton.
            var keys = new List<Transform>(_canonicalBind.Keys);
            foreach (var b in keys) FixFor(TopLimb(b));
            foreach (var b in keys) _canonicalBind[b] = groupFix[TopLimb(b)] * _canonicalBind[b];
        }

        /// <summary>
        /// Skeleton nodes no mesh is weighted to (e.g. a "Mouth_Root" above weighted mouth bones) still need a rest
        /// pose consistent with their bound neighbours. Derive it from a bound parent (parentBind * local), or
        /// from a bound child (childBind * childLocal^-1), instead of using the node's current world position.
        /// </summary>
        private void FillUnboundLimbBinds()
        {
            bool Missing(Transform t) => _limbs.Contains(t) && !_canonicalBind.ContainsKey(t) && t.parent != null && _exported.Contains(t.parent) && _rootOf[t] != t;

            void TopDown()
            {
                foreach (var t in _order)
                    if (Missing(t) && _canonicalBind.TryGetValue(t.parent, out var pc))
                        _canonicalBind[t] = pc * LocalMatrix(t);
            }

            TopDown();
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                var t = _order[i];
                if (!_limbs.Contains(t) || _canonicalBind.ContainsKey(t)) continue;
                for (int c = 0; c < t.childCount; c++)
                {
                    var child = t.GetChild(c);
                    if (_canonicalBind.TryGetValue(child, out var cc))
                    {
                        _canonicalBind[t] = cc * LocalMatrix(child).inverse;
                        break;
                    }
                }
            }
            TopDown();
        }

        private static Matrix4x4 LocalMatrix(Transform t) => Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);

        /// <summary>
        /// For a mesh sharing no bones with the already placed skeleton: find the closest placed ancestor
        /// (or descendant) of one of its bones, predict that bone's rest from the current relative transform,
        /// and return the offset mapping the mesh's own bind onto that prediction.
        /// </summary>
        private bool TryHierarchyOffset(List<(Transform bone, Matrix4x4 bind)> binds, out Matrix4x4 offset)
        {
            offset = Matrix4x4.identity;
            int bestSteps = int.MaxValue;
            foreach (var (b, bind) in binds)
            {
                int steps = 0;
                for (var p = b.parent; p != null && _exported.Contains(p) && steps < bestSteps; p = p.parent)
                {
                    steps++;
                    if (!_canonicalBind.TryGetValue(p, out var cp)) continue;
                    var predicted = cp * World(p).inverse * World(b);
                    offset = predicted * bind.inverse;
                    bestSteps = steps;
                    break;
                }
            }
            if (bestSteps != int.MaxValue) return true;

            // Otherwise look below the mesh's bones (breadth-first).
            foreach (var (b, bind) in binds)
            {
                var queue = new Queue<Transform>();
                queue.Enqueue(b);
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var c = t.GetChild(i);
                        if (!_exported.Contains(c)) continue;
                        if (_canonicalBind.TryGetValue(c, out var cc))
                        {
                            var predicted = cc * World(c).inverse * World(b);
                            offset = predicted * bind.inverse;
                            return true;
                        }
                        queue.Enqueue(c);
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Re-expresses a skinned mesh's vertices for the shared bind poses. Each bone i has a correction
        /// K_i = meshWorld^-1 * C_i * bindpose_i (identity when the mesh was bound exactly like the shared
        /// skeleton), and each vertex moves by the weighted blend of its bones' corrections. That is exact
        /// whenever a vertex's bones agree (e.g. a rigid offset of the whole mesh, or single-bone vertices).
        /// </summary>
        private void ApplySkinCorrection(MeshData md, SkinnedMeshRenderer smr)
        {
            var bones = smr.bones;
            var bp = md.BindPoses;
            if (!md.HasSkin || bp == null) return;
            var meshWorldInv = World(smr.transform).inverse;

            int n = Mathf.Min(bones.Length, bp.Length);
            var k = new Matrix4x4[n];
            var hasK = new bool[n];
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                if (bones[i] == null || !_canonicalBind.TryGetValue(bones[i], out var c)) continue;
                k[i] = meshWorldInv * c * bp[i];
                hasK[i] = true;
                if (MaxDifference(k[i], Matrix4x4.identity) > 1e-6f) any = true;
            }
            if (!any) return;

            int w = 0;
            for (int v = 0; v < md.VertexCount; v++)
            {
                int count = md.BonesPerVertex[v];
                var blend = new Matrix4x4();
                float total = 0f;
                for (int j = 0; j < count; j++)
                {
                    var bw = md.Weights[w + j];
                    if (bw.weight <= 0f || bw.boneIndex < 0 || bw.boneIndex >= n) continue;
                    var ki = hasK[bw.boneIndex] ? k[bw.boneIndex] : Matrix4x4.identity;
                    for (int e = 0; e < 16; e++) blend[e] += ki[e] * bw.weight;
                    total += bw.weight;
                }
                w += count;
                if (total <= 0f) continue;
                for (int e = 0; e < 16; e++) blend[e] /= total;

                md.Positions[v] = blend.MultiplyPoint3x4(md.Positions[v]);
                var normalMatrix = blend.inverse.transpose;
                if (md.Normals != null) md.Normals[v] = normalMatrix.MultiplyVector(md.Normals[v]).normalized;
                foreach (var s in md.BlendShapes)
                {
                    s.DeltaVertices[v] = blend.MultiplyVector(s.DeltaVertices[v]);
                    s.DeltaNormals[v] = normalMatrix.MultiplyVector(s.DeltaNormals[v]);
                }
            }
        }

        private static float MaxDifference(Matrix4x4 a, Matrix4x4 b)
        {
            float d = 0f;
            for (int i = 0; i < 16; i++) d = Mathf.Max(d, Mathf.Abs(a[i] - b[i]));
            return d;
        }

        private bool BuildSkin(SkinnedMeshRenderer smr, MeshData md, GeometryInfo gi, long meshModelId)
        {
            var bones = smr.bones;
            var bindposes = md.BindPoses;
            if (bindposes == null || bindposes.Length == 0)
            {
                _r.Warnings.Add($"'{smr.name}': mesh has bone weights but no bind poses; skinning skipped.");
                return false;
            }

            // Merge duplicate bone entries; gather control-point weights per bone.
            var boneOrder = new List<Transform>();
            var boneBindIndex = new Dictionary<Transform, int>();
            var boneWeights = new Dictionary<Transform, Dictionary<int, double>>();
            for (int i = 0; i < bones.Length && i < bindposes.Length; i++)
            {
                var b = bones[i];
                if (b == null || !_modelIds.ContainsKey(b) || boneBindIndex.ContainsKey(b)) continue;
                boneBindIndex[b] = i;
                boneOrder.Add(b);
                boneWeights[b] = new Dictionary<int, double>();
            }
            if (boneOrder.Count == 0) return false;

            int w = 0;
            var perVertexBone = new Dictionary<Transform, double>();
            for (int v = 0; v < md.VertexCount; v++)
            {
                int n = md.BonesPerVertex[v];
                int cp = gi.VertexToControlPoint[v];
                perVertexBone.Clear();
                for (int k = 0; k < n; k++)
                {
                    var bw = md.Weights[w++];
                    if (bw.weight <= 0f || bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                    var b = bones[bw.boneIndex];
                    if (b == null || !boneWeights.ContainsKey(b)) continue;
                    perVertexBone.TryGetValue(b, out double acc);
                    perVertexBone[b] = acc + bw.weight;
                }
                // Welded vertices share identical weights, so assigning (not adding) per control point is correct.
                foreach (var kv in perVertexBone) boneWeights[kv.Key][cp] = kv.Value;
            }

            Matrix4x4 meshWorld = World(smr.transform);

            long skinId = NewId();
            var skin = AddObject("Deformer", skinId, new FbxObjectName(smr.name, "Deformer"), "Skin");
            skin.Add("Version", 101);
            skin.Add("Link_DeformAcuracy", 50.0);
            ConnectOO(skinId, gi.Id);

            // Ancestors of the bones (skeleton parents and the armature node) also go into the bind pose, using
            // their current world matrix. Blender needs the armature's bind matrix to place the skinned mesh.
            var ancestors = new List<Transform>();
            var inPose = new HashSet<Transform>(boneOrder);
            foreach (var b in boneOrder)
                for (var p = b.parent; p != null && _modelIds.ContainsKey(p) && inPose.Add(p); p = p.parent)
                    ancestors.Add(p);

            long poseId = NewId();
            var pose = AddObject("Pose", poseId, new FbxObjectName(smr.name, "Pose"), "BindPose");
            pose.Add("Type", "BindPose");
            pose.Add("Version", 100);
            pose.Add("NbPoseNodes", 1 + boneOrder.Count + ancestors.Count);
            AddPoseNode(pose, meshModelId, meshWorld);
            foreach (var a in ancestors)
                AddPoseNode(pose, _modelIds[a], _canonicalBind.TryGetValue(a, out var ab) ? ab : World(a));

            foreach (var b in boneOrder)
            {
                // Unity: bindpose = bone.worldToLocal * mesh.localToWorld at bind time.
                if (!_canonicalBind.TryGetValue(b, out Matrix4x4 boneBind))
                    boneBind = meshWorld * bindposes[boneBindIndex[b]].inverse;
                var weights = boneWeights[b];

                long clusterId = NewId();
                var cl = AddObject("Deformer", clusterId, new FbxObjectName(b.name, "SubDeformer"), "Cluster");
                cl.Add("Version", 100);
                cl.Add("UserData", "", "");
                if (weights.Count > 0)
                {
                    var idx = new int[weights.Count];
                    var wt = new double[weights.Count];
                    int k = 0;
                    foreach (var kv in weights) { idx[k] = kv.Key; wt[k] = kv.Value; k++; }
                    cl.AddArray("Indexes", idx);
                    cl.AddArray("Weights", wt);
                }
                cl.AddArray("Transform", Conv.MatrixArray(boneBind.inverse * meshWorld));
                cl.AddArray("TransformLink", Conv.MatrixArray(boneBind));
                cl.AddArray("TransformAssociateModel", Conv.MatrixArray(ArmatureWorld(b)));

                ConnectOO(clusterId, skinId);
                ConnectOO(_modelIds[b], clusterId);
                AddPoseNode(pose, _modelIds[b], boneBind);
            }
            return true;
        }

        /// <summary>
        /// World matrix of the node an importer turns into the armature: the parent of the topmost LimbNode
        /// above this bone, or identity when the skeleton starts at the scene root.
        /// </summary>
        private Matrix4x4 ArmatureWorld(Transform bone)
        {
            var top = bone;
            while (top.parent != null && _limbs.Contains(top.parent) && _modelIds.ContainsKey(top.parent)) top = top.parent;
            var arm = top.parent;
            if (arm == null || !_modelIds.ContainsKey(arm) || _rootOf[top] == top) return Matrix4x4.identity;
            return World(arm);
        }

        private static void AddPoseNode(FbxNode pose, long nodeId, Matrix4x4 unityWorld)
        {
            var pn = pose.Add("PoseNode");
            pn.Add("Node", nodeId);
            pn.AddArray("Matrix", Conv.MatrixArray(unityWorld));
        }

        private void BuildBlendShapes(SkinnedMeshRenderer smr, MeshData md, GeometryInfo gi)
        {
            long bsId = NewId();
            AddObject("Deformer", bsId, new FbxObjectName(md.Name, "Deformer"), "BlendShape").Add("Version", 100);
            ConnectOO(bsId, gi.Id);

            var seen = new bool[gi.ControlPointCount];
            for (int s = 0; s < md.BlendShapes.Count; s++)
            {
                var shape = md.BlendShapes[s];
                Array.Clear(seen, 0, seen.Length);
                var idx = new List<int>();
                var dv = new List<double>();
                var dn = new List<double>();
                for (int v = 0; v < md.VertexCount; v++)
                {
                    int cp = gi.VertexToControlPoint[v];
                    if (seen[cp]) continue;
                    seen[cp] = true;
                    var d = shape.DeltaVertices[v];
                    if (d.sqrMagnitude < 1e-14f) continue;
                    var n = shape.DeltaNormals[v];
                    idx.Add(cp);
                    dv.Add(-d.x); dv.Add(d.y); dv.Add(d.z);
                    dn.Add(-n.x); dn.Add(n.y); dn.Add(n.z);
                }
                if (idx.Count == 0)
                {
                    idx.Add(0);
                    dv.AddRange(new[] { 0.0, 0.0, 0.0 });
                    dn.AddRange(new[] { 0.0, 0.0, 0.0 });
                }

                long shapeId = NewId();
                var sg = AddObject("Geometry", shapeId, new FbxObjectName(shape.Name, "Geometry"), "Shape");
                sg.Add("Version", 100);
                sg.AddArray("Indexes", idx.ToArray());
                sg.AddArray("Vertices", dv.ToArray());
                sg.AddArray("Normals", dn.ToArray());

                float frameWeight = shape.FrameWeight > 0f ? shape.FrameWeight : 100f;
                double percent = smr.GetBlendShapeWeight(s) / frameWeight * 100.0;

                long chId = NewId();
                var ch = AddObject("Deformer", chId, new FbxObjectName(shape.Name, "SubDeformer"), "BlendShapeChannel");
                ch.Add("Version", 100);
                ch.Add("DeformPercent", percent);
                ch.AddArray("FullWeights", new[] { 100.0 });

                ConnectOO(shapeId, chId);
                ConnectOO(chId, bsId);
            }
        }

        // ================================================================== materials & textures

        private long GetMaterial(Material m)
        {
            if (m == null)
            {
                if (_defaultMaterialId == 0)
                {
                    _defaultMaterialId = NewId();
                    WriteMaterial(_defaultMaterialId, "Default", Color.white, Color.black, 0f, 0.5f, null);
                }
                return _defaultMaterialId;
            }
            if (_materials.TryGetValue(m, out long id)) return id;
            id = NewId();
            _materials[m] = id;

            var info = MaterialInfo.From(m);
            WriteMaterial(id, m.name, info.BaseColor, info.Emission, info.Metallic, info.Smoothness, info);
            return id;
        }

        private void WriteMaterial(long id, string name, Color baseColor, Color emission, float metallic, float smoothness, MaterialInfo info)
        {
            var mat = AddObject("Material", id, new FbxObjectName(name, "Material"), "");
            mat.Add("Version", 102);
            mat.Add("ShadingModel", "phong");
            mat.Add("MultiLayer", 0);
            var p = new FbxProps70(mat);
            p.String("ShadingModel", "phong");
            p.Color("DiffuseColor", baseColor.r, baseColor.g, baseColor.b);
            p.Number("DiffuseFactor", 1.0);
            p.Color("EmissiveColor", emission.r, emission.g, emission.b);
            p.Number("EmissiveFactor", 1.0);
            p.Color("AmbientColor", 0, 0, 0);
            p.Number("AmbientFactor", 0.0);

            // Same alpha convention as Blender's exporter (alpha = 1 - TransparencyFactor, fallback TransparentColor).
            float a = baseColor.a;
            if (a < 1e-5f || a > 1f - 1e-5f) p.Color("TransparentColor", 1 - a, 1 - a, 1 - a);
            else p.Color("TransparentColor", baseColor.r, baseColor.g, baseColor.b);
            p.Number("TransparencyFactor", 1.0 - a);
            p.Number("Opacity", a);

            p.Vector3D("NormalMap", 0, 0, 0);
            p.Double("BumpFactor", info != null ? info.NormalScale : 1.0);
            p.Color("SpecularColor", 1, 1, 1);
            p.Number("SpecularFactor", 0.25);
            double shininess = smoothness * 10.0;
            shininess *= shininess;
            p.Number("Shininess", shininess);
            p.Number("ShininessExponent", shininess);
            p.Color("ReflectionColor", baseColor.r, baseColor.g, baseColor.b);
            p.Number("ReflectionFactor", metallic);

            if (info == null || !_o.ExportTextures) return;

            if (info.MainTex != null)
            {
                long tid = GetTextureNode(info.MainTex, false, info.MainScale, info.MainOffset);
                if (tid != 0)
                {
                    ConnectOP(tid, id, "DiffuseColor");
                    if (info.UsesAlpha) ConnectOP(tid, id, "TransparentColor");
                }
            }
            if (info.NormalTex != null)
            {
                long tid = GetTextureNode(info.NormalTex, true, info.NormalTexScale, info.NormalTexOffset);
                if (tid != 0) ConnectOP(tid, id, "NormalMap");
            }
            if (info.EmissionTex != null)
            {
                long tid = GetTextureNode(info.EmissionTex, false, info.EmissionScale, info.EmissionOffset);
                if (tid != 0) ConnectOP(tid, id, "EmissiveColor");
            }
        }

        private long GetTextureNode(Texture tex, bool normal, Vector2 scale, Vector2 offset)
        {
            string key = tex.GetInstanceID() + "|" + normal + "|" + scale.x + "," + scale.y + "|" + offset.x + "," + offset.y;
            if (_textureNodes.TryGetValue(key, out long id)) return id;

            var entry = _textures.Get(tex, normal);
            if (entry == null) { _textureNodes[key] = 0; return 0; }

            if (!_videos.TryGetValue(entry, out long videoId))
            {
                videoId = NewId();
                _videos[entry] = videoId;
                string vname = Path.GetFileNameWithoutExtension(entry.FileName);
                var vid = AddObject("Video", videoId, new FbxObjectName(vname, "Video"), "Clip");
                vid.Add("Type", "Clip");
                var vp = new FbxProps70(vid);
                vp.Url("Path", entry.AbsolutePath);
                vid.Add("UseMipMap", 0);
                vid.Add("Filename", entry.AbsolutePath);
                vid.Add("RelativeFilename", entry.RelativePath);
                if (entry.Content != null) vid.Add("Content", entry.Content);
                _r.TextureCount++;
            }

            id = NewId();
            _textureNodes[key] = id;
            string tname = Path.GetFileNameWithoutExtension(entry.FileName);
            var t = AddObject("Texture", id, new FbxObjectName(tname, "Texture"), "");
            t.Add("Type", "TextureVideoClip");
            t.Add("Version", 202);
            t.Add("TextureName", new FbxObjectName(tname, "Texture"));
            var p = new FbxProps70(t);
            p.Enum("AlphaSource", entry.HasAlpha ? 2 : 0);
            p.Bool("PremultiplyAlpha", false);
            p.Enum("CurrentMappingType", 0);
            p.String("UVSet", "UVChannel_1");
            p.Enum("WrapModeU", tex.wrapModeU == TextureWrapMode.Clamp ? 1 : 0);
            p.Enum("WrapModeV", tex.wrapModeV == TextureWrapMode.Clamp ? 1 : 0);
            p.Vector("Translation", offset.x, offset.y, 0);
            p.Vector("Rotation", 0, 0, 0);
            p.Vector("Scaling", scale.x, scale.y, 1);
            p.Bool("UseMaterial", true);
            p.Bool("UseMipMap", false);
            t.Add("Media", new FbxObjectName(Path.GetFileNameWithoutExtension(entry.FileName), "Video"));
            t.Add("FileName", entry.AbsolutePath);
            t.Add("RelativeFilename", entry.RelativePath);
            t.Add("ModelUVTranslation", 0.0, 0.0);
            t.Add("ModelUVScaling", 1.0, 1.0);
            t.Add("Texture_Alpha_Source", entry.HasAlpha ? "Alpha_Black" : "None");
            t.Add("Cropping", 0, 0, 0, 0);
            ConnectOO(videoId, id);
            return id;
        }

        // ================================================================== document boilerplate

        private FbxNode BuildHeader(string fbxPath)
        {
            var now = DateTime.Now;
            var h = new FbxNode("FBXHeaderExtension");
            h.Add("FBXHeaderVersion", 1003);
            h.Add("FBXVersion", FbxBinaryWriter.Version);
            h.Add("EncryptionType", 0);
            var ts = h.Add("CreationTimeStamp");
            ts.Add("Version", 1000);
            ts.Add("Year", now.Year);
            ts.Add("Month", now.Month);
            ts.Add("Day", now.Day);
            ts.Add("Hour", now.Hour);
            ts.Add("Minute", now.Minute);
            ts.Add("Second", now.Second);
            ts.Add("Millisecond", now.Millisecond);
            h.Add("Creator", Creator);

            var si = h.Add("SceneInfo", new FbxObjectName("GlobalInfo", "SceneInfo"), "UserData");
            si.Add("Type", "UserData");
            si.Add("Version", 100);
            var md = si.Add("MetaData");
            md.Add("Version", 100);
            foreach (var k in new[] { "Title", "Subject", "Author", "Keywords", "Revision", "Comment" }) md.Add(k, "");
            var p = new FbxProps70(si);
            string full = Path.GetFullPath(fbxPath);
            p.DocUrl("DocumentUrl", full);
            p.DocUrl("SrcDocumentUrl", full);
            p.Compound("Original");
            p.String("Original|ApplicationVendor", "Unity Technologies");
            p.String("Original|ApplicationName", "Unity");
            p.String("Original|ApplicationVersion", Application.unityVersion);
            p.DateTime("Original|DateTime_GMT", "01/01/1970 00:00:00.000");
            p.String("Original|FileName", full);
            p.Compound("LastSaved");
            p.String("LastSaved|ApplicationVendor", "Unity Technologies");
            p.String("LastSaved|ApplicationName", "Unity");
            p.String("LastSaved|ApplicationVersion", Application.unityVersion);
            p.DateTime("LastSaved|DateTime_GMT", "01/01/1970 00:00:00.000");
            return h;
        }

        private static FbxNode BuildGlobalSettings()
        {
            var gs = new FbxNode("GlobalSettings");
            gs.Add("Version", 1000);
            var p = new FbxProps70(gs);
            p.Int("UpAxis", 1);
            p.Int("UpAxisSign", 1);
            p.Int("FrontAxis", 2);
            p.Int("FrontAxisSign", 1);
            p.Int("CoordAxis", 0);
            p.Int("CoordAxisSign", 1);
            p.Int("OriginalUpAxis", 1);
            p.Int("OriginalUpAxisSign", 1);
            p.Double("UnitScaleFactor", 100.0); // 1 file unit = 100 cm = 1 Unity meter
            p.Double("OriginalUnitScaleFactor", 100.0);
            p.ColorRGB("AmbientColor", 0, 0, 0);
            p.String("DefaultCamera", "Producer Perspective");
            p.Enum("TimeMode", 6); // 30 fps
            p.Time("TimeSpanStart", 0);
            p.Time("TimeSpanStop", 46186158000L);
            p.Double("CustomFrameRate", -1.0);
            return gs;
        }

        private FbxNode BuildDocuments()
        {
            var docs = new FbxNode("Documents");
            docs.Add("Count", 1);
            var doc = docs.Add("Document", NewId(), "Scene", "Scene");
            var p = new FbxProps70(doc);
            p.Object("SourceObject");
            p.String("ActiveAnimStackName", "");
            doc.Add("RootNode", 0L);
            return docs;
        }

        private FbxNode BuildDefinitions()
        {
            var defs = new FbxNode("Definitions");
            defs.Add("Version", 100);
            int total = 1;
            foreach (var kv in _counts) total += kv.Value;
            defs.Add("Count", total);
            defs.Add("ObjectType", "GlobalSettings").Add("Count", 1);
            foreach (var kv in _counts) defs.Add("ObjectType", kv.Key).Add("Count", kv.Value);
            return defs;
        }

        // ================================================================== helpers

        private long NewId() => _nextId++;

        private FbxNode AddObject(string type, long id, FbxObjectName name, string subclass)
        {
            _counts.TryGetValue(type, out int c);
            _counts[type] = c + 1;
            return _objects.Add(type, id, name, subclass);
        }

        private void ConnectOO(long child, long parent) => _connections.Add("C", "OO", child, parent);
        private void ConnectOP(long child, long parent, string prop) => _connections.Add("C", "OP", child, parent, prop);
    }
}
