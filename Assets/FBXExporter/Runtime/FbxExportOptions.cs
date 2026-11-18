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
        /// Returns true for GameObject names to skip, along with their children.
        /// Example: <c>name =&gt; name.EndsWith("_helper")</c>. Not serialized.
        /// </summary>
        public Func<string, bool> IgnoredNameFilter { get; set; }

        /// <summary>Include inactive GameObjects (exported with Visibility = 0).</summary>
        public bool IncludeInactive = true;

        /// <summary>Keep the world placement of the exported roots. When false, every root is placed at the origin.</summary>
        public bool PreserveWorldTransform = true;

        /// <summary>
        /// false: export the skeleton in its current pose.
        /// true: export bones in their bind pose and reset blend shape weights to 0.
        /// </summary>
        public bool ExportInBindPose = false;

        /// <summary>Export material textures as image files.</summary>
        public bool ExportTextures = true;

        /// <summary>Embed textures in the FBX instead of writing files. Binary format only.</summary>
        public bool EmbedTextures = false;

        /// <summary>
        /// Texture folder, relative to the FBX file. "{name}" becomes the FBX file name without extension.
        /// </summary>
        public string TextureFolder = "{name}_Textures";

        /// <summary>Editor only: copy the original image file instead of re-encoding the texture. Players always re-encode.</summary>
        public bool PreferSourceTextureFiles = true;

        /// <summary>Merge vertices split by UV seams or hard edges.</summary>
        public bool WeldVertices = true;

        /// <summary>Export blend shapes (morph targets) of skinned meshes.</summary>
        public bool ExportBlendShapes = true;

        /// <summary>Export vertex colors when the mesh has them.</summary>
        public bool ExportVertexColors = true;

        /// <summary>For testing: read meshes and textures through the GPU path used for non-readable assets.</summary>
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
