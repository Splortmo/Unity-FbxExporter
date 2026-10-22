using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FbxExporter
{
    /// <summary>
    /// Drop-in component for exporting at runtime (in a player build or Play Mode).
    /// Call <see cref="ExportNow"/> from a UI button, or use the component's context menu.
    /// </summary>
    [AddComponentMenu("FBX Exporter/Runtime FBX Exporter")]
    public sealed class RuntimeFbxExporter : MonoBehaviour
    {
        [Tooltip("Objects to export. Their whole child hierarchy is included. Empty = this GameObject.")]
        public List<GameObject> targets = new List<GameObject>();

        [Tooltip("Output file. Relative paths are resolved against Application.persistentDataPath.")]
        public string outputPath = "Export/export.fbx";

        public FbxExportOptions options = new FbxExportOptions();

        public FbxExportResult LastResult { get; private set; }

        [ContextMenu("Export Now")]
        public FbxExportResult ExportNow()
        {
            var list = new List<GameObject>();
            foreach (var t in targets) if (t != null) list.Add(t);
            if (list.Count == 0) list.Add(gameObject);

            string path = Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(Application.persistentDataPath, outputPath);

            LastResult = FbxExport.Export(path, list, options);
            if (LastResult.Success) Debug.Log(LastResult.ToString(), this);
            else Debug.LogError(LastResult.ToString(), this);
            foreach (var w in LastResult.Warnings) Debug.LogWarning("FBX Export: " + w, this);
            return LastResult;
        }
    }
}
