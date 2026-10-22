using UnityEditor;
using UnityEngine;

namespace BlenderBridge.Editor
{
    public sealed class BridgeStatusWindow : EditorWindow
    {
        private double nextRepaintTime;

        [MenuItem("Window/Blender Bridge/Status")]
        public static void Open()
        {
            GetWindow<BridgeStatusWindow>("Blender Bridge");
        }

        private void OnGUI()
        {
            if (EditorApplication.timeSinceStartup >= nextRepaintTime)
            {
                nextRepaintTime = EditorApplication.timeSinceStartup + 0.5;
                Repaint();
            }

            EditorGUILayout.LabelField("Connection", BridgeServer.Status.ToString());
            EditorGUILayout.LabelField("Listening port", BridgeServer.ListeningPort.ToString());
            EditorGUILayout.LabelField(
                "Peer",
                string.IsNullOrEmpty(BridgeServer.PeerName) ? "-" : BridgeServer.PeerName);
            EditorGUILayout.LabelField(
                "Session",
                string.IsNullOrEmpty(BridgeServer.SessionId) ? "-" : BridgeServer.SessionId);
            EditorGUILayout.LabelField(
                "Unit scale",
                BridgeServer.PeerUnitScale > 0f
                    ? BridgeServer.PeerUnitScale.ToString("G6")
                    : "-");
            EditorGUILayout.LabelField(
                "Snapshot",
                BridgeServer.HasCommittedSnapshot ? "Fresh and committed" : "Not committed");
            EditorGUILayout.LabelField(
                "Live objects",
                BridgeLiveScene.LiveObjectCount.ToString());

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Traffic", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Packets received", BridgeServer.ReceivedPacketCount.ToString("N0"));
            EditorGUILayout.LabelField(
                "Payload received",
                EditorUtility.FormatBytes(BridgeServer.ReceivedPayloadBytes));
            EditorGUILayout.LabelField("Packets applied", BridgeServer.AppliedPacketCount.ToString("N0"));
            EditorGUILayout.LabelField(
                "Queued",
                $"{BridgeServer.PendingMessageCount} packets / {EditorUtility.FormatBytes(BridgeServer.PendingInboxBytes)}");
            EditorGUILayout.LabelField(
                "Mesh uploads",
                $"{BridgeLiveScene.MeshUploadCount:N0} / last {BridgeLiveScene.LastMeshUploadMilliseconds:F2} ms / total {BridgeLiveScene.TotalMeshUploadMilliseconds:F1} ms");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Editor main thread", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Update callback",
                $"last {BridgeServer.LastUpdateMilliseconds:F2} ms / avg {BridgeServer.AverageUpdateMilliseconds:F2} ms / peak {BridgeServer.PeakUpdateMilliseconds:F2} ms");
            EditorGUILayout.LabelField(
                "Inbox drain",
                $"last {BridgeServer.LastDrainMilliseconds:F2} ms");
            EditorGUILayout.LabelField(
                "Live apply",
                $"last {BridgeServer.LastLiveApplyMilliseconds:F2} ms / avg {BridgeServer.AverageLiveApplyMilliseconds:F2} ms");
            EditorGUILayout.LabelField(
                "Snapshot apply",
                $"last {BridgeServer.LastSnapshotApplyMilliseconds:F2} ms ({BridgeServer.LastSnapshotMessageCount:N0} msgs) / avg {BridgeServer.AverageSnapshotApplyMilliseconds:F2} ms");
            if (BridgeServer.SnapshotApplyMessageCount > 0)
            {
                EditorGUILayout.LabelField(
                    "Snapshot staging",
                    $"{BridgeServer.SnapshotAppliedMessageCount:N0} / {BridgeServer.SnapshotApplyMessageCount:N0} messages");
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "Ping/Pong",
                $"{BridgeServer.PingEchoCount:N0} echoes; RTT is measured by Blender from the echoed sender timestamp.");

            if (!string.IsNullOrEmpty(BridgeServer.LastError))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(BridgeServer.LastError, MessageType.Error);
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(
                           BridgeServer.Status != BridgeServerStatus.Stopped))
                {
                    if (GUILayout.Button("Start"))
                        BridgeServer.Start();
                }

                using (new EditorGUI.DisabledScope(BridgeServer.Status == BridgeServerStatus.Stopped))
                {
                    if (GUILayout.Button("Stop"))
                        BridgeServer.Stop();
                }

                using (new EditorGUI.DisabledScope(BridgeServer.Status != BridgeServerStatus.Connected))
                {
                    if (GUILayout.Button("Request Resync"))
                        BridgeServer.RequestResyncFromWindow();
                }
            }
        }
    }
}
