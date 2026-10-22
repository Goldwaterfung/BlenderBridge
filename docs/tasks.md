## Assessment

The methodology is a solid specification for the **explicit Blender → FBX → Unity Prefab bake**, not for live sync. That fits the project’s one-way workflow: keep LOD generation and texture optimization out of the live-preview protocol unless a later requirement calls for them.

The main integration points are:

- Blender’s add-on and FBX staging/export in `__init__.py` and `exporter.py` have no LOD UI, assignments, or staging today.
- Unity’s `BlenderFbxBaker.cs` currently rejects imported `LODGroup`s. Its texture handling already distinguishes some color, linear, and normal-map roles, but it does not implement the proposed platform profiles, size caps, mipmap checks, memory budgets, or quality validation.
- `BridgeSettings.cs` has no LOD, UV, or texture-profile settings.
- `MeshImporter.cs` builds live-preview meshes; the methodology calls for a separate, persistent optimized mesh asset in the saved Prefab.
- Existing automated coverage is primarily Unity Editor tests, including `BlenderFbxBakerTests.cs`. There are no Blender-side test files in the repository.

### Gaps to settle before implementation

1. **LOD caps need a Blender-to-Unity contract.** The methodology requires Unity to validate imported triangle counts against Blender’s per-level caps, but does not specify how those caps and LOD-set identities reach Unity. Define a manifest/metadata format and test that Unity can reliably read it; do not infer cap values from object names.
2. **Several production acceptance values are intentionally unspecified.** Choose project-approved LOD transition defaults, per-platform texture-memory budgets, image-quality thresholds, and the supported device/browser matrix. The provisional compressor quality value of `80` is not itself a quality threshold.
3. **Define the supported Unity/platform baseline.** The methodology references Unity 6.3 APIs, but the project’s README does not state a minimum Unity version. Confirm that before adopting custom mesh layouts and texture importer settings.
4. **Define staging transform semantics.** Specify how assigned meshes with different origins, parents, and transforms align within an LOD set. The FBX hierarchy and Unity renderer discovery depend on this being deterministic.

## Executable tasks by category

Tasks are ordered by dependencies. The work should remain confined to the explicit bake path; live preview and reverse synchronization are out of scope.

### 0. Cross-cutting contract and profile decisions

**Task 0 — Resolve bake metadata and production profiles**  
**Dependencies:** None.

- Define the Blender-to-Unity representation of LOD-set identity, emitted numeric LOD levels, per-level caps, and relevant Blender-side measurements.
- Decide the minimum supported Unity version, explicitly supported target platforms/devices/browsers, memory budgets, texture-quality thresholds, and initial transition values. Do not define runtime or importer fallbacks: unsupported targets must fail preflight with actionable diagnostics and must not produce a successful bake or build.
- Specify transform/origin alignment rules for staged LODs.

**Done when:** A sample export’s metadata can be read unambiguously by Unity, and each target profile has explicit values or is marked incomplete rather than passing silently.

### 1. Blender LOD authoring and validation

**Task 1 — Add LOD-set fields and cap settings to Blender**  
**Dependencies:** Task 0.

- Add LOD0–LOD3 object selectors to each source/root mesh set and expose the four editable triangle caps.
- Validate caps as positive and strictly descending before generation or export.
- Keep generation separate from assignment: the generator must not alter the selected LOD fields.

**Done when:** Empty fields, gaps, one populated level, invalid caps, and scene reloads behave as specified, with actionable Blender UI errors.

**Task 2 — Implement evaluated-mesh counting and assigned-level validation**  
**Dependencies:** Task 1.

- Count triangles and vertices from the fully evaluated modifier stack; release temporary evaluated meshes on all paths.
- Validate assigned meshes for type, non-emptiness, cap, unique assignment, and strictly decreasing triangle counts by LOD number.
- Produce diagnostic data containing the source, assigned object, level, measured count, and cap.

**Done when:** The validation rejects invalid assignments before export and leaves user objects, assignments, and scene state unchanged.

**Task 3 — Generate persistent automatic LOD candidates**  
**Dependencies:** Task 2.

- Evaluate the source once as the baseline and generate every required level directly from it, using candidate-only Decimate modifiers and bounded ratio search.
- Preserve source data and candidate attributes/materials; create durable, bridge-marked candidates without assigning them automatically.
- Make each run atomic: on failure remove only objects created by that run; on rerun do not replace assigned candidates.

**Done when:** The documented baseline bands generate exactly the required candidate levels, each candidate meets its cap, and failed runs preserve the source and prior assignments.

### 2. Blender FBX export

**Task 4 — Stage and export assigned LOD sets deterministically**  
**Dependencies:** Tasks 0 and 2. Task 3 is not required, so manually authored LODs work independently.

- Build the documented export-only hierarchy from assigned objects, preserving numeric level names and separate LOD sets.
- Apply the agreed transform/origin rules without modifying scene objects.
- Export only staged objects, include the contract metadata from Task 0, and clean staging in `finally` paths while restoring selection and active-object state.

**Done when:** Gaps are preserved in FBX names, staging cleanup works on export failure, and assigned Blender objects remain unchanged.

### 3. Unity LOD import and Prefab bake

**Task 5 — Preflight LOD metadata and imported triangle budgets**  
**Dependencies:** Task 4.

- Replace the current blanket rejection of LOD content with strict parsing and validation of the supported hierarchy/metadata.
- Reject malformed or ambiguous group/level names and imported counts above the corresponding caps before saving the final Prefab.
- Include FBX path, LOD-set name, level, measured count, and cap in failures.

**Done when:** Valid exports pass preflight and malformed metadata or Unity-side triangulation overruns fail without reporting a successful bake.

**Task 6 — Assemble and save Unity LODGroups**  
**Dependencies:** Task 5.

- Resolve renderer sets per emitted level, sort by numeric LOD suffix, and create an `LODGroup` for multiple levels only.
- Keep a single emitted level as a normal static renderer.
- Apply configured screen-relative transitions, validate bounds, and retain existing material and collider behavior.

**Done when:** Prefab save/reload preserves the expected levels and renderer sets, including sparse levels such as LOD0/LOD2/LOD3.

### 4. UV0 optimization

**Task 7 — Implement UV0 preflight and quantization validation**  
**Dependencies:** Task 0 and Unity imported meshes being available; can proceed independently of LODGroup assembly.

- Validate finite UV0 coordinates in `[0,1]`; do not clamp.
- Measure reconstructed UNorm16 error in texels against the configured maximum texture dimension and all applicable texture sizes.
- Support Float32 only through the explicit override; keep out-of-range UVs invalid.

**Done when:** Endpoint, non-finite, out-of-range, within-tolerance, over-tolerance, and override cases produce the specified result and diagnostics.

**Task 8 — Persist optimized Unity meshes and verify the saved Prefab references**  
**Dependencies:** Task 7.

- Build the explicit vertex layout with UV0 as two-component UNorm16 in its own stream; preserve required attributes, topology, bounds, and other UV channels.
- Check format support, save mesh assets, update Prefab mesh references, then verify the saved/reloaded asset’s format, dimension, and stream.
- Keep this implementation separate from the live-preview mesh importer.

**Done when:** The Prefab references the optimized mesh asset and verification fails clearly when the layout or target support is invalid.

### 5. Texture import and validation

**Task 9 — Add role-by-platform texture profiles**  
**Dependencies:** Task 0.

- Extend Unity settings with profiles for color, normal, and linear-data textures across explicitly supported targets.
- Configure explicit platform overrides, shared resolution maximum, mipmaps, quality/ASTC block settings where applicable, and required alpha behavior.
- Classify by actual material use; reject incompatible use of one image and do not resize or recompress source files destructively.

**Done when:** Reimported assets match the declared profile, smaller textures are not upscaled, and unsupported targets, missing/unknown settings, or conflicting configurations fail with the asset, target, role, expected setting, and actual capability/configuration. No importer default or alternate format is accepted as a fallback.

**Task 10 — Enforce texture budgets and quality gates**  
**Dependencies:** Task 9.

- Estimate memory from unique imported texture assets, effective format, dimensions, and mip policy; count shared textures once across materials and LODs.
- Enforce the shared dimension cap and approved per-asset/platform memory and quality limits.
- Validate representative builds/devices and make unsupported formats, unavailable device capabilities, or threshold misses explicit failures. Do not substitute another format or silently broaden the supported target matrix.

**Done when:** Over-budget assets report totals and largest contributors; quality is not marked passed until its project threshold is calibrated and measured; unsupported target profiles fail clearly without fallback.

### 6. End-to-end reporting, tests, and documentation

**Task 11 — Produce the combined validation report and close end-to-end coverage**  
**Dependencies:** Tasks 5–10.

- Report imported triangles versus caps, UV0 format/error/override, texture roles/settings/memory, shared texture references, and overall pass/fail.
- Do not report success unless the saved Prefab references the validated meshes and verified texture import settings.
- Add Blender integration coverage for generation/export/cleanup and Unity Editor coverage for FBX parsing, LODGroup assembly, UV layout, texture profiles, and report failures. Update the methodology and user documentation to match implemented behavior.

**Done when:** Representative Blender exports pass through Unity into a saved, reloaded Prefab and report; each required negative case fails with actionable diagnostics. The current Unity test suite is a starting point, but Blender-side test infrastructure will need to be added.

## Recommended checkpoints

1. **LOD checkpoint:** Tasks 0–6. Verify manually authored and generated LODs through FBX import and Prefab save/reload.
2. **UV checkpoint:** Tasks 7–8. Verify serialized mesh layout and platform support independently of texture compression.
3. **Texture checkpoint:** Tasks 9–10. Validate each declared platform profile using representative assets and target builds.
4. **Release checkpoint:** Task 11. Confirm the combined report only passes when all required gates pass.