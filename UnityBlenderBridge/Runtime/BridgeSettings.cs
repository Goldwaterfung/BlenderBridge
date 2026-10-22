using UnityEngine;

namespace BlenderBridge
{
    public enum BridgeColliderPolicy
    {
        None,
        MeshCollider
    }

    [CreateAssetMenu(
        fileName = "BlenderBridgeSettings",
        menuName = "Blender Bridge/Settings",
        order = 0)]
    public sealed class BridgeSettings : ScriptableObject
    {
        public const int DefaultPort = 8191;
        public const string DefaultOutputFolder = "Assets/Art/Blender";
        public const int DefaultMaximumPendingStateSlots = 100000;
        public const int DefaultMaximumQueuedControlEvents = 8192;
        public const int DefaultMaximumPendingInboxMegabytes = 512;
        public const int MaximumPendingStateSlotsLimit = 1000000;
        public const int MaximumQueuedControlEventsLimit = 65536;
        public const int MaximumPendingInboxMegabytesLimit = 4096;

        [SerializeField, Range(1, 65535), Tooltip("Loopback TCP port used by the Unity Editor server.")]
        private int port = DefaultPort;

        [SerializeField, Tooltip("Project-relative folder for baked assets.")]
        private string outputFolder = DefaultOutputFolder;

        [SerializeField, Tooltip("Default collider behavior for an explicit bake.")]
        private BridgeColliderPolicy colliderPolicy = BridgeColliderPolicy.None;

        [SerializeField, Range(1, MaximumPendingStateSlotsLimit),
         Tooltip("Maximum distinct object/type state slots waiting for the Editor thread.")]
        private int maximumPendingStateSlots = DefaultMaximumPendingStateSlots;

        [SerializeField, Range(1, MaximumQueuedControlEventsLimit),
         Tooltip("Maximum ordered lifecycle and handshake events waiting for the Editor thread.")]
        private int maximumQueuedControlEvents = DefaultMaximumQueuedControlEvents;

        [SerializeField, Range(16, MaximumPendingInboxMegabytesLimit),
         Tooltip("Maximum estimated memory for all pending bridge messages.")]
        private int maximumPendingInboxMegabytes = DefaultMaximumPendingInboxMegabytes;

        public int Port => port;
        public string OutputFolder => outputFolder;
        public BridgeColliderPolicy ColliderPolicy => colliderPolicy;
        public int MaximumPendingStateSlots => maximumPendingStateSlots;
        public int MaximumQueuedControlEvents => maximumQueuedControlEvents;
        public long MaximumPendingInboxBytes => (long)maximumPendingInboxMegabytes * 1024 * 1024;

        public bool TryValidate(out string error)
        {
            if (port < 1 || port > 65535)
            {
                error = "The bridge port must be between 1 and 65535.";
                return false;
            }

            if (!IsValidProjectAssetFolder(outputFolder))
            {
                error = "The output folder must be a project-relative path below Assets, using '/' separators and no '.' or '..' segments.";
                return false;
            }

            if (!System.Enum.IsDefined(typeof(BridgeColliderPolicy), colliderPolicy))
            {
                error = "The collider policy value is not supported.";
                return false;
            }

            if (maximumPendingStateSlots < 1 ||
                maximumPendingStateSlots > MaximumPendingStateSlotsLimit)
            {
                error = $"The pending state slot limit must be between 1 and {MaximumPendingStateSlotsLimit}.";
                return false;
            }

            if (maximumQueuedControlEvents < 1 ||
                maximumQueuedControlEvents > MaximumQueuedControlEventsLimit)
            {
                error = $"The ordered control event limit must be between 1 and {MaximumQueuedControlEventsLimit}.";
                return false;
            }

            if (maximumPendingInboxMegabytes < 16 ||
                maximumPendingInboxMegabytes > MaximumPendingInboxMegabytesLimit)
            {
                error = $"The pending inbox memory limit must be between 16 and {MaximumPendingInboxMegabytesLimit} MiB.";
                return false;
            }

            error = null;
            return true;
        }

        private static bool IsValidProjectAssetFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !path.StartsWith("Assets/", System.StringComparison.Ordinal) ||
                path.IndexOf('\\') >= 0)
            {
                return false;
            }

            string[] segments = path.Split('/');
            if (segments.Length < 2 || segments[0] != "Assets")
            {
                return false;
            }

            for (int i = 1; i < segments.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(segments[i]) ||
                    segments[i] == "." ||
                    segments[i] == ".." ||
                    segments[i].EndsWith(".", System.StringComparison.Ordinal) ||
                    segments[i].EndsWith(" ", System.StringComparison.Ordinal) ||
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

        private static bool IsWindowsReservedName(string name)
        {
            string stem = name.Split('.')[0];
            if (stem.Equals("CON", System.StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", System.StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", System.StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return stem.Length == 4 &&
                   (stem.StartsWith("COM", System.StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith("LPT", System.StringComparison.OrdinalIgnoreCase)) &&
                   stem[3] >= '1' && stem[3] <= '9';
        }
    }
}
