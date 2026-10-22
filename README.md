# FBX Exporter (runtime + editor)

Standalone FBX exporter for Unity 6. No dependency on Autodesk's FBX SDK or the `com.unity.formats.fbx` package.
Works in the Editor and in player builds.

**Exports:** hierarchy, static meshes (MeshRenderer), skinned meshes (SkinnedMeshRenderer) with skeleton,
skin weights and bind pose, blend shapes, multiple materials per mesh, UVs (all channels), normals, vertex colors,
and textures (base color, normal map, emission) with tiling/offset.

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

Or add the **Runtime FBX Exporter** component, fill in `targets`, and call `ExportNow()` (e.g. from a UI button).
Relative output paths are resolved against `Application.persistentDataPath`. Must be called on the main thread.

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
| `ForceRuntimeReadback` | false | testing: behave like a player build without Read/Write access |

## Notes

- Units: 1 Unity unit = 1 m. Axes are converted to FBX's right-handed Y-up, so the importer's axis
  conversion (e.g. Blender Z-up) places the model correctly.
- Skinned meshes are exported in their **current pose**, with the original bind pose as the rest pose.
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

## AI disclosure

This project was developed with the assistance of AI (Claude by Anthropic). Code was AI-assisted and reviewed and tested by the author.
