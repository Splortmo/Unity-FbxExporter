using System;
using System.IO;
using UnityEngine;

namespace FbxExporter.Samples
{
    [RequireComponent(typeof(Camera))]
    public sealed class UnityChanExportDemo : MonoBehaviour
    {
        [SerializeField] private GameObject character;
        [SerializeField] private TextAsset unityChanLicense;
        [SerializeField] private Vector3 pivot = new Vector3(0f, 0.82f, 0f);

        private static readonly string[] AnimationNames = { "Idle", "Hand Raise", "Run", "Victory" };

        private Camera orbitCamera;
        private Animator characterAnimator;
        private int animationIndex;
        private float yaw = 165f;
        private float pitch = 8f;
        private float zoom = 1f;
        private Vector2 licenseScroll;
        private GUIStyle wrappedLabel;
        private string licenseText;
        private string exportStatus = "Exports the pose on screen.";
        private const string Attribution = "© Unity Technologies Japan/UCL";

        private void Awake()
        {
            orbitCamera = GetComponent<Camera>();
            characterAnimator = character.GetComponent<Animator>();
            licenseText = unityChanLicense != null ? unityChanLicense.text : Attribution;
        }

        private void LateUpdate()
        {
            Rect panel = GetPanelRect();
            float covered = (Screen.height - panel.y) / Mathf.Max(1f, Screen.height);
            orbitCamera.orthographicSize = zoom * Mathf.Max(1.06f / (1f - covered), 0.78f / orbitCamera.aspect);

            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 target = character.transform.TransformPoint(pivot);
            // Center the model in the space above the bottom panel.
            Vector3 offset = new Vector3(0f, -covered * orbitCamera.orthographicSize, -4f);
            transform.SetPositionAndRotation(target + rotation * offset, rotation);
        }

        private void OnGUI()
        {
            Rect panel = GetPanelRect();
            wrappedLabel ??= new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };

            GUILayout.BeginArea(panel, GUI.skin.box);
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(Mathf.Min(250f, panel.width * 0.39f)));
            GUILayout.Label($"Animation: {AnimationNames[animationIndex]}", GUI.skin.box);
            GUILayout.Label("Drag to orbit · Scroll to zoom", wrappedLabel);
            if (GUILayout.Button("Next Animation", GUILayout.Height(24f)))
                NextAnimation();
            if (GUILayout.Button("Export FBX", GUILayout.Height(30f)))
                ExportCurrentPose();
            GUILayout.Label(exportStatus, wrappedLabel, GUILayout.ExpandHeight(true));
            GUILayout.EndVertical();

            GUILayout.BeginVertical();
            GUILayout.Label(Attribution);
            licenseScroll = GUILayout.BeginScrollView(licenseScroll, false, true);
            GUILayout.Label(licenseText, wrappedLabel);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            HandleOrbit(panel);
        }

        private static Rect GetPanelRect()
        {
            float width = Mathf.Min(720f, Screen.width - 24f);
            float height = Mathf.Min(160f, Screen.height * 0.46f);
            return new Rect((Screen.width - width) * 0.5f, Screen.height - height - 12f, width, height);
        }

        private void HandleOrbit(Rect panel)
        {
            Event pointer = Event.current;
            int control = GUIUtility.GetControlID(FocusType.Passive);
            bool overPanel = panel.Contains(pointer.mousePosition);

            switch (pointer.GetTypeForControl(control))
            {
                case EventType.MouseDown when pointer.button == 0 && !overPanel:
                    GUIUtility.hotControl = control;
                    pointer.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == control:
                    yaw = Mathf.Repeat(yaw + pointer.delta.x * 0.35f, 360f);
                    pitch = Mathf.Clamp(pitch - pointer.delta.y * 0.35f, -35f, 65f);
                    pointer.Use();
                    break;
                case EventType.MouseUp when pointer.button == 0 && GUIUtility.hotControl == control:
                    GUIUtility.hotControl = 0;
                    pointer.Use();
                    break;
                case EventType.ScrollWheel when !overPanel:
                    zoom = Mathf.Clamp(zoom * Mathf.Exp(pointer.delta.y * 0.06f), 0.65f, 1.8f);
                    pointer.Use();
                    break;
            }
        }

        private void NextAnimation()
        {
            animationIndex = (animationIndex + 1) % AnimationNames.Length;
            characterAnimator.Play($"Base Layer.{AnimationNames[animationIndex]}", 0, 0f);
        }

        private void ExportCurrentPose()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string path = "UnityChan.fbx";
#else
            string fileName = $"UnityChan-{DateTime.Now:yyyyMMdd-HHmmss-fff}.fbx";
            string path = Path.Combine(Application.persistentDataPath, "Exports", fileName);
#endif
            FbxExportResult result = FbxExport.Export(path, character);
            if (!result.Success)
            {
                exportStatus = "Export failed. See the Console for details.";
                Debug.LogError(result, this);
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            exportStatus = $"Downloaded {result.FilePath}";
#else
            exportStatus = $"Saved Exports/{Path.GetFileName(result.FilePath)}";
#endif
            Debug.Log(result, this);
            foreach (string warning in result.Warnings)
                Debug.LogWarning(warning, this);
        }
    }
}
