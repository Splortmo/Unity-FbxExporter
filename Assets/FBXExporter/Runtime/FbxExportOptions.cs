using System;
using System.Collections.Generic;

namespace FbxExporter
{
    public enum FbxFormat
    {
        /// <summary>Binary FBX 7.4. Works in Blender, 3ds Max, Maya, Unity, etc. Recommended.</summary>
        Binary,
        /// <summary>ASCII FBX 7.4. Human readable; works in Autodesk apps but NOT in Blender.</summary>
        Ascii
    }

    [Serializable]
    public sealed class FbxExportOptions
    {
        public FbxFormat Format = FbxFormat.Binary;

        /// <summary>
        /// Optional filter on GameObject names. Return true to skip an object; it is not exported and neither
        /// is anything below it. Example: <c>name =&gt; name.EndsWith("_helper")</c>.
        /// Not serialized (code only), so it is not saved with the Editor settings.
        /// </summary>
        public Func<string, bool> IgnoredNameFilter { get; set; }

        /// <summary>Include inactive GameObjects (exported with Visibility = 0).</summary>
        public bool IncludeInactive = true;

        /// <summary>Keep the world placement of the exported roots. When false, every root is placed at the origin.</summary>
        public bool PreserveWorldTransform = true;

        /// <summary>
        /// false (default): keep the pose the skeleton has at export time (snapshot).
        /// true: write bones in their bind pose (T-pose/A-pose the mesh was skinned in) and reset blend shape
        /// weights to 0. Only bones the skinned meshes use (and nodes between them) are affected.
        /// </summary>
        public bool ExportInBindPose = false;

        /// <summary>Export material textures as image files.</summary>
        public bool ExportTextures = true;

        /// <summary>Embed textures inside the FBX (binary only). External files are not written when embedding.</summary>
        public bool EmbedTextures = false;

        /// <summary>
        /// Folder (relative to the FBX file) that receives exported textures.
        /// "{name}" is replaced by the FBX file name without extension.
        /// </summary>
        public string TextureFolder = "{name}_Textures";

        /// <summary>In the Editor, copy the original source image file (png/jpg/tga...) instead of re-encoding when possible.</summary>
        public bool PreferSourceTextureFiles = true;

        /// <summary>Merge vertices that Unity split for UV seams / hard edges back into shared control points.</summary>
        public bool WeldVertices = true;

        /// <summary>Export blend shapes (morph targets) of skinned meshes.</summary>
        public bool ExportBlendShapes = true;

        /// <summary>Export vertex colors when the mesh has them.</summary>
        public bool ExportVertexColors = true;

        /// <summary>Testing aid: always read meshes and textures the way a player build without Read/Write access would.</summary>
        public bool ForceRuntimeReadback = false;
    }

    public sealed class FbxExportResult
    {
        public bool Success;
        public string FilePath;
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> TextureFiles = new List<string>();
        public int ModelCount, MeshCount, SkinnedMeshCount, MaterialCount, TextureCount, BoneCount;
        public Exception Error;

        public override string ToString()
        {
            if (!Success) return "FBX export failed: " + (Error != null ? Error.Message : "unknown error");
            return $"Exported {FilePath}: {ModelCount} nodes, {MeshCount} meshes ({SkinnedMeshCount} skinned), " +
                   $"{BoneCount} bones, {MaterialCount} materials, {TextureCount} textures, {Warnings.Count} warnings";
        }
    }
}
