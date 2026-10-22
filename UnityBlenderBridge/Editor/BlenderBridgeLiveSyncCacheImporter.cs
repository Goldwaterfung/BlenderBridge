using System;
using UnityEditor;

namespace BlenderBridge.Editor
{
    /// <summary>
    /// Imports generated Blender normal maps with the correct importer settings in a single pass,
    /// avoiding the extra <c>SaveAndReimport</c> that used to run after every normal map change.
    /// </summary>
    internal sealed class BlenderBridgeLiveSyncCacheImporter : AssetPostprocessor
    {
        private const string CacheFolderPrefix = "Assets/BlenderBridgeLiveSyncCache/";
        private const string NormalMapMarker = "_Normal_";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(CacheFolderPrefix, StringComparison.Ordinal) ||
                assetPath.IndexOf(NormalMapMarker, StringComparison.Ordinal) < 0)
                return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.NormalMap;
            importer.sRGBTexture = false;
        }
    }
}
