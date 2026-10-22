# Blender-to-Unity LOD Methodology

## Scope and contract

This document specifies the static-mesh LOD path from Blender Bridge through FBX export and Unity Prefab creation. LOD mesh generation is optional and on demand; selecting the meshes to export is a separate step. Users may assign meshes made by other Blender tools, generate candidate meshes with Blender Bridge's **Automatic LOD** button, or combine both. Texture baking remains a separate pipeline stage.

Each LOD set belongs to a source/root object and has four optional Blender object-selector fields: LOD0, LOD1, LOD2, and LOD3. Only objects assigned to these fields are exported. An empty field omits that level; gaps are allowed, and at least one field must be populated. The assigned objects are the export selection, regardless of how they were created. Preserve source and assigned scene objects. Unity creates no LOD group when only one distinct runtime mesh is emitted.

## 1. Configure per-level triangle limits

Expose four positive integer maximums in the Blender Bridge add-on UI. These defaults are editable and apply both to generated candidates and to meshes assigned for export:

| LOD | Maximum triangles per source mesh |
| --- | ---: |
| LOD0 | 100,000 |
| LOD1 | 30,000 |
| LOD2 | 10,000 |
| LOD3 | 5,000 |

The limits are **maximums**, not desired exact counts. Count all triangles in a mesh together, including all material submeshes. Do not pool counts across separate LOD sets or objects. Validate before generation or export that values are positive and strictly descending: `LOD0 > LOD1 > LOD2 > LOD3`.

The **Automatic LOD** button operates on the LOD set's source/root mesh object. Evaluate its enabled modifier stack and use the resulting triangle count as the baseline, `T`. It generates persistent, selectable candidate objects; it does not assign them to the LOD fields or change the export selection. The user chooses which objects to place in those fields. This keeps generation optional and lets manually authored LODs be used without passing through the generator.

| Baseline count | Automatic LOD candidates |
| ---: | --- |
| `T <= 5,000` | No reduced candidates; use the source object directly in an assigned field if desired |
| `5,000 < T <= 10,000` | LOD3 |
| `10,000 < T <= 30,000` | LOD2 and LOD3 |
| `30,000 < T <= 100,000` | LOD1, LOD2, and LOD3 |
| `T > 100,000` | LOD0, LOD1, LOD2, and LOD3 |

Generate a candidate for a level only when its cap is lower than `T`. Thus, Automatic LOD does not create redundant copies for levels whose cap already accommodates the source. When `T` exceeds the LOD0 cap, the LOD0 candidate is reduced too. Generated candidates are durable Blender scene objects so they can be inspected, edited, and selected in the object fields. Give them deterministic names and mark them as Blender Bridge-generated. Re-running generation for the same source may replace only its prior generated candidates that are not currently assigned in an LOD field. Preserve assigned candidates and create a new uniquely named candidate rather than replacing an object the user has selected.

The add-on UI should show the effective limits used for generation and export. Invalid limits should stop the operation before generation or export changes scene data or creates files, and report which value violates the rule.

## 2. Generate persistent LOD candidates

Never apply a modifier to, replace the mesh data of, or otherwise mutate the source object. Keep it intact for manual selection, later generation, and texture-bake work.

When the user invokes Automatic LOD for a source/root mesh:

1. Record the source object and data-block references, world transform, hierarchy parent, material slots, UV layers, and enabled modifier settings needed for export.
2. Evaluate the source's enabled modifier stack and count its triangles. If evaluation fails, abort with the source object name; do not fall back to the base mesh count.
3. Create a persistent candidate object with private mesh data for each required LOD. Do not use a temporary export-only object as the generated result.
4. Preserve the source's mesh attributes, material indices, UV layers, transform, and supported modifier stack on each candidate.
5. Add a Collapse-mode Decimate modifier after the preserved source modifiers. Set its ratio on the candidate only.
6. Evaluate and count the candidate. Keep the best non-empty candidate whose measured count is at or below that LOD's cap.
7. Leave the LOD0–LOD3 object fields unchanged. Report the generated candidate names so the user can assign the desired meshes.

Generate every reduced LOD directly from the same source object and modifier state. Do not make LOD2 by decimating LOD1 or LOD3 by decimating LOD2; chained reduction compounds topology loss and makes each cap depend on all earlier results.

Treat one Automatic LOD run as an atomic operation: validate all requested candidates before publishing them as the new generated set. If generation fails, remove only objects created by that failed run and preserve prior candidates, the source, and all assignments.

The Blender Decimate ratio is a reduction input, not an exact triangle target. The manual describes the ratio in terms of faces, with triangles used in its calculation, so the generator must measure the evaluated output rather than infer a count from the ratio. See the [Blender 5.0 Decimate Modifier documentation](https://docs.blender.org/manual/en/5.0/modeling/modifiers/generate/decimate.html).

### Ratio search

Use a bounded search rather than an unbounded loop or a single guessed ratio:

1. Start with a ratio estimate based on `cap / T`, clamped to the valid Decimate ratio range.
2. Evaluate the candidate and count actual triangles.
3. If it exceeds the cap, lower the ratio to search for a valid candidate. If it is under the cap, retain it as the current best and test a higher ratio to reduce visual loss while staying within budget.
4. Narrow the ratio interval using measured over-cap and under-cap results. Re-evaluate each candidate; never assume the ratio maps linearly to triangle count.
5. Stop at the implementation's fixed maximum evaluation count or when the ratio interval is sufficiently small. Keep the highest-ratio non-empty candidate measured under the cap.

If the bounded search cannot produce a non-empty candidate within budget, fail generation with the source object, LOD, measured count (if available), cap, and reason. Do not silently create an over-budget candidate or an empty mesh. The iteration bound is an internal safety limit, not another user-facing triangle budget.

## 3. Validate assigned meshes and generated candidates

Count triangles after the full object modifier stack has been evaluated. In Blender Python, get the evaluated object from the dependency graph, create a temporary evaluated mesh, calculate its loop triangles, count those triangles, and release the temporary mesh in a `finally` block. See Blender's [Dependency Graph API](https://docs.blender.org/api/5.0/bpy.types.Depsgraph.html) and [Object API](https://docs.blender.org/api/5.0/bpy.types.Object.html).

For each generated candidate and each assigned export level, retain a validation record containing:

- Source object identity/name, assigned object identity/name, and LOD index.
- Configured triangle cap.
- Evaluated triangle count and vertex count.
- Decimate ratio used for generated candidates, or not applicable for manually assigned meshes.
- Whether a level was omitted and why.

Enforce the following checks before export:

- Every assigned object is a valid, non-empty mesh and has no more triangles than the cap for its field.
- Assigned levels are strictly decreasing in triangle count when sorted by LOD number. If two assigned levels are redundant or out of order, fail with an actionable diagnostic rather than silently changing the user's assignments.
- A mesh object may be assigned to at most one LOD field in a set; reject duplicate assignments.
- Generated candidates and assigned objects remain separate from export staging. Generation does not alter the source or pre-existing assignments.
- Preserve user objects and their mesh data, transforms, modifier values, material slots, selection, and active-object state through generation and export, except for the intentional creation/replacement of the bridge-owned generated candidates during Automatic LOD.

If candidate validation fails, cancel generation and report the source, LOD, measured result, expected cap, and corrective action. If assigned-mesh validation fails, cancel export and report the assigned object, LOD, measured result, expected cap, and corrective action. Generation, validation, and export errors must remain visible to the user.

## 4. Stage a deterministic FBX hierarchy

Use an export-only collection or equivalent staging container, with deterministic names so Unity can discover levels without guessing from object order. For each LOD set, stage only the objects assigned in its fields using this logical shape:

```text
<ExportRoot>
  <SourceMeshName>_LODGroup
    LOD0
      <SourceMeshName>_Mesh
    LOD1
      <SourceMeshName>_Mesh
    LOD2
      <SourceMeshName>_Mesh
    LOD3
      <SourceMeshName>_Mesh
```

Only include LOD child nodes for populated fields. Preserve each assigned object's material slots. Align the staged variants to a consistent LOD-set origin and bounds without modifying the assigned scene objects. Preserve distinct LOD sets rather than merging meshes or materials; this keeps triangle budgets independent and allows Unity to create one LODGroup per set.

When a level is omitted, keep its original numeric name on the FBX node. Unity must sort the names numerically and map the emitted levels to a compact ordered LOD array. For example, emitted `LOD0`, `LOD2`, and `LOD3` become Unity array levels 0, 1, and 2 in that order; the node name records provenance, not a required array index.

Export only staged objects. Always clean up temporary staging objects and their private mesh data in a `finally` path, including when validation or FBX export raises an exception. Do not clean up persistent generated candidates. Restore the pre-operation selection and active object. Do not remove or modify user-assigned objects, generated candidates, or pre-existing user collections. If export fails, leave no partial staged hierarchy in the scene and surface the error.

## 5. Import and assemble the Unity LODGroup

The Unity Editor bake should:

1. Import the FBX and validate that every generated group has exactly one renderer set per emitted LOD node.
2. Parse each `<SourceMeshName>_LODGroup` independently. Reject duplicate, malformed, or ambiguous LOD node names rather than relying on hierarchy traversal order.
3. Sort emitted LOD nodes by their numeric suffix. Resolve all `MeshRenderer` components under each node and assign that renderer array to that level.
4. Create an `LODGroup` on the corresponding source-object group root. Do not create one if only a single level exists; preserve that mesh as a normal static renderer.
5. Apply the configured screen-relative transition values, recalculate/validate group bounds, and save the Prefab.
6. Validate the imported meshes again: sum triangle counts across all submeshes for each source object and LOD, and fail if an imported result exceeds the matching cap.

The current Unity bake rejects models containing an `LODGroup`; the implementation must replace that rejection with validation of the generated, supported LOD hierarchy. Continue rejecting unsupported animated or skinned content. Preserve existing material assignment and collider policy consistently for the emitted renderer objects.

Unity LOD transitions are based on screen-relative size, not a direct PC/console/mobile platform switch. Expose three ordered transition thresholds in settings for the transitions after LOD0, LOD1, and LOD2; require `1.0 > threshold0 > threshold1 > threshold2 > 0.0`. A threshold belongs to the emitted level it follows: if LOD1 is omitted, an LOD0-to-LOD2 transition uses the threshold after LOD0, and an LOD2-to-LOD3 transition uses the threshold after LOD2. Ignore thresholds for omitted levels. Any platform-specific budget profile or platform-based asset selection is a separate feature.

The transition controls need documented initial defaults and should be tunable per project. Choose the defaults against representative assets and camera distances, then visually test popping and profile rendering cost; do not claim that a universal set of values fits all projects. Unity 6.3 documents explicit screen-size transitions and renderer sets for LOD Groups, and contrasts them with Mesh LOD's lower-overhead import-time approach. See [Unity 6.3 LOD documentation](https://docs.unity3d.com/6000.3/Documentation/Manual/LevelOfDetail.html).

## 6. Failure handling and acceptance tests

The bake must fail before creating the final Prefab when settings are invalid, an assigned object is invalid or exceeds its cap, the FBX hierarchy is malformed, or Unity's imported count exceeds budget. Include asset path, LOD-set name, assigned mesh, LOD, measured count, and configured maximum in actionable errors. Temporary Blender staging data must be cleaned up on every failure path. A generation failure must leave the source and prior assignments intact; a failed rerun must not remove manually authored objects.

Minimum automated coverage:

| Case | Expected result |
| --- | --- |
| Automatic LOD source has 4,000 triangles | Generate no reduced candidates; leave assignments unchanged |
| Automatic LOD source has 8,000 triangles | Generate LOD3 only |
| Automatic LOD source has 20,000 triangles | Generate LOD2 and LOD3 |
| Automatic LOD source has 80,000 triangles | Generate LOD1, LOD2, and LOD3 |
| Automatic LOD source has 150,000 triangles | Generate four candidates at or below their respective caps |
| User assigns meshes from another LOD tool | Export the assigned objects without applying bridge Decimate generation |
| One or more LOD fields are empty | Omit those nodes; allow gaps and sort emitted levels numerically |
| A manually assigned mesh is invalid, duplicated, unordered, or over cap | Fail export with diagnostics; preserve the user's assignments |
| Candidate cannot produce a valid under-cap result | Fail generation with diagnostics; do not create an invalid candidate |
| Unity FBX import changes triangulation/counts | Unity bake catches the cap violation and does not save a success-shaped Prefab |
| Any generation/export failure | Source objects, manual assignments, and pre-existing scene objects remain unchanged; temporary staging is removed |

Blender-side counts are the first validation gate; Unity-imported counts are authoritative for the Prefab output. Keep both measurements in the bake report so a mismatch can be diagnosed.

## 7. Quantize UV0 to UNorm16 and enforce texel error

The runtime UV policy is:

- UV0 is expected to use the single-tile range `[0,1]`.
- Store UV0 as two-component `UNorm16` by default.
- Accept the conversion only when the maximum per-axis reconstruction error is at most `0.25` texel at the largest texture resolution referenced by the mesh's materials.
- If the check fails, fail the bake and report the mesh, UV coordinate, relevant texture size, measured error, and allowed error. Permit `Float32` only when an explicit per-asset or project override is selected before baking; this is a deliberate configuration, not an automatic fallback.
- Preserve UV channels other than UV0 in their existing format unless a separate validated policy is introduced for them.

`UNorm16` represents a normalized coordinate using integer values from 0 through 65,535. After validating the range, quantize each UV0 component by rounding `uv * 65,535` to the nearest integer, then reconstructing `q / 65,535`. Do not clamp UVs; reject any out-of-range source value with the object and UV-layer name. Non-finite values are also invalid.

For a texture of width `W` and height `H`, measure the reconstructed error as:

```text
error_texels = max(abs(u_original - u_reconstructed) * W,
                   abs(v_original - v_reconstructed) * H)
```

When materials reference textures with different sizes, evaluate each applicable texture size and enforce the threshold against the worst result. The shared texture policy in Section 8 uses a maximum base texture dimension of 2,048, so UV validation must include that maximum even when a particular source image is smaller. If the project changes the maximum, repeat UV validation against the new maximum. A half-step rounding error for UNorm16 is approximately `0.5 / 65,535` UV units per axis; at a 2,048-pixel dimension that is approximately `0.016` texel per axis. The measured reconstruction check, not this theoretical bound, determines acceptance.

### Unity runtime mesh conversion

Do not assume Blender's in-memory UV storage or FBX serialization controls the Unity runtime vertex format. First import the FBX, then create persistent runtime `Mesh` assets for the Prefab that use an explicit vertex-buffer layout. Place position, normal, and tangent attributes in the position/geometry stream; place UV0 in a separate attribute stream using `UNorm16` with dimension 2. A two-component UNorm16 attribute occupies 4 bytes per vertex instead of 8 bytes for two Float32 UV components. This saves 4 bytes per vertex for UV0, not 50% of total mesh memory.

Copy all required vertex attributes, indices, submesh descriptors, bounds, and material assignments when creating the optimized mesh. Preserve vertex ordering and all topology relationships. Reconstruct the quantized UV values and run the texel-error test before uploading the optimized mesh. Keep UV1 and any additional channels unchanged. If the mesh layout cannot be represented safely, the conversion fails unless the explicit Float32 override is enabled.

The Unity runtime mesh must be the mesh referenced by the baked Prefab; validating a temporary candidate while leaving the FBX-imported Float32 mesh assigned would not meet the policy. Store optimized meshes as project assets, update `MeshFilter.sharedMesh` references in the Prefab, and verify the saved/reloaded Prefab's actual attribute format, dimension, and stream. Check target device support for the chosen vertex format and fail with platform/device details if unsupported.

Unity documents custom vertex layouts through `VertexAttributeDescriptor` and `Mesh.SetVertexBufferParams`. It notes that custom layouts are advanced and receive limited validation, and that attribute format/dimension combinations must satisfy layout constraints. Verify that the selected layout is supported and correctly uploaded on each supported target. See [Unity 6.3 VertexAttributeDescriptor](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Rendering.VertexAttributeDescriptor.html), [VertexAttributeFormat](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Rendering.VertexAttributeFormat.html), [Mesh.SetVertexBufferParams](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.SetVertexBufferParams.html), [Mesh.GetVertexAttributeFormat](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.GetVertexAttributeFormat.html), and [SystemInfo.SupportsVertexAttributeFormat](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SystemInfo.SupportsVertexAttributeFormat.html).

Minimum UV-conversion coverage:

| Case | Expected result |
| --- | --- |
| UV0 contains 0 or 1 | Quantize and reconstruct without leaving `[0,1]`; include both range endpoints |
| UV0 contains a non-finite or out-of-range value | Fail with the mesh and UV-layer name; do not clamp silently |
| Maximum per-axis reconstruction error is within 0.25 texel at the largest configured texture size | Save the UNorm16 mesh and verify the persisted UV0 format, dimension 2, and stream |
| Quantization exceeds 0.25 texel at the configured maximum texture size | Fail unless the explicit Float32 override is enabled |
| A platform does not support the UV0 format/dimension | Fail that target validation and identify the platform |
| Prefab save/reload | Confirm the Prefab references the optimized mesh asset, not the original imported Float32 mesh |

## 8. Configure texture resolution and compression per platform

Keep original texture source files intact in the Unity project. Do not destructively resize or pre-compress the staged Blender images. The default imported base resolution is a maximum of 2,048 × 2,048 pixels (preserving the source aspect ratio); source images smaller than this remain at their source dimensions. Apply platform-specific GPU compression through Unity texture import settings for PC/console, mobile, and modern WebGL 2 browser builds. The 2,048 maximum is shared across LODs and is not a separate resolution for each level.

All LODs of a source object reference the same material and the same texture asset set. Do not create or assign additional 512 × 512 or other lower-resolution texture files to LOD2/LOD3. Enable mipmap generation for these 3D surface textures. The GPU selects lower mip levels according to texture sampling footprint as the object occupies less screen space; LODGroup renderer switching and mip selection remain independent mechanisms.

Configure limits in the Unity-side Blender Bridge settings asset, grouped by target platform and texture role:

| Role | Color-space / import intent | Compression profile |
| --- | --- | --- |
| Base Color (and color/emission maps when used as color) | sRGB | Configurable per target |
| Normal | Normal-map import, linear data | Configurable per target |
| Linear Data (AO, roughness, metallic, masks) | Linear, not sRGB | Configurable per target |

Each role/platform profile includes:

- Whether a platform override is enabled and the target platform identifier.
- The shared 2,048 maximum imported dimension, preserving aspect ratio and leaving the source image unchanged. A platform may use a lower imported maximum only through an explicit platform-wide profile change; it must apply consistently across LODs and must be recorded as a change to the shared-resolution policy.
- Requested compression format and quality settings supported by that target.
- Mipmap generation enabled for 3D mesh surface maps.
- Required alpha-channel behavior where the material uses alpha.
- A role-specific quality-validation threshold calibrated by the project.

Classify textures from their actual material-slot/node usage first. Filename suffix hints can assist when usage is unavailable, but are not authoritative when they conflict with material use. Treat one texture used simultaneously in incompatible roles (for example, sRGB base color and linear normal data) as an error; require separate source texture assets rather than selecting one ambiguous importer configuration.

Use explicit Unity platform texture importer overrides rather than relying on whichever defaults happen to be active in a project. The initial format policy is:

| Build target | Preferred format | Compatibility rule |
| --- | --- | --- |
| Standalone desktop (Windows/macOS/Linux) | DXT5/BC3 for RGBA textures | Unity recommends DXT5 as a faster-to-compress desktop RGBA option. A different format requires an intentional profile change and validation. |
| Android/iOS | ASTC | Define the minimum supported device/GPU capability and validate it. Devices that do not support the configured ASTC profile are unsupported; fail target validation with the device, role, and required format rather than substituting another format or relying on runtime decompression. |
| WebGL 2 desktop browsers | DXT/S3TC family (DXT5 for RGBA) | Validate on supported desktop browsers and graphics APIs. |
| WebGL 2 mobile browsers | ASTC | Declare the supported browser/device capability baseline and validate it. If a required browser/device cannot support the configured format, fail that target profile with actionable diagnostics; do not substitute ETC2 or rely on runtime decompression. |

These are separate build-target import profiles, not `#if UNITY_STANDALONE` / `#if UNITY_ANDROID` runtime branches. `#if` controls compiled code; it does not choose how Unity imports a texture asset. Declare the supported browser/device matrix for each WebGL profile. If a required browser class cannot support the profile's configured format, fail validation for that target rather than silently substituting a format or accepting runtime decompression.

Configure profiles independently for Base Color, Normal, and Linear Data maps. Keep sRGB/linear and normal-map importer roles correct regardless of compression format. Preserve alpha only when the material needs it; the selected format must support the required channels.

Set Unity's compressor quality setting explicitly per profile where the selected format/importer uses it. Use `80` as a provisional starting value, not as “80% compression,” a bit rate, or a guaranteed visual-quality score. It is a compressor quality/effort control; the format and—for ASTC—the block footprint determine the storage/quality tradeoff. ASTC block sizes must be explicit profile settings and calibrated by role. Do not claim a final quality result from the numeric quality setting alone.

Verify after reimport that the effective platform override, format, quality setting, ASTC block footprint when applicable, alpha behavior, dimensions, color-space/normal-map role, and mipmap state match the profile. Inspect imported outputs and build for each supported target. Any missing or unsupported format/capability, or any mismatch from the declared profile, must fail preflight or target validation with the asset path, target, role, expected setting, and actual setting/capability. Do not silently substitute a format, use importer defaults, or continue to a successful bake/build. Unity's references are [Choose a GPU texture format by platform](https://docs.unity3d.com/6000.3/Documentation/Manual/texture-choose-format-by-platform.html), [GPU texture formats reference](https://docs.unity3d.com/6000.3/Documentation/Manual/texture-formats-reference.html), [TextureImporterPlatformSettings](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/TextureImporterPlatformSettings.html), [platform maximum texture size](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/TextureImporterPlatformSettings-maxTextureSize.html), and [texture formats in memory](https://docs.unity3d.com/6000.3/Documentation/Manual/texture-compression-formats.html).

### Resolution caps and aggregate memory budget

The initial shared-resolution rule is a 2,048-pixel maximum for both dimensions, with aspect ratio preserved. This is the starting profile selected for this workflow, not an industry-wide optimum. Do not create per-LOD texture variants. If testing shows that a target platform cannot meet its memory budget, first measure compression and mip residency; changing the shared imported maximum is a deliberate platform-profile change, not automatic per-LOD downscaling. Calibrate platform formats, quality settings, and memory budgets using representative assets and the lowest-memory devices in each target class; record the project-approved values and device/build used to establish them.

Enforce both:

1. **Shared per-texture cap:** the effective imported width and height must not exceed 2,048, while preserving aspect ratio. Verify that all LOD renderers for the source object reference the same texture assets; there is no per-LOD resolution assignment.
2. **Per-asset runtime texture-memory budget:** sum the estimated imported GPU memory for the unique texture assets referenced by that asset's materials on the target platform. Count a shared texture once, not once per material slot or LOD renderer. Include the selected imported format, effective dimensions, and full mip-chain policy in the estimate; do not use the PNG/JPEG source file size as a proxy for GPU memory.

Record for each target profile the texture path/GUID, role, source dimensions, imported dimensions, selected format, mipmap state, estimated memory, and profile cap. For an asset over its size or memory budget, fail and report the largest contributing textures and measured totals. Do not automatically lower resolution, create lower-resolution LOD maps, change format/quality, or disable mipmaps to make an asset pass. A developer must revise the profile or explicitly approve a change to the shared imported resolution or compression settings, after which the importer and memory checks run again.

An import-time estimate is a linting gate, not a substitute for build measurements. Unity documents that a full mip chain adds about 33% texture storage compared with the base level; this is only a rough reference because compressed block formats and platform layouts affect actual allocation. Estimate from the actual imported format, dimensions, and mip levels, then validate representative platform builds and inspect runtime memory on target devices. Enabling mipmaps does not itself enable mipmap streaming or guarantee that unused mips are absent from memory. Keep each measurement associated with its platform profile so changes in Unity version, target platform, or importer settings trigger revalidation.

### Quality validation and reporting

Calibrate DXT5/BC3, ASTC block size, and compressor quality independently for Base Color, Normal, and Linear Data maps on each declared supported target. Use representative textures covering gradients, high-frequency detail, alpha where applicable, smooth normal variation, and grayscale data. Compare the imported output with the source using role-appropriate automated metrics and visually inspect representative assets in the target render pipeline. There are no universal quality thresholds; each production profile must declare a project-approved quality threshold. Until that threshold is calibrated, mark the profile incomplete and do not report a production pass; a visual-review-only exception requires explicit approval and must be recorded.

The validator must fail on missing platform settings, unsupported/unknown texture roles, incorrect color-space or normal-map import state, texture-size or memory-budget overruns, unavailable target formats, or a configured quality threshold miss. Reports should include asset path, texture path, target platform, role, expected and actual setting, source/import dimensions, selected format, quality result, estimated memory, and the relevant limit.

Minimum texture-profile coverage:

| Case | Expected result |
| --- | --- |
| One image is referenced by multiple materials in the same role | Count its memory once for the asset |
| Multiple LODs reference the same material set | Reuse the same texture assets; do not create per-LOD lower-resolution maps |
| 2,048 × 2,048 source texture with mipmaps | Import at no more than 2,048 per dimension, preserve aspect ratio, and include the mip chain in memory estimates |
| Source texture is smaller than 2,048 in either dimension | Do not upscale it |
| One image is used as both sRGB color and incompatible linear/normal data | Fail and request distinct source textures |
| Platform override is absent or does not match the configured role profile | Fail validation rather than use an unknown importer default |
| Imported dimension exceeds the role/platform maximum | Fail; do not alter the source image |
| Aggregate estimated memory exceeds the per-asset/platform budget | Fail with the total and largest contributing textures |
| A requested format is unsupported for a required target | Fail that target profile; do not silently substitute a format |
| Standalone desktop texture profile | Verify DXT5/BC3 is selected for RGBA imports and the build does not unexpectedly decompress it |
| Mobile ASTC profile on a device without ASTC support | Fail target validation with device, format, and role details; do not substitute another format or rely on implicit runtime decompression |
| WebGL 2 desktop and mobile browser profiles | Validate each declared profile against its required browser/device matrix; fail unsupported combinations with actionable diagnostics and do not substitute formats |
| Compressor quality setting is 80 | Treat it as a provisional compressor setting; require image-quality and memory validation rather than interpreting it as 80% compression |
| ASTC format selected | Verify the configured block footprint as well as quality setting and validate each map role |
| Texture quality threshold is configured and missed | Fail with role, platform, and measured quality result |
| Unity reimport or build changes effective format/dimensions/mips | Detect the changed result and require profile revalidation |

## 9. Combined asset validation report

For each source mesh and target platform, report at least:

- LOD index and imported triangle count versus its per-mesh cap.
- UV0 format, dimension, stream, source range, maximum measured texel error, and whether the Float32 override was used.
- Referenced unique textures and each texture's role, source/imported dimensions, platform format, mipmap state, and estimated memory.
- Confirmation that all LODs share the same texture asset references and that no per-LOD texture variants were generated.
- Aggregate estimated texture memory versus the per-asset budget.
- Pass/fail result and actionable failure reason.

Do not report a successful bake unless the saved Prefab references the validated optimized meshes and the texture importers have the verified platform settings. Blender-side data and pre-import estimates are diagnostic inputs; the Unity-imported mesh and platform-specific texture import results are the validation targets.
