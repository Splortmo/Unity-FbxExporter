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

        /// <summary>Include inactive GameObjects (exported with Visibility = 0).</summary>
        public bool IncludeInactive = true;

        /// <summary>Keep the world placement of the exported roots. When false, every root is placed at the origin.</summary>
        public bool PreserveWorldTransform = true;

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
