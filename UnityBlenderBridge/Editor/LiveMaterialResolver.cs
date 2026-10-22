using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BlenderBridge;
using UnityEditor;
using UnityEngine;

namespace BlenderBridge.Editor
{
    internal static class LiveMaterialResolver
    {
        private static readonly Dictionary<MaterialKey, MaterialDefinition> Definitions =
            new Dictionary<MaterialKey, MaterialDefinition>();
        private static readonly Dictionary<MaterialKey, ResolvedMaterial> Resolved =
            new Dictionary<MaterialKey, ResolvedMaterial>();
        private static readonly Dictionary<MaterialKey, string> MissingWarnings =
            new Dictionary<MaterialKey, string>();
        private const string LiveTextureCacheFolder = "Assets/BlenderBridgeLiveSyncCache";
        private static Shader previewShader;

        private static readonly string[] BaseColorProperties = { "_BaseMap", "_MainTex" };
        private static readonly string[] NormalProperties = { "_BumpMap", "_NormalMap" };
        private static readonly string[] EmissionProperties = { "_EmissionMap" };

        public static void UpdateFromHello(HelloPayload hello)
        {
            if (hello == null)
                throw new ArgumentNullException(nameof(hello));

            var incoming = new Dictionary<MaterialKey, MaterialMetadata>();
            try
            {
                HelloMetadata metadata = JsonUtility.FromJson<HelloMetadata>(hello.MetadataJson);
                if (metadata?.materials != null)
                {
                    foreach (MaterialMetadata item in metadata.materials)
                    {
                        if (item == null ||
                            !Guid.TryParse(item.object_uuid, out Guid objectId) ||
                            objectId == Guid.Empty ||
                            item.index < 0)
                            continue;

                        var key = new MaterialKey(objectId, (uint)item.index);
                        if (incoming.ContainsKey(key))
                        {
                            BridgeServer.ReportApplicationError(
                                $"Hello metadata contains duplicate material slot {item.index} for object {objectId}.");
                            continue;
                        }

                        incoming.Add(key, item);
                    }
                }
            }
            catch (ArgumentException exception)
            {
                BridgeServer.ReportApplicationError(
                    $"Could not read Blender material preview metadata: {exception.Message}");
            }

            HashSet<Guid> liveObjects = null;
            if (hello.Objects != null)
            {
                liveObjects = new HashSet<Guid>();
                foreach (HelloObject liveObject in hello.Objects)
                {
                    if (liveObject != null && liveObject.ObjectId != Guid.Empty)
                        liveObjects.Add(liveObject.ObjectId);
                }
            }

            var keysToRemove = new List<MaterialKey>();
            foreach (MaterialKey key in Definitions.Keys)
            {
                if (!incoming.ContainsKey(key) ||
                    (liveObjects != null && !liveObjects.Contains(key.ObjectId)))
                    keysToRemove.Add(key);
            }

            foreach (MaterialKey key in keysToRemove)
            {
                Definitions.Remove(key);
                DestroyResolvedMaterial(key);
                MissingWarnings.Remove(key);
            }

            if (liveObjects != null)
            {
                keysToRemove.Clear();
                foreach (MaterialKey key in Resolved.Keys)
                {
                    if (!liveObjects.Contains(key.ObjectId))
                        keysToRemove.Add(key);
                }

                foreach (MaterialKey key in keysToRemove)
                {
                    DestroyResolvedMaterial(key);
                    MissingWarnings.Remove(key);
                }
            }

            foreach (KeyValuePair<MaterialKey, MaterialMetadata> entry in incoming)
            {
                TextureMetadataSet textures = entry.Value.textures;
                if (!HasAnyTexture(textures) &&
                    Definitions.TryGetValue(entry.Key, out MaterialDefinition previous))
                    textures = previous.Textures;

                var updated = new MaterialDefinition(entry.Value, textures);
                if (Definitions.TryGetValue(entry.Key, out MaterialDefinition existing))
                {
                    if (existing.IsEquivalentTo(updated))
                        continue;

                    Definitions[entry.Key] = updated;
                    RefreshResolvedMaterial(entry.Key);
                }
                else
                {
                    Definitions.Add(entry.Key, updated);
                    RefreshResolvedMaterial(entry.Key);
                }
            }

            MissingWarnings.Clear();
        }

        public static void UpdateMaterials(MaterialsPayload payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            try
            {
                MaterialsMetadata metadata =
                    JsonUtility.FromJson<MaterialsMetadata>(payload.MetadataJson);
                var updated = new Dictionary<MaterialKey, MaterialDefinition>();
                if (metadata?.materials != null)
                {
                    foreach (MaterialMetadata item in metadata.materials)
                    {
                        if (item == null ||
                            !Guid.TryParse(item.object_uuid, out Guid objectId) ||
                            objectId != payload.ObjectId ||
                            item.index < 0)
                            continue;

                        var key = new MaterialKey(objectId, (uint)item.index);
                        if (updated.ContainsKey(key))
                        {
                            BridgeServer.ReportApplicationError(
                                $"Material packet contains duplicate slot {item.index} for object {objectId}.");
                            continue;
                        }

                        updated.Add(key, new MaterialDefinition(item));
                    }
                }

                var removedKeys = new List<MaterialKey>();
                foreach (MaterialKey key in Definitions.Keys)
                {
                    if (key.ObjectId == payload.ObjectId && !updated.ContainsKey(key))
                        removedKeys.Add(key);
                }

                foreach (MaterialKey key in removedKeys)
                {
                    Definitions.Remove(key);
                    DestroyResolvedMaterial(key);
                }

                foreach (KeyValuePair<MaterialKey, MaterialDefinition> entry in updated)
                {
                    if (Definitions.TryGetValue(entry.Key, out MaterialDefinition previous) &&
                        previous.IsEquivalentTo(entry.Value))
                        continue;

                    Definitions[entry.Key] = entry.Value;
                    RefreshResolvedMaterial(entry.Key);
                }

                MissingWarnings.Clear();
            }
            catch (ArgumentException exception)
            {
                BridgeServer.ReportApplicationError(
                    $"Could not read Blender material texture metadata for {payload.ObjectId}: {exception.Message}");
            }
        }

        public static Material Resolve(Guid objectId, uint materialIndex)
        {
            var key = new MaterialKey(objectId, materialIndex);
            if (Resolved.TryGetValue(key, out ResolvedMaterial resolved) && resolved.Material != null)
                return resolved.Material;

            if (resolved != null)
                DestroyResolvedMaterial(key);

            bool hasDefinition = Definitions.TryGetValue(key, out MaterialDefinition definition);
            Shader shader = GetPreviewShader();
            var material = new Material(shader)
            {
                name = hasDefinition
                    ? $"Blender Preview - {definition.Name}"
                    : $"Blender Preview Fallback - {objectId:N} - {materialIndex}",
                hideFlags = HideFlags.DontSaveInEditor
            };

            resolved = new ResolvedMaterial(key, material);
            Resolved.Add(key, resolved);
            if (hasDefinition)
            {
                try
                {
                    ApplyDefinition(resolved, definition);
                }
                catch
                {
                    DestroyResolvedMaterial(key);
                    throw;
                }
            }
            else
            {
                ApplyFallbackColor(material, materialIndex);
                WarnMissingMaterial(key);
            }

            return material;
        }

        public static void Clear()
        {
            Definitions.Clear();
            MissingWarnings.Clear();

            var keys = new List<MaterialKey>(Resolved.Keys);
            foreach (MaterialKey key in keys)
                DestroyResolvedMaterial(key);

            if (AssetDatabase.IsValidFolder(LiveTextureCacheFolder))
                AssetDatabase.DeleteAsset(LiveTextureCacheFolder);
        }

        public static void ReleaseObject(Guid objectId)
        {
            var keysToRemove = new List<MaterialKey>();
            foreach (MaterialKey key in Definitions.Keys)
            {
                if (key.ObjectId == objectId)
                    keysToRemove.Add(key);
            }

            foreach (MaterialKey key in keysToRemove)
                Definitions.Remove(key);

            keysToRemove.Clear();
            foreach (MaterialKey key in Resolved.Keys)
            {
                if (key.ObjectId == objectId)
                    keysToRemove.Add(key);
            }

            foreach (MaterialKey key in keysToRemove)
                DestroyResolvedMaterial(key);

            keysToRemove.Clear();
            foreach (MaterialKey key in MissingWarnings.Keys)
            {
                if (key.ObjectId == objectId)
                    keysToRemove.Add(key);
            }

            foreach (MaterialKey key in keysToRemove)
                MissingWarnings.Remove(key);
        }

        private static Shader GetPreviewShader()
        {
            if (previewShader != null)
                return previewShader;

            previewShader = Shader.Find("Universal Render Pipeline/Lit");
            if (previewShader == null)
                previewShader = Shader.Find("Standard");
            if (previewShader == null)
                previewShader = Shader.Find("Hidden/InternalErrorShader");
            if (previewShader == null)
                throw new InvalidOperationException(
                    "No supported shader is available for Blender Bridge preview materials.");
            return previewShader;
        }

        private static void RefreshResolvedMaterial(MaterialKey key)
        {
            if (!Resolved.TryGetValue(key, out ResolvedMaterial resolved) ||
                resolved.Material == null ||
                !Definitions.TryGetValue(key, out MaterialDefinition definition))
                return;

            try
            {
                ApplyDefinition(resolved, definition);
            }
            catch (UnityException exception)
            {
                ReportMaterialUpdateFailure(key, definition, exception);
            }
            catch (InvalidOperationException exception)
            {
                ReportMaterialUpdateFailure(key, definition, exception);
            }
            catch (ArgumentException exception)
            {
                ReportMaterialUpdateFailure(key, definition, exception);
            }
            catch (OverflowException exception)
            {
                ReportMaterialUpdateFailure(key, definition, exception);
            }
        }

        private static void ReportMaterialUpdateFailure(
            MaterialKey key,
            MaterialDefinition definition,
            Exception exception)
        {
            BridgeServer.ReportApplicationWarning(
                $"Could not update Blender material '{definition.Name}' for object {key.ObjectId}, " +
                $"slot {key.MaterialIndex}: {exception.Message}");
        }

        private static void ApplyDefinition(
            ResolvedMaterial resolved,
            MaterialDefinition definition)
        {
            Material material = resolved.Material;
            MaterialDefinition previous = resolved.AppliedDefinition;
            material.name = $"Blender Preview - {definition.Name}";
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", definition.BaseColor);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", definition.BaseColor);
            if (material.HasProperty("_Metallic"))
                material.SetFloat("_Metallic", definition.Metallic);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", definition.Smoothness);

            TextureMetadataSet textures = definition.Textures;
            resolved.TextureMetadataIncomplete = false;
            ApplyTextureChannel(
                resolved,
                TextureChannel.BaseColor,
                textures?.base_color,
                linear: false,
                properties: BaseColorProperties,
                keyword: null);
            Texture2D metallic = ApplyTextureChannel(
                resolved,
                TextureChannel.Metallic,
                textures?.metallic,
                linear: true,
                properties: null,
                keyword: null);
            Texture2D roughness = ApplyTextureChannel(
                resolved,
                TextureChannel.Roughness,
                textures?.roughness,
                linear: true,
                properties: null,
                keyword: null);
            ApplyTextureChannel(
                resolved,
                TextureChannel.Normal,
                textures?.normal,
                linear: true,
                properties: NormalProperties,
                keyword: "_NORMALMAP");
            Texture2D emission = ApplyTextureChannel(
                resolved,
                TextureChannel.Emission,
                textures?.emission,
                linear: false,
                properties: EmissionProperties,
                keyword: "_EMISSION");

            if (emission != null && material.HasProperty("_EmissionColor"))
                material.SetColor("_EmissionColor", Color.white);

            bool wantsCombined = metallic != null || roughness != null;
            bool combinedInputsChanged =
                previous == null ||
                !TextureMetadataEquals(previous.Textures?.metallic, textures?.metallic) ||
                !TextureMetadataEquals(previous.Textures?.roughness, textures?.roughness) ||
                !previous.Metallic.Equals(definition.Metallic) ||
                !previous.Smoothness.Equals(definition.Smoothness);

            if (!wantsCombined)
            {
                DestroyCombinedTexture(resolved);
            }
            else if (resolved.MetallicSmoothness == null || combinedInputsChanged)
            {
                DestroyCombinedTexture(resolved);
                resolved.MetallicSmoothness = BuildMetallicSmoothnessMap(
                    metallic,
                    roughness,
                    definition.Metallic,
                    definition.Smoothness);
            }

            if (material.HasProperty("_MetallicGlossMap"))
                material.SetTexture("_MetallicGlossMap", resolved.MetallicSmoothness);
            if (material.HasProperty("_MetallicMap"))
                material.SetTexture("_MetallicMap", resolved.MetallicSmoothness);
            if (resolved.MetallicSmoothness != null)
                material.EnableKeyword("_METALLICSPECGLOSSMAP");
            else
                material.DisableKeyword("_METALLICSPECGLOSSMAP");

            // If a channel kept a texture because the incoming metadata was
            // hash-only, keep the matching old definition too. Otherwise a later
            // full payload with the same hash would be considered a no-op and the
            // stale texture would never be replaced.
            if (!resolved.TextureMetadataIncomplete)
                resolved.AppliedDefinition = definition;
        }

        private static Texture2D ApplyTextureChannel(
            ResolvedMaterial resolved,
            TextureChannel channel,
            TextureMetadata metadata,
            bool linear,
            string[] properties,
            string keyword)
        {
            int index = (int)channel;
            TextureMetadata previous = GetTextureMetadata(resolved.AppliedDefinition, channel);

            // Blender omits texture bytes when it believes Unity already has the
            // content. Never destroy a texture that is already applied just
            // because the incoming hash-only metadata has no bytes to replace it
            // with; a later snapshot is the recovery path for a real mismatch.
            bool hashOnly = metadata != null &&
                            string.IsNullOrEmpty(metadata.png_base64) &&
                            string.IsNullOrWhiteSpace(metadata.path) &&
                            !string.IsNullOrEmpty(metadata.hash);
            if (hashOnly && resolved.Textures[index] != null)
            {
                // Keep the existing texture. If we cannot prove it matches the
                // incoming hash, leave AppliedDefinition untouched so the next
                // payload with bytes forces a real reload.
                if (previous == null || !TextureMetadataEquals(previous, metadata))
                    resolved.TextureMetadataIncomplete = true;
            }
            else if (!TextureMetadataEquals(previous, metadata))
            {
                string previousNormalMapAssetPath =
                    channel == TextureChannel.Normal ? resolved.NormalMapAssetPath : null;
                ReleaseChannelTexture(resolved, channel, deleteNormalMapAsset: false);
                string normalMapAssetPath = string.Empty;
                if (metadata != null)
                {
                    resolved.Textures[index] = LoadTexture(
                        metadata,
                        linear,
                        resolved.Key,
                        normalMap: channel == TextureChannel.Normal,
                        readable: channel == TextureChannel.Metallic ||
                                  channel == TextureChannel.Roughness,
                        out normalMapAssetPath);
                }

                if (channel == TextureChannel.Normal)
                {
                    if (!string.IsNullOrEmpty(previousNormalMapAssetPath) &&
                        !string.Equals(
                            previousNormalMapAssetPath,
                            normalMapAssetPath,
                            StringComparison.Ordinal))
                        DeleteGeneratedNormalMapAsset(previousNormalMapAssetPath);
                    resolved.NormalMapAssetPath = normalMapAssetPath;
                }
            }

            Texture2D texture = resolved.Textures[index];
            if (properties != null)
            {
                foreach (string property in properties)
                {
                    if (resolved.Material.HasProperty(property))
                        resolved.Material.SetTexture(property, texture);
                }
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                if (texture != null)
                    resolved.Material.EnableKeyword(keyword);
                else
                    resolved.Material.DisableKeyword(keyword);
            }

            return texture;
        }

        private static TextureMetadata GetTextureMetadata(
            MaterialDefinition definition,
            TextureChannel channel)
        {
            if (definition?.Textures == null)
                return null;

            switch (channel)
            {
                case TextureChannel.BaseColor:
                    return definition.Textures.base_color;
                case TextureChannel.Metallic:
                    return definition.Textures.metallic;
                case TextureChannel.Roughness:
                    return definition.Textures.roughness;
                case TextureChannel.Normal:
                    return definition.Textures.normal;
                case TextureChannel.Emission:
                    return definition.Textures.emission;
                default:
                    return null;
            }
        }

        private static void ReleaseChannelTexture(
            ResolvedMaterial resolved,
            TextureChannel channel,
            bool deleteNormalMapAsset = true)
        {
            int index = (int)channel;
            Texture2D texture = resolved.Textures[index];
            resolved.Textures[index] = null;
            if (channel == TextureChannel.Normal)
            {
                string assetPath = resolved.NormalMapAssetPath;
                resolved.NormalMapAssetPath = null;
                if (deleteNormalMapAsset && !string.IsNullOrEmpty(assetPath))
                    DeleteGeneratedNormalMapAsset(assetPath);
                return;
            }

            if (texture != null)
                UnityEngine.Object.DestroyImmediate(texture);
        }

        private static void DestroyCombinedTexture(ResolvedMaterial resolved)
        {
            if (resolved.MetallicSmoothness == null)
                return;

            UnityEngine.Object.DestroyImmediate(resolved.MetallicSmoothness);
            resolved.MetallicSmoothness = null;
        }

        private static Texture2D LoadTexture(
            TextureMetadata metadata,
            bool linear,
            MaterialKey key,
            bool normalMap,
            bool readable,
            out string normalMapAssetPath)
        {
            normalMapAssetPath = string.Empty;
            if (metadata == null)
                return null;

            byte[] fileBytes = null;
            if (!metadata.prefer_embedded && !string.IsNullOrWhiteSpace(metadata.path))
            {
                try
                {
                    if (File.Exists(metadata.path))
                        fileBytes = File.ReadAllBytes(metadata.path);
                }
                catch (IOException exception)
                {
                    BridgeServer.ReportApplicationWarning(
                        $"Could not read Blender texture '{metadata.path}': {exception.Message}");
                }
                catch (UnauthorizedAccessException exception)
                {
                    BridgeServer.ReportApplicationWarning(
                        $"Could not read Blender texture '{metadata.path}': {exception.Message}");
                }
            }

            byte[] embeddedBytes = null;
            if (!string.IsNullOrEmpty(metadata.png_base64))
            {
                try
                {
                    embeddedBytes = Convert.FromBase64String(metadata.png_base64);
                }
                catch (FormatException exception)
                {
                    BridgeServer.ReportApplicationWarning(
                        $"Embedded Blender texture data is invalid: {exception.Message}");
                }
            }

            if (normalMap)
            {
                if (fileBytes != null && fileBytes.Length > 0)
                {
                    Texture2D imported = CreateImportedNormalMap(
                        fileBytes,
                        key,
                        out normalMapAssetPath);
                    if (imported != null)
                        return imported;
                }

                if (embeddedBytes != null && embeddedBytes.Length > 0 &&
                    (fileBytes == null || !BytesEqual(fileBytes, embeddedBytes)))
                {
                    Texture2D imported = CreateImportedNormalMap(
                        embeddedBytes,
                        key,
                        out normalMapAssetPath);
                    if (imported != null)
                        return imported;
                }

                if ((fileBytes == null || fileBytes.Length == 0) &&
                    (embeddedBytes == null || embeddedBytes.Length == 0))
                {
                    BridgeServer.ReportApplicationWarning(
                        $"Blender texture '{metadata.path}' is unavailable and has no embedded fallback.");
                }

                return null;
            }

            Texture2D texture = TryDecodeTexture(
                fileBytes,
                linear,
                metadata.path,
                readable);
            if (texture == null)
                texture = TryDecodeTexture(
                    embeddedBytes,
                    linear,
                    metadata.path,
                    readable);
            if (texture != null)
                return texture;

            if ((fileBytes == null || fileBytes.Length == 0) &&
                (embeddedBytes == null || embeddedBytes.Length == 0))
            {
                BridgeServer.ReportApplicationWarning(
                    $"Blender texture '{metadata.path}' is unavailable and has no embedded fallback.");
                return null;
            }

            BridgeServer.ReportApplicationWarning(
                $"Blender texture '{metadata.path}' could not be decoded from its file or embedded PNG fallback.");
            return null;
        }

        private static Texture2D CreateImportedNormalMap(
            byte[] imageBytes,
            MaterialKey key,
            out string assetPath)
        {
            assetPath = string.Empty;
            try
            {
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    BridgeServer.ReportApplicationWarning(
                        $"Could not import Blender normal map for object {key.ObjectId}, material slot {key.MaterialIndex}: the PNG data is empty.");
                    return null;
                }

                string contentHash = GetContentHash(imageBytes);
                string createdAssetPath = GetNormalMapAssetPath(key, contentHash);
                string absolutePath = Path.GetFullPath(Path.Combine(
                    Directory.GetParent(Application.dataPath).FullName,
                    createdAssetPath));
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));

                bool needsImport = !File.Exists(absolutePath) ||
                                   !FileContentsEqual(absolutePath, imageBytes);
                if (needsImport)
                {
                    File.WriteAllBytes(absolutePath, imageBytes);
                    AssetDatabase.ImportAsset(
                        createdAssetPath,
                        ImportAssetOptions.ForceSynchronousImport);
                }

                TextureImporter importer =
                    AssetImporter.GetAtPath(createdAssetPath) as TextureImporter;
                if (importer == null)
                {
                    BridgeServer.ReportApplicationWarning(
                        $"Unity could not create a texture importer for Blender normal map '{createdAssetPath}'.");
                    DeleteGeneratedNormalMapAsset(createdAssetPath);
                    return null;
                }

                if (importer.textureType != TextureImporterType.NormalMap || importer.sRGBTexture)
                {
                    importer.textureType = TextureImporterType.NormalMap;
                    importer.sRGBTexture = false;
                    importer.SaveAndReimport();
                }

                Texture2D normalMap = AssetDatabase.LoadAssetAtPath<Texture2D>(createdAssetPath);
                if (normalMap == null)
                {
                    BridgeServer.ReportApplicationWarning(
                        $"Unity could not load the imported Blender normal map '{createdAssetPath}'.");
                    DeleteGeneratedNormalMapAsset(createdAssetPath);
                    return null;
                }

                assetPath = createdAssetPath;
                return normalMap;
            }
            catch (IOException exception)
            {
                BridgeServer.ReportApplicationWarning(
                    $"Could not stage Blender normal map '{assetPath}': {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                BridgeServer.ReportApplicationWarning(
                    $"Could not stage Blender normal map '{assetPath}': {exception.Message}");
            }
            catch (UnityException exception)
            {
                BridgeServer.ReportApplicationWarning(
                    $"Unity could not import Blender normal map '{assetPath}': {exception.Message}");
            }

            return null;
        }

        private static string GetNormalMapAssetPath(MaterialKey key, string contentHash)
        {
            return $"{LiveTextureCacheFolder}/{key.ObjectId:N}/{key.MaterialIndex}_Normal_{contentHash}.png";
        }

        private static string GetContentHash(byte[] imageBytes)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(imageBytes ?? Array.Empty<byte>());
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static bool FileContentsEqual(string path, byte[] expected)
        {
            byte[] current = File.ReadAllBytes(path);
            if (current.Length != expected.Length)
                return false;

            for (int i = 0; i < current.Length; i++)
            {
                if (current[i] != expected[i])
                    return false;
            }

            return true;
        }

        private static void DeleteGeneratedNormalMapAsset(string assetPath)
        {
            if (!assetPath.StartsWith(
                    LiveTextureCacheFolder + "/",
                    StringComparison.Ordinal))
                return;

            AssetDatabase.DeleteAsset(assetPath);
            string folderPath = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            while (!string.IsNullOrEmpty(folderPath) &&
                   folderPath.StartsWith(LiveTextureCacheFolder, StringComparison.Ordinal))
            {
                string absoluteFolderPath = Path.Combine(projectRoot, folderPath);
                if (!Directory.Exists(absoluteFolderPath) ||
                    Directory.GetFileSystemEntries(absoluteFolderPath).Length != 0)
                    break;

                AssetDatabase.DeleteAsset(folderPath);
                folderPath = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            }
        }

        private static Texture2D TryDecodeTexture(
            byte[] imageBytes,
            bool linear,
            string path,
            bool readable)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                return null;

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear)
            {
                name = string.IsNullOrWhiteSpace(path)
                    ? "Blender Live Texture"
                    : Path.GetFileName(path),
                hideFlags = HideFlags.DontSaveInEditor
            };
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;
            if (ImageConversion.LoadImage(texture, imageBytes, markNonReadable: !readable))
                return texture;

            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        private static Texture2D BuildMetallicSmoothnessMap(
            Texture2D metallic,
            Texture2D roughness,
            float metallicValue,
            float smoothnessValue)
        {
            int width = Mathf.Clamp(
                Mathf.Max(metallic != null ? metallic.width : 1, roughness != null ? roughness.width : 1),
                1,
                4096);
            int height = Mathf.Clamp(
                Mathf.Max(metallic != null ? metallic.height : 1, roughness != null ? roughness.height : 1),
                1,
                4096);

            Color32[] metallicPixels = null;
            bool metallicDirect = false;
            if (metallic != null)
            {
                metallicPixels = metallic.GetPixels32();
                metallicDirect = metallic.width == width && metallic.height == height;
            }

            Color32[] roughnessPixels = null;
            bool roughnessDirect = false;
            if (roughness != null)
            {
                roughnessPixels = roughness.GetPixels32();
                roughnessDirect = roughness.width == width && roughness.height == height;
            }

            byte metallicByte = ToByte(metallicValue);
            byte smoothnessByte = ToByte(smoothnessValue);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = (y + 0.5f) / height;
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    byte metal;
                    if (metallicPixels == null)
                    {
                        metal = metallicByte;
                    }
                    else if (metallicDirect)
                    {
                        metal = metallicPixels[row + x].r;
                    }
                    else
                    {
                        metal = ToByte(SampleRed(
                            metallicPixels,
                            metallic.width,
                            metallic.height,
                            (x + 0.5f) / width,
                            v));
                    }

                    byte smoothness;
                    if (roughnessPixels == null)
                    {
                        smoothness = smoothnessByte;
                    }
                    else
                    {
                        float roughnessValue = roughnessDirect
                            ? roughnessPixels[row + x].r / 255f
                            : SampleRed(
                                roughnessPixels,
                                roughness.width,
                                roughness.height,
                                (x + 0.5f) / width,
                                v);
                        smoothness = ToByte(1f - roughnessValue);
                    }

                    pixels[row + x] = new Color32(metal, metal, metal, smoothness);
                }
            }

            var combined = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                name = "Blender Metallic Smoothness",
                hideFlags = HideFlags.DontSaveInEditor
            };
            combined.SetPixels32(pixels);
            combined.Apply(false, false);
            return combined;
        }

        private static float SampleRed(
            Color32[] pixels,
            int sourceWidth,
            int sourceHeight,
            float u,
            float v)
        {
            float fx = u * sourceWidth - 0.5f;
            float fy = v * sourceHeight - 0.5f;
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0;
            float ty = fy - y0;
            int x1 = WrapCoordinate(x0 + 1, sourceWidth);
            int y1 = WrapCoordinate(y0 + 1, sourceHeight);
            x0 = WrapCoordinate(x0, sourceWidth);
            y0 = WrapCoordinate(y0, sourceHeight);

            float top = Mathf.Lerp(
                pixels[y0 * sourceWidth + x0].r,
                pixels[y0 * sourceWidth + x1].r,
                tx);
            float bottom = Mathf.Lerp(
                pixels[y1 * sourceWidth + x0].r,
                pixels[y1 * sourceWidth + x1].r,
                tx);
            return Mathf.Lerp(top, bottom, ty) / 255f;
        }

        private static int WrapCoordinate(int value, int size)
        {
            if (value < 0)
                value += size * ((-value + size - 1) / size);
            return value % size;
        }

        private static byte ToByte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        private static void ApplyFallbackColor(Material material, uint materialIndex)
        {
            float hue = (materialIndex * 0.61803398875f) % 1f;
            Color color = Color.HSVToRGB(hue, 0.72f, 0.95f);
            color.a = 1f;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
        }

        private static void WarnMissingMaterial(MaterialKey key)
        {
            if (MissingWarnings.ContainsKey(key))
                return;

            string warning =
                $"No Blender material metadata was found for object {key.ObjectId}, material slot {key.MaterialIndex}; using a visible preview fallback.";
            MissingWarnings.Add(key, warning);
            BridgeServer.ReportApplicationWarning(warning);
        }

        private static void DestroyResolvedMaterial(MaterialKey key)
        {
            if (!Resolved.TryGetValue(key, out ResolvedMaterial resolved))
                return;

            Resolved.Remove(key);
            for (int i = 0; i < (int)TextureChannel.Count; i++)
                ReleaseChannelTexture(resolved, (TextureChannel)i);
            DestroyCombinedTexture(resolved);
            if (resolved.Material != null)
                UnityEngine.Object.DestroyImmediate(resolved.Material);
        }

        private static bool HasAnyTexture(TextureMetadataSet textures)
        {
            if (textures == null)
                return false;

            return textures.base_color != null ||
                   textures.metallic != null ||
                   textures.roughness != null ||
                   textures.normal != null ||
                   textures.emission != null;
        }

        private static bool HasTextureSource(TextureMetadata metadata)
        {
            return metadata != null &&
                   (!string.IsNullOrEmpty(metadata.png_base64) ||
                    !string.IsNullOrWhiteSpace(metadata.path));
        }

        private static bool TextureMetadataEquals(TextureMetadata left, TextureMetadata right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;
            if (!string.IsNullOrEmpty(left.hash) &&
                string.Equals(left.hash, right.hash, StringComparison.Ordinal) &&
                HasTextureSource(left) == HasTextureSource(right))
                return true;

            return string.Equals(left.path, right.path, StringComparison.Ordinal) &&
                   left.prefer_embedded == right.prefer_embedded &&
                   string.Equals(left.png_base64, right.png_base64, StringComparison.Ordinal);
        }

        private enum TextureChannel
        {
            BaseColor = 0,
            Metallic = 1,
            Roughness = 2,
            Normal = 3,
            Emission = 4,
            Count = 5
        }

        private sealed class ResolvedMaterial
        {
            public ResolvedMaterial(MaterialKey key, Material material)
            {
                Key = key;
                Material = material;
            }

            public MaterialKey Key { get; }
            public Material Material { get; }
            public MaterialDefinition AppliedDefinition;
            public bool TextureMetadataIncomplete;
            public readonly Texture2D[] Textures = new Texture2D[(int)TextureChannel.Count];
            public Texture2D MetallicSmoothness;
            public string NormalMapAssetPath;
        }

        [Serializable]
        private sealed class HelloMetadata
        {
            public MaterialMetadata[] materials = Array.Empty<MaterialMetadata>();
        }

        [Serializable]
        private sealed class MaterialsMetadata
        {
            public MaterialMetadata[] materials = Array.Empty<MaterialMetadata>();
        }

        [Serializable]
        private sealed class MaterialMetadata
        {
            public string object_uuid = string.Empty;
            public int index = -1;
            public string name = string.Empty;
            public float[] base_color = Array.Empty<float>();
            public float metallic = 0f;
            public float smoothness = 0f;
            public TextureMetadataSet textures = null;
        }

        [Serializable]
        private sealed class TextureMetadataSet
        {
            public TextureMetadata base_color = null;
            public TextureMetadata metallic = null;
            public TextureMetadata roughness = null;
            public TextureMetadata normal = null;
            public TextureMetadata emission = null;
        }

        [Serializable]
        private sealed class TextureMetadata
        {
            public string path = string.Empty;
            public bool prefer_embedded = false;
            public string hash = string.Empty;
            public string png_base64 = string.Empty;
        }

        private sealed class MaterialDefinition
        {
            public MaterialDefinition(MaterialMetadata metadata)
                : this(metadata, metadata.textures)
            {
            }

            public MaterialDefinition(MaterialMetadata metadata, TextureMetadataSet textures)
            {
                Name = string.IsNullOrWhiteSpace(metadata.name)
                    ? $"Material {metadata.index}"
                    : metadata.name;
                float[] sourceColor = metadata.base_color;
                BaseColor = sourceColor != null && sourceColor.Length == 4 &&
                            IsFinite(sourceColor[0]) && IsFinite(sourceColor[1]) &&
                            IsFinite(sourceColor[2]) && IsFinite(sourceColor[3])
                    ? new Color(
                        Mathf.Clamp01(sourceColor[0]),
                        Mathf.Clamp01(sourceColor[1]),
                        Mathf.Clamp01(sourceColor[2]),
                        Mathf.Clamp01(sourceColor[3]))
                    : Color.magenta;
                Metallic = IsFinite(metadata.metallic) ? Mathf.Clamp01(metadata.metallic) : 0f;
                Smoothness = IsFinite(metadata.smoothness) ? Mathf.Clamp01(metadata.smoothness) : 0.5f;
                Textures = textures;
            }

            public string Name { get; }
            public Color BaseColor { get; }
            public float Metallic { get; }
            public float Smoothness { get; }
            public TextureMetadataSet Textures { get; }

            public bool IsEquivalentTo(MaterialDefinition other)
            {
                return other != null &&
                       string.Equals(Name, other.Name, StringComparison.Ordinal) &&
                       BaseColor.Equals(other.BaseColor) &&
                       Metallic.Equals(other.Metallic) &&
                       Smoothness.Equals(other.Smoothness) &&
                       TextureMetadataEquals(Textures?.base_color, other.Textures?.base_color) &&
                       TextureMetadataEquals(Textures?.metallic, other.Textures?.metallic) &&
                       TextureMetadataEquals(Textures?.roughness, other.Textures?.roughness) &&
                       TextureMetadataEquals(Textures?.normal, other.Textures?.normal) &&
                       TextureMetadataEquals(Textures?.emission, other.Textures?.emission);
            }
        }

        private struct MaterialKey : IEquatable<MaterialKey>
        {
            public MaterialKey(Guid objectId, uint materialIndex)
            {
                ObjectId = objectId;
                MaterialIndex = materialIndex;
            }

            public Guid ObjectId { get; }
            public uint MaterialIndex { get; }

            public bool Equals(MaterialKey other)
            {
                return ObjectId.Equals(other.ObjectId) && MaterialIndex == other.MaterialIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is MaterialKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (ObjectId.GetHashCode() * 397) ^ (int)MaterialIndex;
                }
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
