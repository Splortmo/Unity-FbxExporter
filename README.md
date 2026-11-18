# FBX Exporter (runtime + editor)

Standalone FBX exporter for Unity. No dependency on Autodesk's FBX SDK or the `com.unity.formats.fbx` package.
Works in the Editor and in player builds, including WebGL.

**Exports:** hierarchy, static meshes (MeshRenderer), skinned meshes (SkinnedMeshRenderer) with skeleton,
skin weights and bind pose, blend shapes, multiple materials per mesh, UVs (all channels), normals, vertex colors,
and textures (base color, normal map, emission) with tiling/offset.

**Compatibility:** Unity 2022.3 LTS and newer (including Unity 6). Requires the .NET Standard 2.1 API level (the default) and the
built-in `imageconversion` module. The code uses Mesh APIs introduced in Unity 2021.2, so older versions are not supported.
Developed on Unity 6000.3.

**Format:** binary FBX 7.4 by default. This is what Blender, 3ds Max, Maya and Unity read.
ASCII FBX is available as an option. Autodesk apps read it, but **Blender cannot import ASCII FBX**.

## Editor

- `GameObject > Export to FBX...` or `Tools > FBX Exporter > Export Selected...`: exports the selected objects
  (each with its children) using the saved settings.
- `Tools > FBX Exporter > Export Window`: lets you change the settings, then export.

## Runtime

```csharp
using FbxExporter;

var result = FbxExport.Export("C:/Exports/character.fbx", new[] { character, prop },
                              new FbxExportOptions { EmbedTextures = false });
if (!result.Success) Debug.LogError(result.Error);
foreach (var w in result.Warnings) Debug.LogWarning(w);
```

`FbxExport.Export` is the only entry point, on every platform (see [WebGL](#webgl) for how it behaves there).
Relative paths are resolved against the current working directory, so for player builds pass an absolute path
(e.g. under `Application.persistentDataPath`). Must be called on the main thread.

## Options (`FbxExportOptions`)

| Option | Default | Notes |
|---|---|---|
| `Format` | Binary | `Ascii` for text output (not readable by Blender) |
| `IncludeInactive` | true | inactive objects are exported hidden |
| `PreserveWorldTransform` | true | false = each root at the origin |
| `ExportTextures` | true | |
| `EmbedTextures` | false | binary only; images packed inside the .fbx |
| `TextureFolder` | `{name}_Textures` | relative to the .fbx |
| `PreferSourceTextureFiles` | true | Editor only: copy the original png/jpg/tga instead of re-encoding |
| `WeldVertices` | true | merges vertices Unity split at UV seams so the mesh is connected in the DCC |
| `ExportBlendShapes` | true | |
| `ExportVertexColors` | true | |
| `ExportInBindPose` | false | false = keep the pose at export time; true = bones in bind pose and blend shape weights 0 |
| `IgnoredNameFilter` | null | `Func<string,bool>`, code only (not saved by the Editor window). Return true to skip an object and its children, e.g. `name => name.EndsWith("_helper")` |
| `ForceRuntimeReadback` | false | testing: behave like a player build without Read/Write access |

## Notes

- Units: 1 Unity unit = 1 m. Axes are converted to FBX's right-handed Y-up, so the importer's axis
  conversion (e.g. Blender Z-up) places the model correctly.
- Skinned meshes are exported in their **current pose** by default, with the original bind pose as the rest pose.
  Set `ExportInBindPose = true` to write the bones in the bind pose (and blend shape weights at 0) instead.
- In player builds, meshes without Read/Write enabled are read back from the GPU. Their blend shapes can't
  be read this way and are skipped (a warning is reported). Non-readable and compressed textures are
  rendered through a RenderTexture, and packed normal maps are converted back to standard RGB normal maps.
- If a skinned-mesh GameObject has child objects, it is exported as an empty node (keeping its name and
  children) plus a `<name>_Mesh` child holding the geometry. Importers re-parent skinned meshes under the
  armature, which would otherwise break the children.
- Unity stores a bind pose per mesh, while Blender keeps one rest pose per bone. The exporter therefore
  re-binds all meshes of a skeleton to one shared bind pose. Meshes bound with an offset skeleton (e.g. a
  head authored at the origin) are moved by that offset. Meshes sharing no bones with the rest (e.g. an
  ear prefab bound at runtime) are fitted through the hierarchy. The bind frame is matched to the armature
  node's current scale and position, and scale between a bone and its parent is removed from the rest pose,
  since Blender bones can't store it. The skinned result is unchanged. A warning is reported if a mesh's
  bind pose can't be fully reconciled; only vertices blended between the conflicting bones are affected.
- Select the common parent of a character's meshes and skeleton. Bones outside the exported hierarchy are
  skipped with a warning.

## WebGL

Use the same `FbxExport.Export` call. A page can't write into a folder, so in a WebGL player build the export is
written to the browser's temporary file system and then downloaded: a zip containing the `.fbx` and its texture folder
(or just the `.fbx` when there are no textures or they are embedded). Only the file name of the path is used.
Extract the zip into any folder and the texture links keep working.

```csharp
var result = FbxExport.Export("character.fbx", new[] { character }, new FbxExportOptions());
if (!result.Success) Debug.LogError(result.Error);
```

- `result.FilePath` is the downloaded file name (`character.fbx` or `character.zip`); `result.TextureFiles` is empty because the temporary files are deleted after the download.
- Meshes must have **Read/Write enabled**: WebGL can't read GPU buffers back, so non-readable meshes are skipped with a warning.
- The zip is built in memory (stored, not compressed), so very large exports need enough browser memory.
- In the Editor, also with the build target set to WebGL, `Export` writes to the given path like on desktop.

## Read/Write (non-readable) meshes

Meshes are never modified or converted: the exporter does not change import settings, so Read/Write does not
have to be enabled and is not enabled for you.

| Situation | Result |
|---|---|
| Editor | Exported normally. Non-readable meshes can still be read, no warning. |
| Player build, Read/Write on | Exported normally. |
| Player build, Read/Write off | Exported by reading the vertex and index buffers back from the GPU (with a warning). Geometry, normals, UVs, vertex colors, skin weights (up to 4 per vertex) and bind poses are kept. **Blend shapes are skipped.** |
| WebGL | Not supported: non-readable meshes are skipped with a warning. Enable Read/Write. |
| GPU readback unavailable | The mesh is skipped with a warning and its node is exported empty. |

To get blend shapes (or full skin weights) in a player build, enable **Read/Write** on those meshes' import settings.
The `ForceRuntimeReadback` option lets you test the GPU path inside the Editor.

## AI disclosure

This project was developed with the assistance of AI (Claude by Anthropic). Code was AI-assisted and reviewed and tested by the author.
