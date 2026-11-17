using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace FbxExporter.Editor
{
    /// <summary>Editor UI: Tools > FBX Exporter, and GameObject > Export to FBX...</summary>
    public sealed class FbxExporterWindow : EditorWindow
    {
        private const string PrefsKey = "FbxExporter.Options";
        private const string LastDirKey = "FbxExporter.LastDir";

        private FbxExportOptions _options;
        private Vector2 _scroll;
        private string _lastReport;

        [MenuItem("Tools/FBX Exporter/Export Window", priority = 0)]
        public static void Open()
        {
            GetWindow<FbxExporterWindow>("FBX Exporter").Show();
        }

        [MenuItem("Tools/FBX Exporter/Export Selected...", priority = 1)]
        [MenuItem("GameObject/Export to FBX...", false, 30)]
        public static void ExportSelectedMenu()
        {
            ExportSelected(LoadOptions());
        }

        [MenuItem("Tools/FBX Exporter/Export Selected...", true)]
        [MenuItem("GameObject/Export to FBX...", true)]
        private static bool ValidateExportSelected() => Selection.gameObjects.Length > 0;

        internal static FbxExportOptions LoadOptions()
        {
            var o = new FbxExportOptions();
            string json = EditorPrefs.GetString(PrefsKey, "");
            if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, o);
            return o;
        }

        private static void SaveOptions(FbxExportOptions o) => EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(o));

        internal static FbxExportResult ExportSelected(FbxExportOptions options)
        {
            var selection = Selection.gameObjects
                .Where(g => g.scene.IsValid()) // scene objects only, not project assets
                .OrderBy(g => g.transform.GetSiblingIndex())
                .ToArray();
            if (selection.Length == 0)
            {
                EditorUtility.DisplayDialog("FBX Exporter", "Select one or more GameObjects in the Hierarchy first.", "OK");
                return null;
            }

            string dir = EditorPrefs.GetString(LastDirKey, Application.dataPath);
            string defaultName = selection.Length == 1 ? selection[0].name : "Export";
            string path = EditorUtility.SaveFilePanel("Export FBX", dir, defaultName + ".fbx", "fbx");
            if (string.IsNullOrEmpty(path)) return null;
            EditorPrefs.SetString(LastDirKey, Path.GetDirectoryName(path));

            FbxExportResult result;
            try
            {
                EditorUtility.DisplayProgressBar("FBX Exporter", "Exporting " + Path.GetFileName(path), 0.5f);
                result = FbxExport.Export(path, selection, options);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (result.Success) Debug.Log(result.ToString());
            else Debug.LogError(result.ToString() + "\n" + result.Error);
            foreach (var w in result.Warnings) Debug.LogWarning("FBX Export: " + w);

            // Refresh if we wrote into the project so the files show up.
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(Application.dataPath)))
                AssetDatabase.Refresh();
            return result;
        }

        private void OnEnable() => _options = LoadOptions();

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Format", EditorStyles.boldLabel);
            _options.Format = (FbxFormat)EditorGUILayout.EnumPopup("File Format", _options.Format);
            if (_options.Format == FbxFormat.Ascii)
                EditorGUILayout.HelpBox("Blender cannot import ASCII FBX files. Use Binary for Blender; both work in 3ds Max.", MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene", EditorStyles.boldLabel);
            _options.IncludeInactive = EditorGUILayout.Toggle("Include Inactive", _options.IncludeInactive);
            _options.PreserveWorldTransform = EditorGUILayout.Toggle("Keep World Position", _options.PreserveWorldTransform);
            _options.ExportInBindPose = EditorGUILayout.Toggle(new GUIContent("Export In Bind Pose", "Write bones in their bind pose and reset blend shapes, instead of the current pose."), _options.ExportInBindPose);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Meshes", EditorStyles.boldLabel);
            _options.WeldVertices = EditorGUILayout.Toggle("Weld Split Vertices", _options.WeldVertices);
            _options.ExportBlendShapes = EditorGUILayout.Toggle("Blend Shapes", _options.ExportBlendShapes);
            _options.ExportVertexColors = EditorGUILayout.Toggle("Vertex Colors", _options.ExportVertexColors);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Textures", EditorStyles.boldLabel);
            _options.ExportTextures = EditorGUILayout.Toggle("Export Textures", _options.ExportTextures);
            using (new EditorGUI.DisabledScope(!_options.ExportTextures))
            {
                using (new EditorGUI.DisabledScope(_options.Format != FbxFormat.Binary))
                    _options.EmbedTextures = EditorGUILayout.Toggle("Embed In FBX", _options.EmbedTextures);
                using (new EditorGUI.DisabledScope(_options.EmbedTextures && _options.Format == FbxFormat.Binary))
                    _options.TextureFolder = EditorGUILayout.TextField(new GUIContent("Texture Folder", "Relative to the FBX file. {name} = FBX file name."), _options.TextureFolder);
                _options.PreferSourceTextureFiles = EditorGUILayout.Toggle(new GUIContent("Copy Source Images", "Copy the original png/jpg/tga files when available instead of re-encoding."), _options.PreferSourceTextureFiles);
            }

            EditorGUILayout.Space();
            _options.ForceRuntimeReadback = EditorGUILayout.Toggle(new GUIContent("Simulate Player Build", "Read meshes/textures the way a player build without Read/Write access would (testing)."), _options.ForceRuntimeReadback);

            if (EditorGUI.EndChangeCheck()) SaveOptions(_options);

            EditorGUILayout.Space();
            int count = Selection.gameObjects.Length;
            using (new EditorGUI.DisabledScope(count == 0))
            {
                if (GUILayout.Button(count == 0 ? "Select GameObjects to export" : $"Export {count} Selected Object(s)...", GUILayout.Height(30)))
                {
                    var r = ExportSelected(_options);
                    if (r != null)
                        _lastReport = r.ToString() + (r.Warnings.Count > 0 ? "\n\n" + string.Join("\n", r.Warnings) : "");
                }
            }

            if (!string.IsNullOrEmpty(_lastReport))
                EditorGUILayout.HelpBox(_lastReport, MessageType.Info);

            EditorGUILayout.EndScrollView();
        }

        private void OnSelectionChange() => Repaint();
    }
}
