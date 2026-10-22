using UnityEngine;

namespace BlenderBridge.Editor
{
    public sealed class BlenderBakeMetadata : ScriptableObject
    {
        public string sourceAssetPath;
        public string sourceAssetGuid;
        public string sourceDependencyHash;
        public string exporterVersion;
        public string prefabAssetPath;
        public string lastBakedUtc;
        public string[] sourceObjectPaths;
        public string[] sourceObjectLocalIds;
        public string[] materialAssetPaths;
    }
}
