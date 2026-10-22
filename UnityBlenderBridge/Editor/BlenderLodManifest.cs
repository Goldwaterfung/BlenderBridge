using System;

namespace BlenderBridge.Editor
{
    [Serializable]
    public sealed class BlenderLodManifest
    {
        public int schemaVersion;
        public string fbxFile;
        public string coordinateSpace;
        public BlenderLodSetManifest[] lodSets;
    }

    [Serializable]
    public sealed class BlenderLodSetManifest
    {
        public string sourceName;
        public string sourceId;
        public string groupName;
        public BlenderLodLevelManifest[] levels;
    }

    [Serializable]
    public sealed class BlenderLodLevelManifest
    {
        public int level;
        public string nodeName;
        public string meshName;
        public string assignedObjectName;
        public int triangleCap;
        public int triangleCount;
        public int vertexCount;
    }
}
