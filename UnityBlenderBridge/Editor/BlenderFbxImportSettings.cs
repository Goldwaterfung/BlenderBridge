using System;
using System.IO;
using BlenderBridge;
using UnityEditor;

namespace BlenderBridge.Editor
{
    internal static class BlenderFbxImportSettings
    {
        private const string TextureRolePrefix = "BlenderBridge:sRGB=";
        private const string NormalMapPrefix = "BlenderBridge:NormalMap=";

        public static bool TryGetBridgeSettings(out BridgeSettings settings, out string error)
        {
            settings = null;
            error = null;
            string[] guids = AssetDatabase.FindAssets("t:BridgeSettings");
            if (guids.Length > 1)
            {
                error = "Multiple BridgeSettings assets were found. Keep exactly one settings asset.";
                return false;
            }

            if (guids.Length == 0)
                return true;

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            settings = AssetDatabase.LoadAssetAtPath<BridgeSettings>(path);
            if (settings == null)
            {
                error = $"Could not load BridgeSettings at '{path}'.";
                return false;
            }

            if (!settings.TryValidate(out error))
                return false;

            return true;
        }

        public static bool IsUnderOutputFolder(string assetPath, string outputFolder)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(outputFolder))
                return false;

            string normalized = assetPath.Replace('\\', '/');
            string folder = outputFolder.TrimEnd('/').Replace('\\', '/');
            return normalized.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryGetTextureSrgbHint(string assetPath, out bool isSrgb)
        {
            string suffix = GetTextureSuffix(assetPath);

            switch (suffix)
            {
                case "basecolor":
                case "base_color":
                case "albedo":
                case "diffuse":
                case "color":
                case "emission":
                case "emissive":
                case "emit":
                    isSrgb = true;
                    return true;
                case "normal":
                case "normalgl":
                case "normaldx":
                case "n":
                case "roughness":
                case "rough":
                case "metallic":
                case "metalness":
                case "metal":
                case "mask":
                case "masks":
                case "orm":
                case "ao":
                case "occlusion":
                    isSrgb = false;
                    return true;
                default:
                    isSrgb = false;
                    return false;
            }
        }

        public static bool IsNormalMapTextureHint(string assetPath)
        {
            string suffix = GetTextureSuffix(assetPath);
            return suffix == "normal" || suffix == "normalgl" || suffix == "normaldx" || suffix == "n";
        }

        private static string GetTextureSuffix(string assetPath)
        {
            string filename = Path.GetFileNameWithoutExtension(assetPath);
            int separator = filename.LastIndexOf('_');
            return (separator < 0 ? filename : filename.Substring(separator + 1)).ToLowerInvariant();
        }

        public static bool TryGetPersistedTextureRole(string userData, out bool isSrgb)
        {
            if (!string.IsNullOrEmpty(userData))
            {
                string[] lines = userData.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimEnd('\r');
                    if (!line.StartsWith(TextureRolePrefix, StringComparison.Ordinal))
                        continue;

                    string value = line.Substring(TextureRolePrefix.Length);
                    if (bool.TryParse(value, out isSrgb))
                        return true;
                }
            }

            isSrgb = false;
            return false;
        }

        public static string SetPersistedTextureRole(string userData, bool isSrgb)
        {
            return SetPersistedLine(userData, TextureRolePrefix, isSrgb.ToString());
        }

        public static bool TryGetPersistedNormalMapRole(string userData, out bool isNormalMap)
        {
            return TryGetPersistedBoolean(userData, NormalMapPrefix, out isNormalMap);
        }

        public static string SetPersistedNormalMapRole(string userData, bool isNormalMap)
        {
            return SetPersistedLine(userData, NormalMapPrefix, isNormalMap.ToString());
        }

        private static bool TryGetPersistedBoolean(string userData, string prefix, out bool value)
        {
            string[] lines = string.IsNullOrEmpty(userData)
                ? Array.Empty<string>()
                : userData.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                    bool.TryParse(line.Substring(prefix.Length), out value))
                    return true;
            }

            value = false;
            return false;
        }

        private static string SetPersistedLine(string userData, string prefix, string value)
        {
            string[] lines = string.IsNullOrEmpty(userData)
                ? Array.Empty<string>()
                : userData.Split('\n');
            var preservedLines = new System.Collections.Generic.List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                    preservedLines.Add(line);
            }

            preservedLines.Add(prefix + value);
            return string.Join("\n", preservedLines);
        }
    }

    public sealed class BlenderBridgeAssetPostprocessor : AssetPostprocessor
    {
        private void OnPreprocessModel()
        {
            if (!BlenderFbxImportSettings.TryGetBridgeSettings(out BridgeSettings settings, out string error))
            {
                UnityEngine.Debug.LogError("[Blender Bridge] " + error);
                return;
            }

            string outputFolder = settings == null
                ? BridgeSettings.DefaultOutputFolder
                : settings.OutputFolder;
            if (!BlenderFbxImportSettings.IsUnderOutputFolder(assetPath, outputFolder) ||
                !string.Equals(Path.GetExtension(assetPath), ".fbx", StringComparison.OrdinalIgnoreCase))
                return;

            var importer = (ModelImporter)assetImporter;
            importer.globalScale = 1f;
            importer.useFileScale = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.generateSecondaryUV = false;
            importer.importAnimation = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.preserveHierarchy = true;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }

        private void OnPreprocessTexture()
        {
            if (!BlenderFbxImportSettings.TryGetBridgeSettings(out BridgeSettings settings, out string error))
            {
                UnityEngine.Debug.LogError("[Blender Bridge] " + error);
                return;
            }

            string outputFolder = settings == null
                ? BridgeSettings.DefaultOutputFolder
                : settings.OutputFolder;
            if (!BlenderFbxImportSettings.IsUnderOutputFolder(assetPath, outputFolder) ||
                !TryGetTextureRole((TextureImporter)assetImporter, assetPath, out bool isSrgb))
                return;

            var importer = (TextureImporter)assetImporter;
            importer.sRGBTexture = isSrgb;
            bool hasPersistedNormalMapRole =
                BlenderFbxImportSettings.TryGetPersistedNormalMapRole(importer.userData, out bool isNormalMap);
            if (!hasPersistedNormalMapRole)
                isNormalMap = BlenderFbxImportSettings.IsNormalMapTextureHint(assetPath);
            importer.textureType = isNormalMap
                ? TextureImporterType.NormalMap
                : importer.textureType == TextureImporterType.NormalMap
                    ? TextureImporterType.Default
                    : importer.textureType;
        }

        private static bool TryGetTextureRole(
            TextureImporter importer,
            string path,
            out bool isSrgb)
        {
            return BlenderFbxImportSettings.TryGetPersistedTextureRole(importer.userData, out isSrgb) ||
                   BlenderFbxImportSettings.TryGetTextureSrgbHint(path, out isSrgb);
        }
    }
}
