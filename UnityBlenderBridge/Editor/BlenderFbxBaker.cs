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
            ValidateDependencies(sourceAssetPath);
            ConfigureReferencedTextureColorSpaces(sourceAssetPath, outputFolder, importedModel);

            AssetDatabase.ImportAsset(
                sourceAssetPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            importedModel = AssetDatabase.LoadAssetAtPath<GameObject>(sourceAssetPath);
            if (importedModel == null)
                throw new InvalidDataException($"The FBX '{sourceAssetPath}' failed to reimport after texture configuration.");

            ValidateStaticModel(importedModel);
            ValidateDependencies(sourceAssetPath);
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

            var metadataBackup = MetadataBackup.Capture(
                AssetDatabase.LoadAssetAtPath<BlenderBakeMetadata>(metadataPath));
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
                    AssignMaterialsAndColliders(
                        bakeRoot,
                        materialMap,
                        fallbackMaterial,
                        settings?.ColliderPolicy ?? BridgeColliderPolicy.None);

                    PrefabUtility.SaveAsPrefabAsset(bakeRoot, prefabPath, out bool prefabSaved);
                    if (!prefabSaved || AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                        throw new IOException($"Unity did not save the baked Prefab at '{prefabPath}'.");
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
            }
            catch (Exception bakeException)
            {
                try
                {
                    metadataBackup.Restore(metadataPath);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The FBX bake failed and its metadata rollback also failed.",
                        bakeException,
                        rollbackException);
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

        private static void ConfigureReferencedTextureColorSpaces(
            string sourceAssetPath,
            string outputFolder,
            GameObject model)
        {
            var desiredSrgbByPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var desiredNormalMapByPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (Material material in GetSourceMaterials(model))
            {
                AddTextureRole(material, desiredSrgbByPath, desiredNormalMapByPath, outputFolder, true, false,
                    "_BaseMap", "_MainTex", "_ColorMap", "_EmissionMap");
                AddTextureRole(material, desiredSrgbByPath, desiredNormalMapByPath, outputFolder, false, true,
                    "_BumpMap", "_NormalMap");
                AddTextureRole(material, desiredSrgbByPath, desiredNormalMapByPath, outputFolder, false, false,
                    "_MetallicGlossMap", "_MetallicMap", "_RoughnessMap", "_OcclusionMap", "_MaskMap");
            }

            var reimportPaths = new List<string>();
            foreach (KeyValuePair<string, bool> entry in desiredSrgbByPath)
            {
                if (string.Equals(entry.Key, sourceAssetPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Texture '{entry.Key}' is embedded in the FBX; v1 requires staged external texture assets.");

                TextureImporter importer = AssetImporter.GetAtPath(entry.Key) as TextureImporter;
                if (importer == null)
                    throw new InvalidDataException(
                        $"Texture dependency '{entry.Key}' has no editable TextureImporter.");

                bool desiredIsNormalMap = desiredNormalMapByPath[entry.Key];
                TextureImporterType desiredTextureType = desiredIsNormalMap
                    ? TextureImporterType.NormalMap
                    : importer.textureType == TextureImporterType.NormalMap
                        ? TextureImporterType.Default
                        : importer.textureType;
                bool hasPersistedNormalMapRole =
                    BlenderFbxImportSettings.TryGetPersistedNormalMapRole(importer.userData, out bool persistedNormalMap);
                if (importer.sRGBTexture != entry.Value ||
                    !BlenderFbxImportSettings.TryGetPersistedTextureRole(importer.userData, out bool persistedRole) ||
                    persistedRole != entry.Value ||
                    importer.textureType != desiredTextureType ||
                    !hasPersistedNormalMapRole ||
                    persistedNormalMap != desiredIsNormalMap)
                {
                    importer.sRGBTexture = entry.Value;
                    importer.textureType = desiredTextureType;
                    importer.userData =
                        BlenderFbxImportSettings.SetPersistedTextureRole(importer.userData, entry.Value);
                    importer.userData =
                        BlenderFbxImportSettings.SetPersistedNormalMapRole(importer.userData, desiredIsNormalMap);
                    reimportPaths.Add(entry.Key);
                }
            }

            for (int i = 0; i < reimportPaths.Count; i++)
                AssetDatabase.ImportAsset(reimportPaths[i], ImportAssetOptions.ForceSynchronousImport);
        }

        private static void AddTextureRole(
            Material material,
            Dictionary<string, bool> desiredSrgbByPath,
            Dictionary<string, bool> desiredNormalMapByPath,
            string outputFolder,
            bool isSrgb,
            bool isNormalMap,
            params string[] properties)
        {
            if (material == null)
                return;

            for (int i = 0; i < properties.Length; i++)
            {
                string property = properties[i];
                if (!material.HasProperty(property))
                    continue;

                Texture texture = material.GetTexture(property);
                if (texture == null)
                    continue;

                string path = AssetDatabase.GetAssetPath(texture);
                if (string.IsNullOrEmpty(path))
                    continue;
                if (!BlenderFbxImportSettings.IsUnderOutputFolder(path, outputFolder))
                {
                    throw new InvalidDataException(
                        $"Texture dependency '{path}' is outside the configured Blender Bridge output folder '{outputFolder}'.");
                }

                if (desiredSrgbByPath.TryGetValue(path, out bool previous) && previous != isSrgb)
                {
                    throw new InvalidDataException(
                        $"Texture '{path}' is used as both colour and linear data. Use separate texture assets for those roles.");
                }
                if (desiredNormalMapByPath.TryGetValue(path, out bool previousNormalMap) &&
                    previousNormalMap != isNormalMap)
                {
                    throw new InvalidDataException(
                        $"Texture '{path}' is used as both a normal map and another data map. Use separate texture assets for those roles.");
                }

                desiredSrgbByPath[path] = isSrgb;
                desiredNormalMapByPath[path] = isNormalMap;
            }
        }

        private static void ValidateStaticModel(GameObject model)
        {
            if (model.GetComponentInChildren<SkinnedMeshRenderer>(true) != null ||
                model.GetComponentInChildren<Animator>(true) != null ||
                model.GetComponentInChildren<Animation>(true) != null ||
                model.GetComponentInChildren<LODGroup>(true) != null)
            {
                throw new InvalidDataException(
                    "This v1 FBX bake supports static meshes only; rigged, animated, and LOD content is unsupported.");
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
