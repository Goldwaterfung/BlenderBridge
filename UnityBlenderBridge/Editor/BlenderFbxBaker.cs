using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BlenderBridge;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BlenderBridge.Editor
{
    public static class BlenderFbxBaker
    {
        private const string UrpLitShaderName = "Universal Render Pipeline/Lit";
        private const string ExporterVersion = "Blender Bridge FBX v1";
        private const int MaximumAssetNameLength = 120;

        internal sealed class LodLevelPlan
        {
            public int Level;
            public string NodeName;
            public float Transition;
        }

        internal sealed class LodGroupPlan
        {
            public string GroupName;
            public LodLevelPlan[] Levels;
        }

        [MenuItem("Assets/Blender Bridge/Bake FBX To Prefab", false, 2100)]
        private static void BakeSelectedFbx()
        {
            string sourcePath = AssetDatabase.GetAssetPath(Selection.activeObject);
            try
            {
                Bake(sourcePath);
                EditorUtility.DisplayDialog(
                    "Blender Bridge",
                    "The FBX Prefab and its materials were baked successfully.",
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogError("[Blender Bridge] FBX bake failed: " + exception);
                EditorUtility.DisplayDialog(
                    "Blender Bridge FBX Bake Failed",
                    exception.Message,
                    "OK");
            }
        }

        [MenuItem("Assets/Blender Bridge/Bake FBX To Prefab", true)]
        private static bool CanBakeSelectedFbx()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) &&
                   string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase) &&
                   AssetDatabase.LoadAssetAtPath<GameObject>(path) != null;
        }

        public static string Bake(string sourceAssetPath)
        {
            if (!IsValidProjectAssetPath(sourceAssetPath, false) ||
                !string.Equals(Path.GetExtension(sourceAssetPath), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Select an imported FBX file inside the Unity project's Assets folder.", nameof(sourceAssetPath));
            }

            if (!BlenderFbxImportSettings.TryGetBridgeSettings(out BridgeSettings settings, out string settingsError))
                throw new InvalidOperationException(settingsError);

            string outputFolder = settings == null
                ? BridgeSettings.DefaultOutputFolder
                : settings.OutputFolder;
            if (!BlenderFbxImportSettings.IsUnderOutputFolder(sourceAssetPath, outputFolder))
            {
                throw new InvalidOperationException(
                    $"The FBX must be under the configured Blender Bridge output folder '{outputFolder}'.");
            }

            string absoluteSourcePath = GetAbsoluteAssetPath(sourceAssetPath);
            if (!File.Exists(absoluteSourcePath))
                throw new FileNotFoundException($"The FBX source file '{sourceAssetPath}' does not exist.", absoluteSourcePath);

            AssetDatabase.ImportAsset(
                sourceAssetPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            GameObject importedModel = AssetDatabase.LoadAssetAtPath<GameObject>(sourceAssetPath);
            if (importedModel == null)
                throw new InvalidDataException($"Unity did not import '{sourceAssetPath}' as a model.");

            ValidateStaticModel(importedModel);
            List<LodGroupPlan> lodPlans = PreflightLods(sourceAssetPath, importedModel);
            ValidateDependencies(sourceAssetPath);
            List<BlenderUv0Preflight.ValidationResult> uv0Results = BlenderUv0Preflight.ValidateModel(
                sourceAssetPath,
                importedModel,
                settings?.Uv0MaximumTextureDimension ?? BridgeSettings.DefaultUv0MaximumTextureDimension,
                settings != null && settings.AllowFloat32Uv0Override);
            foreach (BlenderUv0Preflight.ValidationResult result in uv0Results)
            {
                if (result.UsesFloat32Override)
                {
                    Debug.LogWarning(
                        $"[Blender Bridge] '{result.MeshContext}' UV0 requires the explicit Float32 override: " +
                        $"coordinate {result.LimitingCoordinateIndex} ({result.LimitingCoordinate.x:R}, " +
                        $"{result.LimitingCoordinate.y:R}), {result.MaximumTexelError:F6} texel at " +
                        $"{result.LimitingResolution.Width}x{result.LimitingResolution.Height} " +
                        $"({result.LimitingResolution.Description}).");
                }
            }
            List<string> sourceObjectPaths = GetMeshObjectPaths(importedModel);
            string sourceGuid = AssetDatabase.AssetPathToGUID(sourceAssetPath);
            if (string.IsNullOrEmpty(sourceGuid))
                throw new InvalidOperationException($"Unity has no asset GUID for '{sourceAssetPath}'.");

            string bakeFolder = ResolveBakeFolder(sourceAssetPath, outputFolder, sourceGuid);
            string materialsFolder = bakeFolder + "/Materials";
            EnsureAssetFolder(materialsFolder);

            string safeName = SanitizeAssetName(Path.GetFileNameWithoutExtension(sourceAssetPath));
            BlenderBakeMetadata existingMetadata = FindMetadata(sourceGuid);
            string prefabPath = existingMetadata != null &&
                                !string.IsNullOrWhiteSpace(existingMetadata.prefabAssetPath)
                ? existingMetadata.prefabAssetPath
                : bakeFolder + "/" + safeName + ".prefab";
            if (!BlenderFbxImportSettings.IsUnderOutputFolder(prefabPath, outputFolder))
                throw new InvalidOperationException("Existing Prefab metadata points outside the configured output folder.");
            GetAbsoluteAssetPath(prefabPath);
            PreflightOutputPath(prefabPath, typeof(GameObject));
            string metadataPath = ResolveMetadataPath(sourceGuid, bakeFolder, safeName, outputFolder);
            PreflightOutputPath(metadataPath, typeof(BlenderBakeMetadata));
            if (!string.Equals(
                    Path.GetDirectoryName(prefabPath)?.Replace('\\', '/'),
                    bakeFolder,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Existing Prefab metadata points outside this FBX's stable bake folder.");
            }

            Shader urpLit = Shader.Find(UrpLitShaderName);
            if (urpLit == null)
                throw new InvalidOperationException($"Required shader '{UrpLitShaderName}' is not available.");

            Dictionary<Material, Material> materialMap =
                CreateOrUpdateMaterials(
                    importedModel,
                    materialsFolder,
                    urpLit,
                    out Material fallbackMaterial,
                    out List<string> materialPaths);

            string absolutePrefabPath = GetAbsoluteAssetPath(prefabPath);
            bool prefabExistedBefore = File.Exists(absolutePrefabPath);
            byte[] prefabBackup = prefabExistedBefore
                ? File.ReadAllBytes(absolutePrefabPath)
                : null;
            var metadataBackup = MetadataBackup.Capture(
                AssetDatabase.LoadAssetAtPath<BlenderBakeMetadata>(metadataPath));
            var meshAssetTransaction = new MeshAssetTransaction();
            try
            {
                Scene previewScene = default(Scene);
                GameObject bakeRoot = null;
                try
                {
                    previewScene = EditorSceneManager.NewPreviewScene();
                    bakeRoot = PrefabUtility.InstantiatePrefab(importedModel, previewScene) as GameObject;
                    if (bakeRoot == null)
                        throw new InvalidOperationException("Could not stage the imported model for Prefab creation.");

                    bakeRoot.name = safeName;
                    string meshesFolder = bakeFolder + "/Meshes";
                    List<BlenderMeshOptimizer.MeshAssetRecord> optimizedMeshes =
                        BlenderMeshOptimizer.CreatePersistentMeshes(
                            bakeRoot,
                            sourceAssetPath,
                            meshesFolder,
                            settings?.Uv0MaximumTextureDimension ?? BridgeSettings.DefaultUv0MaximumTextureDimension,
                            settings != null && settings.AllowFloat32Uv0Override,
                            meshAssetTransaction);
                    foreach (BlenderMeshOptimizer.MeshAssetRecord optimizedMesh in optimizedMeshes)
                    {
                        Debug.Log(
                            $"[Blender Bridge] Optimized '{optimizedMesh.HierarchyPath}' to " +
                            $"{optimizedMesh.VertexFormat} UV0 (maximum reconstruction error " +
                            $"{optimizedMesh.MaximumTexelError:F6} texel at " +
                            $"{optimizedMesh.LimitingTextureWidth}x{optimizedMesh.LimitingTextureHeight}).");
                    }
                    AssignMaterialsAndColliders(
                        bakeRoot,
                        materialMap,
                        fallbackMaterial,
                        settings?.ColliderPolicy ?? BridgeColliderPolicy.None);
                    AssembleLodGroups(
                        bakeRoot,
                        lodPlans,
                        settings?.Lod0Transition ?? BridgeSettings.DefaultLod0Transition,
                        settings?.Lod1Transition ?? BridgeSettings.DefaultLod1Transition,
                        settings?.Lod2Transition ?? BridgeSettings.DefaultLod2Transition);

                    PrefabUtility.SaveAsPrefabAsset(bakeRoot, prefabPath, out bool prefabSaved);
                    if (!prefabSaved || AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                        throw new IOException($"Unity did not save the baked Prefab at '{prefabPath}'.");
                    AssetDatabase.ImportAsset(
                        prefabPath,
                        ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    ValidateSavedLodGroups(prefabPath, lodPlans);
                    BlenderMeshOptimizer.ValidateSavedMeshes(
                        prefabPath,
                        optimizedMeshes,
                        settings?.Uv0MaximumTextureDimension ?? BridgeSettings.DefaultUv0MaximumTextureDimension,
                        settings != null && settings.AllowFloat32Uv0Override);
                }
                finally
                {
                    try
                    {
                        if (bakeRoot != null)
                            UnityEngine.Object.DestroyImmediate(bakeRoot);
                    }
                    finally
                    {
                        if (previewScene.IsValid())
                            EditorSceneManager.ClosePreviewScene(previewScene);
                    }
                }

                WriteMetadata(
                    metadataPath,
                    sourceAssetPath,
                    sourceGuid,
                    AssetDatabase.GetAssetDependencyHash(sourceAssetPath).ToString(),
                    prefabPath,
                    sourceObjectPaths,
                    GetMeshObjectLocalIds(importedModel),
                    materialPaths);
                AssetDatabase.SaveAssets();
                meshAssetTransaction.Commit();
            }
            catch (Exception bakeException)
            {
                var rollbackExceptions = new List<Exception>();
                try
                {
                    meshAssetTransaction.Rollback();
                }
                catch (Exception rollbackException)
                {
                    rollbackExceptions.Add(rollbackException);
                }

                try
                {
                    metadataBackup.Restore(metadataPath);
                }
                catch (Exception rollbackException)
                {
                    rollbackExceptions.Add(rollbackException);
                }

                try
                {
                    if (prefabExistedBefore)
                    {
                        File.WriteAllBytes(absolutePrefabPath, prefabBackup);
                        AssetDatabase.ImportAsset(
                            prefabPath,
                            ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    }
                    else if (File.Exists(absolutePrefabPath) &&
                             !AssetDatabase.DeleteAsset(prefabPath) &&
                             File.Exists(absolutePrefabPath))
                    {
                        throw new IOException($"Could not remove failed Prefab output '{prefabPath}'.");
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackExceptions.Add(rollbackException);
                }

                if (rollbackExceptions.Count > 0)
                {
                    rollbackExceptions.Insert(0, bakeException);
                    throw new AggregateException(
                        "The FBX bake failed and one or more output rollbacks also failed.",
                        rollbackExceptions);
                }
                throw;
            }

            return prefabPath;
        }

        public static string SanitizeAssetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "BlenderAsset";

            char[] invalid = Path.GetInvalidFileNameChars();
            var characters = name.Trim().Select(character =>
                char.IsControl(character) || invalid.Contains(character) ||
                character == '/' || character == '\\' || character == ':' ||
                character == '*' || character == '?' || character == '"' ||
                character == '<' || character == '>' || character == '|'
                    ? '_'
                    : character).ToArray();
            string result = new string(characters).Trim().Trim('.', ' ');
            if (string.IsNullOrEmpty(result))
                result = "BlenderAsset";
            if (result.Length > MaximumAssetNameLength)
            {
                int length = MaximumAssetNameLength;
                if (char.IsHighSurrogate(result[length - 1]))
                    length--;
                result = result.Substring(0, length).TrimEnd('.', ' ');
            }
            if (string.IsNullOrEmpty(result))
                result = "BlenderAsset";
            if (IsWindowsReservedName(result))
                result += "_";
            return result;
        }

        private static bool IsWindowsReservedName(string name)
        {
            string stem = name.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
                return true;

            if (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] >= '1' && stem[3] <= '9')
                return true;

            return false;
        }

        private static void ValidateDependencies(string sourceAssetPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string[] dependencies = AssetDatabase.GetDependencies(sourceAssetPath, true);
            for (int i = 0; i < dependencies.Length; i++)
            {
                string dependency = dependencies[i];
                if (!dependency.StartsWith("Assets/", StringComparison.Ordinal))
                    continue;

                string absolutePath = Path.GetFullPath(Path.Combine(projectRoot, dependency));
                if (!IsWithinDirectory(absolutePath, Application.dataPath) ||
                    !File.Exists(absolutePath) ||
                    AssetDatabase.LoadMainAssetAtPath(dependency) == null)
                {
                    throw new InvalidDataException(
                        $"Required FBX dependency '{dependency}' is missing or failed to import.");
                }
            }
        }

        private static bool IsWithinDirectory(string path, string directory)
        {
            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.StartsWith(
                fullDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateStaticModel(GameObject model)
        {
            if (model.GetComponentInChildren<SkinnedMeshRenderer>(true) != null ||
                model.GetComponentInChildren<Animator>(true) != null ||
                model.GetComponentInChildren<Animation>(true) != null)
            {
                throw new InvalidDataException(
                    "This v1 FBX bake supports static meshes only; rigged or animated content is unsupported.");
            }

            MeshRenderer[] renderers = model.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 0)
                throw new InvalidDataException("The FBX contains no static mesh renderers to bake.");

            Renderer[] allRenderers = model.GetComponentsInChildren<Renderer>(true);
            if (allRenderers.Length != renderers.Length)
                throw new InvalidDataException("The FBX contains a non-static renderer that the v1 bake does not support.");

            foreach (MeshRenderer renderer in renderers)
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    throw new InvalidDataException(
                        $"Static renderer '{GetHierarchyPath(renderer.transform, model.transform)}' has no imported mesh.");
                }
            }
        }

        private static List<LodGroupPlan> PreflightLods(string sourceAssetPath, GameObject model)
        {
            string manifestPath = GetAbsoluteAssetPath(sourceAssetPath) + ".lod.json";
            if (!File.Exists(manifestPath))
            {
                if (ContainsLodMarkers(model))
                {
                    throw new InvalidDataException(
                        $"FBX '{sourceAssetPath}' contains LOD hierarchy nodes but has no required sidecar '{Path.GetFileName(manifestPath)}'.");
                }
                return new List<LodGroupPlan>();
            }

            BlenderLodManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<BlenderLodManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is IOException ||
                exception is FormatException)
            {
                throw new InvalidDataException(
                    $"Could not read LOD manifest '{manifestPath}': {exception.Message}",
                    exception);
            }

            return ValidateLodManifest(sourceAssetPath, model, manifest, manifestPath);
        }

        internal static List<LodGroupPlan> ValidateLodManifestForTests(
            string sourceAssetPath,
            GameObject model,
            BlenderLodManifest manifest)
        {
            return ValidateLodManifest(
                sourceAssetPath,
                model,
                manifest,
                sourceAssetPath + ".lod.json");
        }

        private static List<LodGroupPlan> ValidateLodManifest(
            string sourceAssetPath,
            GameObject model,
            BlenderLodManifest manifest,
            string manifestPath)
        {
            if (model.GetComponentInChildren<LODGroup>(true) != null)
            {
                throw new InvalidDataException(
                    $"FBX '{sourceAssetPath}' contains a native Unity LODGroup; Blender Bridge accepts only its named sidecar-backed hierarchy.");
            }

            if (manifest == null ||
                manifest.schemaVersion != 1 ||
                !string.Equals(manifest.fbxFile, Path.GetFileName(sourceAssetPath), StringComparison.Ordinal) ||
                !string.Equals(manifest.coordinateSpace, "source-root-local", StringComparison.Ordinal) ||
                manifest.lodSets == null ||
                manifest.lodSets.Length == 0)
            {
                throw new InvalidDataException(
                    $"LOD manifest '{manifestPath}' has an unsupported schema or incomplete FBX contract.");
            }

            var expectedGroups = new HashSet<string>(StringComparer.Ordinal);
            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            var plans = new List<LodGroupPlan>(manifest.lodSets.Length);
            Transform[] allTransforms = model.GetComponentsInChildren<Transform>(true);
            var discoveredGroups = allTransforms
                .Where(transform => transform.name.EndsWith("_LODGroup", StringComparison.Ordinal))
                .ToArray();
            if (discoveredGroups.Length != manifest.lodSets.Length)
            {
                throw new InvalidDataException(
                    $"FBX '{sourceAssetPath}' has {discoveredGroups.Length} LOD group nodes but its manifest declares {manifest.lodSets.Length}.");
            }

            foreach (BlenderLodSetManifest lodSet in manifest.lodSets)
            {
                if (lodSet == null ||
                    string.IsNullOrWhiteSpace(lodSet.sourceName) ||
                    !Guid.TryParse(lodSet.sourceId, out _) ||
                    string.IsNullOrWhiteSpace(lodSet.groupName) ||
                    lodSet.levels == null ||
                    lodSet.levels.Length == 0 ||
                    !sourceIds.Add(lodSet.sourceId) ||
                    !expectedGroups.Add(lodSet.groupName))
                {
                    throw new InvalidDataException(
                        $"LOD manifest '{manifestPath}' contains an empty, duplicate, or invalid LOD set.");
                }

                Transform[] matches = allTransforms
                    .Where(transform => string.Equals(transform.name, lodSet.groupName, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length != 1)
                {
                    throw new InvalidDataException(
                        $"FBX '{sourceAssetPath}' must contain exactly one group named '{lodSet.groupName}', found {matches.Length}.");
                }

                Transform group = matches[0];
                if (group.parent != model.transform)
                {
                    throw new InvalidDataException(
                        $"LOD group '{lodSet.groupName}' must be a direct child of FBX root '{model.name}'.");
                }
                var levelPlans = new List<LodLevelPlan>(lodSet.levels.Length);
                var expectedNodes = new HashSet<string>(StringComparer.Ordinal);
                var seenLevels = new HashSet<int>();
                int previousImportedTriangles = int.MaxValue;
                foreach (BlenderLodLevelManifest level in lodSet.levels
                    .OrderBy(item => item == null ? -1 : item.level))
                {
                    if (level == null ||
                        level.level < 0 ||
                        level.level > 3 ||
                        !seenLevels.Add(level.level) ||
                        !string.Equals(
                            level.nodeName,
                            lodSet.groupName + "_LOD" + level.level,
                            StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(level.meshName) ||
                        string.IsNullOrWhiteSpace(level.assignedObjectName) ||
                        level.triangleCap <= 0 ||
                        level.triangleCount <= 0 ||
                        level.vertexCount <= 0)
                    {
                        throw new InvalidDataException(
                            $"LOD set '{lodSet.groupName}' contains malformed or duplicate level metadata.");
                    }

                    expectedNodes.Add(level.nodeName);
                    Transform node = FindUniqueDirectChild(group, level.nodeName);
                    MeshRenderer[] renderers = node.GetComponentsInChildren<MeshRenderer>(true);
                    if (renderers.Length != 1 ||
                        !string.Equals(renderers[0].gameObject.name, level.meshName, StringComparison.Ordinal))
                    {
                        string foundRenderer = renderers.Length == 1
                            ? "'" + renderers[0].gameObject.name + "'"
                            : renderers.Length.ToString() + " renderers";
                        throw new InvalidDataException(
                            $"LOD node '{lodSet.groupName}/{level.nodeName}' for assigned mesh '{level.assignedObjectName}' must contain exactly one renderer named '{level.meshName}'; found {foundRenderer}.");
                    }

                    MeshFilter filter = renderers[0].GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null)
                    {
                        throw new InvalidDataException(
                            $"LOD node '{lodSet.groupName}/{level.nodeName}' for assigned mesh '{level.assignedObjectName}' has no imported mesh.");
                    }

                    int importedTriangles = CountImportedTriangles(
                        filter.sharedMesh,
                        sourceAssetPath,
                        lodSet.groupName,
                        level.level);
                    if (importedTriangles <= 0 || importedTriangles > level.triangleCap)
                    {
                        throw new InvalidDataException(
                            $"FBX '{sourceAssetPath}', LOD set '{lodSet.groupName}', assigned mesh '{level.assignedObjectName}' ('{level.meshName}'), LOD{level.level} imported {importedTriangles} triangles; cap is {level.triangleCap}.");
                    }
                    if (importedTriangles >= previousImportedTriangles)
                    {
                        throw new InvalidDataException(
                            $"FBX '{sourceAssetPath}', LOD set '{lodSet.groupName}', assigned mesh '{level.assignedObjectName}', LOD{level.level} imported {importedTriangles} triangles, which is not lower than the previous emitted level's {previousImportedTriangles}.");
                    }
                    previousImportedTriangles = importedTriangles;

                    levelPlans.Add(new LodLevelPlan
                    {
                        Level = level.level,
                        NodeName = level.nodeName
                    });
                }

                Transform[] actualLodNodes = group.Cast<Transform>().ToArray();
                if (actualLodNodes.Length != expectedNodes.Count ||
                    actualLodNodes.Any(child => !expectedNodes.Contains(child.name)) ||
                    group.GetComponentsInChildren<MeshRenderer>(true).Length != levelPlans.Count)
                {
                    throw new InvalidDataException(
                        $"LOD set '{lodSet.groupName}' contains missing, duplicate, or undeclared LOD nodes/renderers.");
                }

                levelPlans.Sort((left, right) => left.Level.CompareTo(right.Level));
                plans.Add(new LodGroupPlan
                {
                    GroupName = lodSet.groupName,
                    Levels = levelPlans.ToArray()
                });
            }

            if (discoveredGroups.Any(group => !expectedGroups.Contains(group.name)))
            {
                throw new InvalidDataException(
                    $"FBX '{sourceAssetPath}' contains an LOD group not declared in its manifest.");
            }
            return plans;
        }

        private static bool ContainsLodMarkers(GameObject model)
        {
            return model.GetComponentInChildren<LODGroup>(true) != null ||
                   model.GetComponentsInChildren<Transform>(true).Any(transform =>
                transform.name.EndsWith("_LODGroup", StringComparison.Ordinal) ||
                transform.name.StartsWith("LOD", StringComparison.Ordinal));
        }

        private static Transform FindUniqueDirectChild(Transform parent, string childName)
        {
            Transform match = null;
            foreach (Transform child in parent)
            {
                if (!string.Equals(child.name, childName, StringComparison.Ordinal))
                    continue;
                if (match != null)
                    throw new InvalidDataException(
                        $"LOD group '{parent.name}' has duplicate child nodes named '{childName}'.");
                match = child;
            }

            if (match == null)
                throw new InvalidDataException(
                    $"LOD group '{parent.name}' is missing child node '{childName}'.");
            return match;
        }

        private static int CountImportedTriangles(
            Mesh mesh,
            string sourceAssetPath,
            string lodSetName,
            int level)
        {
            long triangleCount = 0;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                {
                    throw new InvalidDataException(
                        $"FBX '{sourceAssetPath}', LOD set '{lodSetName}', LOD{level} contains non-triangle topology.");
                }
                triangleCount += mesh.GetIndexCount(submesh) / 3;
            }

            if (triangleCount > int.MaxValue)
                throw new InvalidDataException(
                    $"FBX '{sourceAssetPath}', LOD set '{lodSetName}', LOD{level} exceeds supported triangle-count range.");
            return (int)triangleCount;
        }

        internal static void AssembleLodGroups(
            GameObject root,
            List<LodGroupPlan> plans,
            float lod0Transition,
            float lod1Transition,
            float lod2Transition)
        {
            float[] transitions = { lod0Transition, lod1Transition, lod2Transition };
            foreach (LodGroupPlan plan in plans)
            {
                if (plan.Levels.Length == 1)
                    continue;
                Transform[] groupMatches = root.GetComponentsInChildren<Transform>(true)
                    .Where(transform => string.Equals(transform.name, plan.GroupName, StringComparison.Ordinal))
                    .ToArray();
                if (groupMatches.Length != 1)
                    throw new InvalidDataException(
                        $"Baked model must contain exactly one LOD group root named '{plan.GroupName}'.");

                LODGroup group = groupMatches[0].GetComponent<LODGroup>();
                if (group == null)
                    group = groupMatches[0].gameObject.AddComponent<LODGroup>();
                var lods = new LOD[plan.Levels.Length];
                for (int index = 0; index < plan.Levels.Length; index++)
                {
                    LodLevelPlan level = plan.Levels[index];
                    Transform node = FindUniqueDirectChild(group.transform, level.NodeName);
                    Renderer[] renderers = node.GetComponentsInChildren<MeshRenderer>(true);
                    if (renderers.Length != 1)
                        throw new InvalidDataException(
                            $"Baked LOD node '{plan.GroupName}/LOD{level.Level}' no longer has exactly one renderer.");
                    float threshold = level.Level < transitions.Length ? transitions[level.Level] : 0f;
                    level.Transition = threshold;
                    lods[index] = new LOD(threshold, renderers);
                }

                group.SetLODs(lods);
                group.RecalculateBounds();
                if (float.IsNaN(group.size) || float.IsInfinity(group.size) || group.size <= 0f)
                    throw new InvalidDataException(
                        $"LOD group '{plan.GroupName}' has invalid or empty renderer bounds.");
            }
        }

        private static void ValidateSavedLodGroups(string prefabPath, List<LodGroupPlan> plans)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                throw new InvalidDataException($"Could not reload baked Prefab '{prefabPath}'.");

            LODGroup[] groups = prefab.GetComponentsInChildren<LODGroup>(true);
            int expectedGroupCount = plans.Count(plan => plan.Levels.Length > 1);
            if (groups.Length != expectedGroupCount)
                throw new InvalidDataException(
                    $"Saved Prefab '{prefabPath}' contains {groups.Length} LODGroups; expected {expectedGroupCount}.");

            foreach (LodGroupPlan plan in plans)
            {
                if (plan.Levels.Length == 1)
                    continue;
                LODGroup[] matches = groups.Where(group =>
                    string.Equals(group.gameObject.name, plan.GroupName, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1 || matches[0].GetLODs().Length != plan.Levels.Length)
                    throw new InvalidDataException(
                        $"Saved Prefab '{prefabPath}' did not preserve LOD group '{plan.GroupName}' and its {plan.Levels.Length} levels.");
                LOD[] lods = matches[0].GetLODs();
                for (int index = 0; index < lods.Length; index++)
                {
                    if (lods[index].renderers == null ||
                        lods[index].renderers.Length != 1 ||
                        !Mathf.Approximately(
                            lods[index].screenRelativeTransitionHeight,
                            plan.Levels[index].Transition))
                        throw new InvalidDataException(
                            $"Saved Prefab '{prefabPath}' has an invalid transition or renderer set for '{plan.GroupName}' LOD{plan.Levels[index].Level}.");
                }
                if (float.IsNaN(matches[0].size) ||
                    float.IsInfinity(matches[0].size) ||
                    matches[0].size <= 0f)
                    throw new InvalidDataException(
                        $"Saved Prefab '{prefabPath}' has invalid bounds for LOD group '{plan.GroupName}'.");
            }
        }

        private static List<string> GetMeshObjectPaths(GameObject model)
        {
            return model.GetComponentsInChildren<MeshRenderer>(true)
                .Select(renderer => GetHierarchyPath(renderer.transform, model.transform))
                .ToList();
        }

        private static List<string> GetMeshObjectLocalIds(GameObject model)
        {
            var localIds = new List<string>();
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        renderer.gameObject,
                        out string guid,
                        out long localId))
                {
                    localIds.Add(guid + ":" + localId);
                }
                else
                {
                    localIds.Add(string.Empty);
                }
            }

            return localIds;
        }

        private static string GetHierarchyPath(Transform transform, Transform root)
        {
            var segments = new Stack<string>();
            Transform current = transform;
            while (current != null && current != root)
            {
                segments.Push(current.name);
                current = current.parent;
            }

            segments.Push(root.name);
            return string.Join("/", segments);
        }

        private static List<Material> GetSourceMaterials(GameObject model)
        {
            var materials = new List<Material>();
            var seen = new HashSet<Material>();
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] slots = renderer.sharedMaterials;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] != null && seen.Add(slots[i]))
                        materials.Add(slots[i]);
                }
            }

            return materials;
        }

        private static Dictionary<Material, Material> CreateOrUpdateMaterials(
            GameObject model,
            string materialsFolder,
            Shader urpLit,
            out Material fallbackMaterial,
            out List<string> materialPaths)
        {
            var mapping = new Dictionary<Material, Material>();
            fallbackMaterial = null;
            materialPaths = new List<string>();
            var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<Material> sources = GetSourceMaterials(model);
            for (int i = 0; i < sources.Count; i++)
                ValidateSupportedMaterialFeatures(sources[i]);

            bool needsFallback = sources.Count == 0;
            foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] slots = renderer.sharedMaterials;
                if (slots.Length == 0 || Array.Exists(slots, material => material == null))
                {
                    needsFallback = true;
                    break;
                }
            }

            var plans = new List<MaterialPlan>();
            for (int i = 0; i < sources.Count; i++)
            {
                Material source = sources[i];
                string safeName = SanitizeAssetName(source.name);
                usedNames.TryGetValue(safeName, out int occurrence);
                usedNames[safeName] = occurrence + 1;
                if (occurrence > 0)
                    safeName += "_" + (occurrence + 1).ToString("D2");

                string path = materialsFolder + "/" + safeName + ".mat";
                PreflightOutputPath(path, typeof(Material));
                plans.Add(new MaterialPlan(source, path, source.name));
                materialPaths.Add(path);
            }

            string fallbackPath = null;
            string fallbackName = null;
            if (needsFallback)
            {
                fallbackName = "Blender Bridge Default";
                int fallbackIndex = 1;
                while (usedNames.ContainsKey(fallbackName))
                    fallbackName = "Blender Bridge Default " + (++fallbackIndex);
                fallbackPath = materialsFolder + "/" + fallbackName + ".mat";
                PreflightOutputPath(fallbackPath, typeof(Material));
                plans.Add(new MaterialPlan(null, fallbackPath, fallbackName));
                materialPaths.Add(fallbackPath);
            }

            for (int i = 0; i < plans.Count; i++)
            {
                MaterialPlan plan = plans[i];
                Material persistent = CreateOrUpdateMaterial(plan.Source, plan.Path, urpLit, plan.Name);
                if (plan.Source == null)
                    fallbackMaterial = persistent;
                else
                    mapping.Add(plan.Source, persistent);
            }

            return mapping;
        }

        private static Material CreateOrUpdateMaterial(
            Material source,
            string path,
            Shader urpLit,
            string overrideName = null)
        {
            string materialName = overrideName ??
                                  (source == null ? "Blender Bridge Default" : source.name);
            Material staged = new Material(urpLit) { name = materialName };
            try
            {
                if (source != null)
                    CopyMaterialProperties(source, staged);
                Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (existing == null)
                {
                    PreflightOutputPath(path, typeof(Material));
                    AssetDatabase.CreateAsset(staged, path);
                    staged = null;
                    return AssetDatabase.LoadAssetAtPath<Material>(path);
                }

                EditorUtility.CopySerialized(staged, existing);
                existing.name = materialName;
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssetIfDirty(existing);
                return existing;
            }
            finally
            {
                if (staged != null)
                    UnityEngine.Object.DestroyImmediate(staged);
            }
        }

        private static void CopyMaterialProperties(Material source, Material destination)
        {
            ValidateSupportedMaterialFeatures(source);

            SetColorFromAny(source, destination, new[] { "_BaseColor", "_Color" }, "_BaseColor");
            SetFloatFromAny(source, destination, new[] { "_Metallic" }, "_Metallic");
            SetFloatFromAny(source, destination, new[] { "_Smoothness", "_Glossiness" }, "_Smoothness");
            if (source.HasProperty("_Roughness") && destination.HasProperty("_Smoothness"))
                destination.SetFloat("_Smoothness", 1f - source.GetFloat("_Roughness"));

            CopyTexture(source, destination, new[] { "_BaseMap", "_MainTex", "_ColorMap" }, "_BaseMap");
            CopyTexture(source, destination, new[] { "_BumpMap", "_NormalMap" }, "_BumpMap");
            CopyTexture(source, destination, new[] { "_MetallicGlossMap", "_MetallicMap" }, "_MetallicGlossMap");
            CopyTexture(source, destination, new[] { "_OcclusionMap" }, "_OcclusionMap");
            CopyTexture(source, destination, new[] { "_EmissionMap" }, "_EmissionMap");
            SetFloatFromAny(source, destination, new[] { "_BumpScale" }, "_BumpScale");
            SetFloatFromAny(source, destination, new[] { "_OcclusionStrength" }, "_OcclusionStrength");

            if (source.HasProperty("_EmissionColor") && destination.HasProperty("_EmissionColor"))
            {
                destination.SetColor("_EmissionColor", source.GetColor("_EmissionColor"));
                destination.EnableKeyword("_EMISSION");
            }
        }

        private static void ValidateSupportedMaterialFeatures(Material source)
        {
            if ((source.HasProperty("_RoughnessMap") && source.GetTexture("_RoughnessMap") != null) ||
                (source.HasProperty("_SpecGlossMap") && source.GetTexture("_SpecGlossMap") != null))
            {
                throw new InvalidDataException(
                    $"Material '{source.name}' uses a separate roughness or specular-gloss texture that the v1 FBX-to-URP mapping cannot represent.");
            }
        }

        private static void SetColorFromAny(
            Material source,
            Material destination,
            string[] sourceProperties,
            string destinationProperty)
        {
            if (!destination.HasProperty(destinationProperty))
                return;

            for (int i = 0; i < sourceProperties.Length; i++)
            {
                if (source.HasProperty(sourceProperties[i]))
                {
                    destination.SetColor(destinationProperty, source.GetColor(sourceProperties[i]));
                    return;
                }
            }
        }

        private static void SetFloatFromAny(
            Material source,
            Material destination,
            string[] sourceProperties,
            string destinationProperty)
        {
            if (!destination.HasProperty(destinationProperty))
                return;

            for (int i = 0; i < sourceProperties.Length; i++)
            {
                if (source.HasProperty(sourceProperties[i]))
                {
                    destination.SetFloat(destinationProperty, source.GetFloat(sourceProperties[i]));
                    return;
                }
            }
        }

        private static void CopyTexture(
            Material source,
            Material destination,
            string[] sourceProperties,
            string destinationProperty)
        {
            if (!destination.HasProperty(destinationProperty))
                return;

            for (int i = 0; i < sourceProperties.Length; i++)
            {
                if (!source.HasProperty(sourceProperties[i]))
                    continue;

                Texture texture = source.GetTexture(sourceProperties[i]);
                if (texture == null)
                    continue;

                string sourceProperty = sourceProperties[i];
                destination.SetTexture(destinationProperty, texture);
                destination.SetTextureScale(destinationProperty, source.GetTextureScale(sourceProperty));
                destination.SetTextureOffset(destinationProperty, source.GetTextureOffset(sourceProperty));
                if (destinationProperty == "_BumpMap")
                    destination.EnableKeyword("_NORMALMAP");
                else if (destinationProperty == "_MetallicGlossMap")
                    destination.EnableKeyword("_METALLICSPECGLOSSMAP");
                else if (destinationProperty == "_OcclusionMap")
                    destination.EnableKeyword("_OCCLUSIONMAP");
                return;
            }
        }

        private static void AssignMaterialsAndColliders(
            GameObject root,
            Dictionary<Material, Material> materialMap,
            Material fallbackMaterial,
            BridgeColliderPolicy colliderPolicy)
        {
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] sourceSlots = renderer.sharedMaterials;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                int slotCount = Math.Max(sourceSlots.Length, filter.sharedMesh.subMeshCount);
                var persistentSlots = new Material[slotCount];
                for (int i = 0; i < sourceSlots.Length; i++)
                {
                    Material source = sourceSlots[i];
                    if (source == null)
                    {
                        persistentSlots[i] = fallbackMaterial;
                    }
                    else if (!materialMap.TryGetValue(source, out persistentSlots[i]))
                    {
                        throw new InvalidDataException(
                            $"Could not resolve material slot {i} on '{GetHierarchyPath(renderer.transform, root.transform)}'.");
                    }
                }
                for (int i = sourceSlots.Length; i < persistentSlots.Length; i++)
                    persistentSlots[i] = fallbackMaterial;

                renderer.sharedMaterials = persistentSlots;
                if (colliderPolicy == BridgeColliderPolicy.MeshCollider &&
                    renderer.GetComponent<MeshCollider>() == null)
                {
                    var collider = renderer.gameObject.AddComponent<MeshCollider>();
                    collider.sharedMesh = filter.sharedMesh;
                }
            }
        }

        private static string ResolveBakeFolder(string sourcePath, string outputFolder, string sourceGuid)
        {
            BlenderBakeMetadata existingMetadata = FindMetadata(sourceGuid);
            if (existingMetadata != null)
            {
                string existingPath = AssetDatabase.GetAssetPath(existingMetadata);
                string existingFolder = Path.GetDirectoryName(existingPath)?.Replace('\\', '/');
                if (!BlenderFbxImportSettings.IsUnderOutputFolder(existingPath, outputFolder))
                {
                    throw new InvalidOperationException(
                        "The existing bake metadata is outside the configured output folder. Move or remove the old bake before changing its output location.");
                }

                return existingFolder;
            }

            string safeName = SanitizeAssetName(Path.GetFileNameWithoutExtension(sourcePath));
            string uniqueName = safeName + "_" + sourceGuid.Substring(0, Math.Min(8, sourceGuid.Length));
            return outputFolder.TrimEnd('/') + "/Baked/" + uniqueName;
        }

        private static string ResolveMetadataPath(
            string sourceGuid,
            string bakeFolder,
            string safeName,
            string outputFolder)
        {
            BlenderBakeMetadata existingMetadata = FindMetadata(sourceGuid);
            if (existingMetadata == null)
                return bakeFolder + "/" + safeName + ".metadata.asset";

            string existingPath = AssetDatabase.GetAssetPath(existingMetadata);
            if (!BlenderFbxImportSettings.IsUnderOutputFolder(existingPath, outputFolder))
                throw new InvalidOperationException("Existing bake metadata is outside the configured output folder.");
            return existingPath;
        }

        private static BlenderBakeMetadata FindMetadata(string sourceGuid)
        {
            string[] guids = AssetDatabase.FindAssets("t:BlenderBakeMetadata");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                BlenderBakeMetadata metadata = AssetDatabase.LoadAssetAtPath<BlenderBakeMetadata>(path);
                if (metadata != null &&
                    string.Equals(metadata.sourceAssetGuid, sourceGuid, StringComparison.OrdinalIgnoreCase))
                    return metadata;
            }

            return null;
        }

        private static void WriteMetadata(
            string metadataPath,
            string sourcePath,
            string sourceGuid,
            string dependencyHash,
            string prefabPath,
            List<string> sourceObjectPaths,
            List<string> sourceObjectLocalIds,
            List<string> materialPaths)
        {
            BlenderBakeMetadata metadata = AssetDatabase.LoadAssetAtPath<BlenderBakeMetadata>(metadataPath);
            bool isNew = metadata == null;
            if (isNew)
            {
                PreflightOutputPath(metadataPath, typeof(BlenderBakeMetadata));
                metadata = ScriptableObject.CreateInstance<BlenderBakeMetadata>();
                try
                {
                    SetMetadata(metadata, sourcePath, sourceGuid, dependencyHash, prefabPath,
                        sourceObjectPaths, sourceObjectLocalIds, materialPaths);
                    AssetDatabase.CreateAsset(metadata, metadataPath);
                    metadata = null;
                }
                finally
                {
                    if (metadata != null)
                        UnityEngine.Object.DestroyImmediate(metadata);
                }

                return;
            }

            SetMetadata(metadata, sourcePath, sourceGuid, dependencyHash, prefabPath,
                sourceObjectPaths, sourceObjectLocalIds, materialPaths);
            EditorUtility.SetDirty(metadata);
            AssetDatabase.SaveAssetIfDirty(metadata);
        }

        private static void SetMetadata(
            BlenderBakeMetadata metadata,
            string sourcePath,
            string sourceGuid,
            string dependencyHash,
            string prefabPath,
            List<string> sourceObjectPaths,
            List<string> sourceObjectLocalIds,
            List<string> materialPaths)
        {
            metadata.sourceAssetPath = sourcePath;
            metadata.sourceAssetGuid = sourceGuid;
            metadata.sourceDependencyHash = dependencyHash;
            metadata.exporterVersion = ExporterVersion;
            metadata.prefabAssetPath = prefabPath;
            metadata.lastBakedUtc = DateTime.UtcNow.ToString("O");
            metadata.sourceObjectPaths = sourceObjectPaths.ToArray();
            metadata.sourceObjectLocalIds = sourceObjectLocalIds.ToArray();
            metadata.materialAssetPaths = materialPaths.ToArray();
        }

        private static void EnsureAssetFolder(string assetFolder)
        {
            if (!IsValidProjectAssetPath(assetFolder, true))
                throw new ArgumentException("Bake folders must be inside the project Assets folder.", nameof(assetFolder));

            string normalized = assetFolder;
            GetAbsoluteAssetPath(normalized);
            string current = "Assets";
            if (!AssetDatabase.IsValidFolder(current))
                throw new IOException("Unity's project Assets folder is unavailable.");

            string[] segments = normalized.Split('/');
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (File.Exists(GetAbsoluteAssetPath(next)))
                        throw new IOException($"Bake folder path '{next}' is occupied by a file.");
                    string guid = AssetDatabase.CreateFolder(current, segments[i]);
                    if (string.IsNullOrEmpty(guid))
                        throw new IOException($"Could not create bake folder '{next}'.");
                }

                if (!AssetDatabase.IsValidFolder(next) || !Directory.Exists(GetAbsoluteAssetPath(next)))
                    throw new IOException($"Bake folder '{next}' is unavailable after creation.");
                current = next;
            }
        }

        private static void PreflightOutputPath(string assetPath, Type expectedType)
        {
            string absolutePath = GetAbsoluteAssetPath(assetPath);
            if (AssetDatabase.IsValidFolder(assetPath))
                throw new IOException($"Output path '{assetPath}' is occupied by a folder.");

            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (existing != null && !expectedType.IsInstanceOfType(existing))
            {
                throw new IOException(
                    $"Output path '{assetPath}' is occupied by {existing.GetType().Name}, not {expectedType.Name}.");
            }

            if (existing == null && File.Exists(absolutePath))
            {
                throw new IOException($"Output file '{assetPath}' already exists but is not a compatible imported asset.");
            }
        }

        private static string GetAbsoluteAssetPath(string assetPath)
        {
            if (assetPath == "Assets")
                return Path.GetFullPath(Application.dataPath);
            if (!IsValidProjectAssetPath(assetPath, false))
                throw new ArgumentException("Asset path must be below Assets.", nameof(assetPath));

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string absolutePath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            if (!IsWithinDirectory(absolutePath, Application.dataPath))
                throw new ArgumentException("Asset path resolves outside the project Assets folder.", nameof(assetPath));
            return absolutePath;
        }

        private static bool IsValidProjectAssetPath(string assetPath, bool allowAssetsRoot)
        {
            if (string.Equals(assetPath, "Assets", StringComparison.Ordinal))
                return allowAssetsRoot;
            if (string.IsNullOrWhiteSpace(assetPath) ||
                !assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                assetPath.IndexOf('\\') >= 0)
            {
                return false;
            }

            string[] segments = assetPath.Split('/');
            for (int i = 1; i < segments.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(segments[i]) ||
                    segments[i] == "." ||
                    segments[i] == "..")
                {
                    return false;
                }

                if (segments[i].EndsWith(".", StringComparison.Ordinal) ||
                    segments[i].EndsWith(" ", StringComparison.Ordinal) ||
                    IsWindowsReservedName(segments[i]))
                {
                    return false;
                }

                for (int j = 0; j < segments[i].Length; j++)
                {
                    char character = segments[i][j];
                    if (char.IsControl(character) ||
                        character == '<' || character == '>' || character == ':' ||
                        character == '"' || character == '|' || character == '?' ||
                        character == '*')
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private sealed class MaterialPlan
        {
            public MaterialPlan(Material source, string path, string name)
            {
                Source = source;
                Path = path;
                Name = name;
            }

            public Material Source { get; }
            public string Path { get; }
            public string Name { get; }
        }

        private sealed class MetadataBackup
        {
            private readonly bool existed;
            private readonly string sourceAssetPath;
            private readonly string sourceAssetGuid;
            private readonly string sourceDependencyHash;
            private readonly string exporterVersion;
            private readonly string prefabAssetPath;
            private readonly string lastBakedUtc;
            private readonly string[] sourceObjectPaths;
            private readonly string[] sourceObjectLocalIds;
            private readonly string[] materialAssetPaths;

            private MetadataBackup(BlenderBakeMetadata metadata)
            {
                existed = metadata != null;
                if (!existed)
                    return;

                sourceAssetPath = metadata.sourceAssetPath;
                sourceAssetGuid = metadata.sourceAssetGuid;
                sourceDependencyHash = metadata.sourceDependencyHash;
                exporterVersion = metadata.exporterVersion;
                prefabAssetPath = metadata.prefabAssetPath;
                lastBakedUtc = metadata.lastBakedUtc;
                sourceObjectPaths = metadata.sourceObjectPaths;
                sourceObjectLocalIds = metadata.sourceObjectLocalIds;
                materialAssetPaths = metadata.materialAssetPaths;
            }

            public static MetadataBackup Capture(BlenderBakeMetadata metadata)
            {
                return new MetadataBackup(metadata);
            }

            public void Restore(string metadataPath)
            {
                if (!existed)
                {
                    if (AssetDatabase.LoadAssetAtPath<BlenderBakeMetadata>(metadataPath) != null)
                        AssetDatabase.DeleteAsset(metadataPath);
                    return;
                }

                BlenderBakeMetadata metadata =
                    AssetDatabase.LoadAssetAtPath<BlenderBakeMetadata>(metadataPath);
                if (metadata == null)
                    throw new IOException($"Could not restore bake metadata at '{metadataPath}'.");

                metadata.sourceAssetPath = sourceAssetPath;
                metadata.sourceAssetGuid = sourceAssetGuid;
                metadata.sourceDependencyHash = sourceDependencyHash;
                metadata.exporterVersion = exporterVersion;
                metadata.prefabAssetPath = prefabAssetPath;
                metadata.lastBakedUtc = lastBakedUtc;
                metadata.sourceObjectPaths = sourceObjectPaths;
                metadata.sourceObjectLocalIds = sourceObjectLocalIds;
                metadata.materialAssetPaths = materialAssetPaths;
                EditorUtility.SetDirty(metadata);
                AssetDatabase.SaveAssetIfDirty(metadata);
            }
        }
    }
}
