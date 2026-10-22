using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BlenderBridge;
using UnityEditor;
using UnityEngine;

namespace BlenderBridge.Editor
{
    public static class BridgeLiveScene
    {
        private const string ContainerName = "__BlenderLive";
        private const HideFlags LiveObjectHideFlags = HideFlags.DontSaveInEditor;
        private static readonly int EditorMainThreadId = Thread.CurrentThread.ManagedThreadId;
        private static readonly Queue<SceneState> RetiredScenes = new Queue<SceneState>();
        private static SceneState activeScene = new SceneState(false);
        private static SceneState stagingScene;
        private static bool connectionWasActive;
        private static long meshUploadCount;
        private static double totalMeshUploadMilliseconds;
        private static double lastMeshUploadMilliseconds;

        public static int LiveObjectCount
        {
            get
            {
                EnsureMainThread();
                return activeScene.Objects.Count;
            }
        }

        public static long MeshUploadCount
        {
            get
            {
                EnsureMainThread();
                return meshUploadCount;
            }
        }

        public static double TotalMeshUploadMilliseconds
        {
            get
            {
                EnsureMainThread();
                return totalMeshUploadMilliseconds;
            }
        }

        public static double LastMeshUploadMilliseconds
        {
            get
            {
                EnsureMainThread();
                return lastMeshUploadMilliseconds;
            }
        }

        public static bool TryGetGameObject(Guid objectId, out GameObject gameObject)
        {
            EnsureMainThread();
            if (activeScene.Objects.TryGetValue(objectId, out LiveObject liveObject))
            {
                gameObject = liveObject.GameObject;
                return gameObject != null;
            }

            gameObject = null;
            return false;
        }

        public static void ApplyMessages(IReadOnlyList<BridgeInboundMessage> messages)
        {
            EnsureMainThread();
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));

            ApplyMessages(activeScene, messages);
        }

        internal static void BeginSnapshotReplacement()
        {
            EnsureMainThread();
            AbortSnapshotReplacement();
            stagingScene = new SceneState(true);
        }

        internal static void ApplySnapshotMessages(IReadOnlyList<BridgeInboundMessage> messages)
        {
            EnsureMainThread();
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));
            if (stagingScene == null)
                throw new InvalidOperationException("A snapshot replacement has not been started.");

            ApplyMessages(stagingScene, messages);
        }

        internal static void CommitSnapshotReplacement()
        {
            EnsureMainThread();
            if (stagingScene == null)
                throw new InvalidOperationException("A snapshot replacement has not been started.");

            SceneState previousScene = activeScene;
            stagingScene.Container?.SetActive(true);

            if (previousScene.Container != null)
            {
                previousScene.Container.SetActive(false);
                RetiredScenes.Enqueue(previousScene);
            }
            else
            {
                ClearScene(previousScene);
            }

            activeScene = stagingScene;
            activeScene.IsStaging = false;
            if (activeScene.Container != null)
                activeScene.Container.name = ContainerName;
            stagingScene = null;
        }

        internal static void AbortSnapshotReplacement()
        {
            EnsureMainThread();
            if (stagingScene == null)
                return;

            stagingScene.Container?.SetActive(false);
            RetiredScenes.Enqueue(stagingScene);
            stagingScene = null;
        }

        internal static void CleanupRetiredScenes(int maximumObjects)
        {
            CleanupRetiredScenes(maximumObjects, double.PositiveInfinity);
        }

        internal static void CleanupRetiredScenes(int maximumObjects, double deadline)
        {
            EnsureMainThread();
            if (maximumObjects < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumObjects));

            int remaining = maximumObjects;
            while (remaining > 0 &&
                   RetiredScenes.Count > 0 &&
                   EditorApplication.timeSinceStartup < deadline)
            {
                SceneState scene = RetiredScenes.Peek();
                while (remaining > 0 &&
                       scene.Objects.Count > 0 &&
                       EditorApplication.timeSinceStartup < deadline)
                {
                    using (Dictionary<Guid, LiveObject>.Enumerator enumerator =
                           scene.Objects.GetEnumerator())
                    {
                        if (!enumerator.MoveNext())
                            break;
                        DestroyLiveObject(enumerator.Current.Value);
                        scene.Objects.Remove(enumerator.Current.Key);
                        scene.ParentByObject.Remove(enumerator.Current.Key);
                    }

                    remaining--;
                }

                if (scene.Objects.Count == 0)
                {
                    if (scene.Container != null)
                        UnityEngine.Object.DestroyImmediate(scene.Container);
                    scene.Container = null;
                    RetiredScenes.Dequeue();
                }
            }
        }

        private static void ApplyMessages(
            SceneState scene,
            IReadOnlyList<BridgeInboundMessage> messages)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                BridgeInboundMessage message = messages[i];
                if (message.Type == BridgePacketType.Hello)
                {
                    ApplyHello(scene, (HelloPayload)message.Payload);
                    continue;
                }

                try
                {
                    if (message.Type == BridgePacketType.Hierarchy)
                        ApplyHierarchy(scene, (HierarchyPayload)message.Payload);
                    else if (message.Type == BridgePacketType.ObjectDeleted)
                        ApplyDeletion(scene, (ObjectDeletedPayload)message.Payload);
                }
                catch (Exception exception)
                {
                    // A single bad hierarchy/deletion must not prevent the
                    // transform phase below from running.
                    BridgeServer.ReportApplicationError(
                        $"Could not apply {message.Type} packet: {exception.Message}");
                }
            }

            for (int i = 0; i < messages.Count; i++)
            {
                BridgeInboundMessage message = messages[i];
                if (message.Type != BridgePacketType.Transform)
                    continue;

                var payload = (TransformPayload)message.Payload;
                try
                {
                    ApplyTransform(scene, payload);
                }
                catch (Exception exception)
                {
                    // One malformed transform must not discard the rest of the
                    // batch (which is what made live updates appear frozen).
                    BridgeServer.ReportApplicationError(
                        $"Could not apply transform for object {payload.ObjectId}: {exception.Message}");
                }
            }

            for (int i = 0; i < messages.Count; i++)
            {
                BridgeInboundMessage message = messages[i];
                if (message.Type == BridgePacketType.Materials)
                    ApplyMaterials(scene, (MaterialsPayload)message.Payload);
                else if (message.Type == BridgePacketType.Visibility)
                    ApplyVisibility(scene, (VisibilityPayload)message.Payload);
                else if (message.Type == BridgePacketType.MeshFull)
                    ApplyMeshPlaceholder(scene, (MeshPayload)message.Payload);
            }
        }

        public static void ReplaceSnapshot(IReadOnlyList<BridgeInboundMessage> snapshotMessages)
        {
            EnsureMainThread();
            if (snapshotMessages == null)
                throw new ArgumentNullException(nameof(snapshotMessages));

            BeginSnapshotReplacement();
            try
            {
                ApplySnapshotMessages(snapshotMessages);
                CommitSnapshotReplacement();
            }
            catch
            {
                AbortSnapshotReplacement();
                throw;
            }
        }

        internal static void UpdateConnectionState(bool connectionIsActive)
        {
            EnsureMainThread();
            if (connectionWasActive && !connectionIsActive)
                Clear();

            connectionWasActive = connectionIsActive;
        }

        public static void Clear()
        {
            EnsureMainThread();
            ClearScene(activeScene);
            if (stagingScene != null)
            {
                ClearScene(stagingScene);
                stagingScene = null;
            }

            while (RetiredScenes.Count > 0)
                ClearScene(RetiredScenes.Dequeue());
            LiveMaterialResolver.Clear();
        }

        private static void ClearScene(SceneState scene)
        {
            foreach (LiveObject liveObject in scene.Objects.Values)
                DestroyLiveObject(liveObject);

            scene.Objects.Clear();
            scene.ParentByObject.Clear();
            if (scene.Container != null)
                UnityEngine.Object.DestroyImmediate(scene.Container);
            scene.Container = null;
        }

        private static void DestroyLiveObject(LiveObject liveObject)
        {
            if (liveObject.GameObject != null && liveObject.HiddenByBridge)
                SceneVisibilityManager.instance.Show(liveObject.GameObject, false);
            if (liveObject.Mesh != null)
                UnityEngine.Object.DestroyImmediate(liveObject.Mesh);
            if (liveObject.GameObject != null)
                UnityEngine.Object.DestroyImmediate(liveObject.GameObject);
        }

        private static void ApplyHierarchy(SceneState scene, HierarchyPayload payload)
        {
            if (!IsValidObjectId(payload.ObjectId, "hierarchy"))
                return;

            if (payload.ObjectId == payload.ParentId ||
                WouldCreateParentCycle(scene, payload.ObjectId, payload.ParentId))
            {
                BridgeServer.ReportApplicationError(
                    $"Rejected hierarchy for object {payload.ObjectId}: its parent would create a cycle.");
                BridgeServer.RequestResyncForApplicationError("hierarchy cycle");
                return;
            }

            GameObject gameObject = GetOrCreateObject(scene, payload.ObjectId, payload.Name);
            GameObject parentObject = payload.ParentId == Guid.Empty
                ? EnsureContainer(scene)
                : GetOrCreateAncestor(scene, payload.ParentId);

            Transform transform = gameObject.transform;
            transform.SetParent(parentObject.transform, false);
            int siblingIndex = (int)Math.Min(payload.SiblingIndex, (uint)(parentObject.transform.childCount - 1));
            transform.SetSiblingIndex(siblingIndex);
            scene.ParentByObject[payload.ObjectId] = payload.ParentId;
            SetName(scene, payload.ObjectId, payload.Name);
        }

        private static void ApplyTransform(SceneState scene, TransformPayload payload)
        {
            if (!IsValidObjectId(payload.ObjectId, "transform"))
                return;

            GameObject gameObject = GetOrCreateObject(scene, payload.ObjectId, payload.Name);
            Transform transform = gameObject.transform;
            transform.localPosition = ToUnity(payload.LocalPosition);
            transform.localRotation = new Quaternion(
                payload.LocalRotation.X,
                payload.LocalRotation.Y,
                payload.LocalRotation.Z,
                payload.LocalRotation.W);
            transform.localScale = ToUnity(payload.LocalScale);
            SetName(scene, payload.ObjectId, payload.Name);
        }

        private static void ApplyVisibility(SceneState scene, VisibilityPayload payload)
        {
            if (!IsValidObjectId(payload.ObjectId, "visibility"))
                return;

            GameObject gameObject = GetOrCreateObject(scene, payload.ObjectId, payload.Name);
            LiveObject liveObject = scene.Objects[payload.ObjectId];
            if (payload.Visible && liveObject.HiddenByBridge)
            {
                SceneVisibilityManager.instance.Show(gameObject, false);
                liveObject.HiddenByBridge = false;
            }
            else if (!payload.Visible && !liveObject.HiddenByBridge)
            {
                SceneVisibilityManager.instance.Hide(gameObject, false);
                liveObject.HiddenByBridge = true;
            }

            SetName(scene, payload.ObjectId, payload.Name);
        }

        private static void ApplyMeshPlaceholder(SceneState scene, MeshPayload payload)
        {
            if (!IsValidObjectId(payload.ObjectId, "mesh"))
                return;

            GameObject gameObject = GetOrCreateObject(scene, payload.ObjectId, payload.Name);
            LiveObject liveObject = scene.Objects[payload.ObjectId];
            SetName(scene, payload.ObjectId, payload.Name);

            if (liveObject.HasMesh && liveObject.ContentHash == payload.ContentHash)
                return;

            Mesh importedMesh = null;
            long uploadStarted = Stopwatch.GetTimestamp();
            try
            {
                importedMesh = MeshImporter.Build(payload);
                Material[] materials = MeshImporter.ResolveMaterials(payload.ObjectId, payload);
                MeshFilter filter = gameObject.GetComponent<MeshFilter>();
                if (filter == null)
                    filter = gameObject.AddComponent<MeshFilter>();
                MeshRenderer renderer = gameObject.GetComponent<MeshRenderer>();
                if (renderer == null)
                    renderer = gameObject.AddComponent<MeshRenderer>();

                Mesh previousMesh = liveObject.Mesh;
                filter.sharedMesh = importedMesh;
                renderer.sharedMaterials = materials;
                liveObject.Mesh = importedMesh;
                liveObject.MaterialIndices = GetMaterialIndices(payload);
                liveObject.ContentHash = payload.ContentHash;
                liveObject.HasMesh = true;
                if (previousMesh != null)
                    UnityEngine.Object.DestroyImmediate(previousMesh);
            }
            catch (UnityException exception)
            {
                if (importedMesh != null)
                    UnityEngine.Object.DestroyImmediate(importedMesh);
                ReportMeshApplicationFailure(payload, exception);
            }
            catch (InvalidOperationException exception)
            {
                if (importedMesh != null)
                    UnityEngine.Object.DestroyImmediate(importedMesh);
                ReportMeshApplicationFailure(payload, exception);
            }
            catch (ArgumentException exception)
            {
                if (importedMesh != null)
                    UnityEngine.Object.DestroyImmediate(importedMesh);
                ReportMeshApplicationFailure(payload, exception);
            }
            catch (OverflowException exception)
            {
                if (importedMesh != null)
                    UnityEngine.Object.DestroyImmediate(importedMesh);
                ReportMeshApplicationFailure(payload, exception);
            }
            finally
            {
                lastMeshUploadMilliseconds =
                    (Stopwatch.GetTimestamp() - uploadStarted) * 1000d / Stopwatch.Frequency;
                totalMeshUploadMilliseconds += lastMeshUploadMilliseconds;
                meshUploadCount++;
            }
        }

        private static void ApplyHello(SceneState scene, HelloPayload payload)
        {
            LiveMaterialResolver.UpdateFromHello(payload);
            foreach (KeyValuePair<Guid, LiveObject> entry in scene.Objects)
            {
                if (entry.Value.MaterialIndices == null || entry.Value.GameObject == null)
                    continue;

                MeshRenderer renderer = entry.Value.GameObject.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.sharedMaterials =
                        MeshImporter.ResolveMaterials(entry.Key, entry.Value.MaterialIndices);
            }
        }

        private static void ApplyMaterials(SceneState scene, MaterialsPayload payload)
        {
            if (!IsValidObjectId(payload.ObjectId, "materials"))
                return;

            LiveMaterialResolver.UpdateMaterials(payload);
            if (!scene.Objects.TryGetValue(payload.ObjectId, out LiveObject liveObject) ||
                liveObject.GameObject == null ||
                liveObject.MaterialIndices == null)
                return;

            MeshRenderer renderer = liveObject.GameObject.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.sharedMaterials =
                    MeshImporter.ResolveMaterials(payload.ObjectId, liveObject.MaterialIndices);
        }

        private static uint[] GetMaterialIndices(MeshPayload payload)
        {
            var materialIndices = new uint[payload.Submeshes.Length];
            for (int i = 0; i < materialIndices.Length; i++)
                materialIndices[i] = payload.Submeshes[i].MaterialIndex;
            return materialIndices;
        }

        private static void ReportMeshApplicationFailure(MeshPayload payload, Exception exception)
        {
            string name = string.IsNullOrEmpty(payload.Name) ? payload.ObjectId.ToString() : payload.Name;
            BridgeServer.ReportApplicationError(
                $"Could not apply mesh for '{name}' ({payload.ObjectId}): {exception.Message}");
            BridgeServer.RequestResyncForApplicationError("mesh upload failure");
        }

        private static void ApplyDeletion(SceneState scene, ObjectDeletedPayload payload)
        {
            if (!scene.Objects.TryGetValue(payload.ObjectId, out LiveObject deletedObject))
                return;

            Transform deletedTransform = deletedObject.GameObject.transform;
            Transform root = EnsureContainer(scene).transform;
            foreach (KeyValuePair<Guid, LiveObject> entry in scene.Objects)
            {
                if (entry.Key == payload.ObjectId || entry.Value.GameObject == null)
                    continue;

                Transform child = entry.Value.GameObject.transform;
                if (child.parent == deletedTransform)
                {
                    child.SetParent(root, true);
                    scene.ParentByObject[entry.Key] = Guid.Empty;
                }
            }

            if (deletedObject.HiddenByBridge)
                SceneVisibilityManager.instance.Show(deletedObject.GameObject, false);
            if (deletedObject.Mesh != null)
                UnityEngine.Object.DestroyImmediate(deletedObject.Mesh);
            LiveMaterialResolver.ReleaseObject(payload.ObjectId);
            scene.Objects.Remove(payload.ObjectId);
            scene.ParentByObject.Remove(payload.ObjectId);
            UnityEngine.Object.DestroyImmediate(deletedObject.GameObject);
        }

        private static GameObject GetOrCreateAncestor(SceneState scene, Guid objectId)
        {
            if (objectId == Guid.Empty)
                return EnsureContainer(scene);
            if (scene.Objects.TryGetValue(objectId, out LiveObject existing))
                return existing.GameObject;

            GameObject ancestor = GetOrCreateObject(scene, objectId, $"Blender object {objectId:N}");
            return ancestor;
        }

        private static GameObject GetOrCreateObject(SceneState scene, Guid objectId, string name)
        {
            EnsureContainer(scene);
            if (scene.Objects.TryGetValue(objectId, out LiveObject existing))
            {
                SetName(scene, objectId, name);
                return existing.GameObject;
            }

            var gameObject = new GameObject(string.IsNullOrEmpty(name)
                ? $"Blender object {objectId:N}"
                : name)
            {
                hideFlags = LiveObjectHideFlags
            };
            gameObject.transform.SetParent(scene.Container.transform, false);
            scene.Objects.Add(objectId, new LiveObject(gameObject));
            scene.ParentByObject[objectId] = Guid.Empty;
            return gameObject;
        }

        private static GameObject EnsureContainer(SceneState scene)
        {
            if (scene.Container != null)
                return scene.Container;

            scene.Container = new GameObject(scene.IsStaging
                ? ContainerName + " (staging)"
                : ContainerName)
            {
                hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor
            };
            if (scene.IsStaging)
                scene.Container.SetActive(false);
            return scene.Container;
        }

        private static void SetName(SceneState scene, Guid objectId, string name)
        {
            if (!string.IsNullOrEmpty(name) &&
                scene.Objects.TryGetValue(objectId, out LiveObject liveObject))
                liveObject.GameObject.name = name;
        }

        private static bool WouldCreateParentCycle(
            SceneState scene,
            Guid objectId,
            Guid proposedParentId)
        {
            var visited = new HashSet<Guid>();
            Guid current = proposedParentId;
            while (current != Guid.Empty)
            {
                if (current == objectId || !visited.Add(current))
                    return true;
                if (!scene.ParentByObject.TryGetValue(current, out current))
                    return false;
            }

            return false;
        }

        private static bool IsValidObjectId(Guid objectId, string packetName)
        {
            if (objectId != Guid.Empty)
                return true;

            BridgeServer.ReportApplicationError($"Rejected {packetName} packet with an empty object UUID.");
            BridgeServer.RequestResyncForApplicationError($"empty UUID in {packetName} packet");
            return false;
        }

        private static void EnsureMainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != EditorMainThreadId)
            {
                throw new InvalidOperationException(
                    "Live scene objects may only be accessed from the Unity Editor main thread.");
            }
        }

        private static Vector3 ToUnity(Vector3Value value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private sealed class LiveObject
        {
            public LiveObject(GameObject gameObject)
            {
                GameObject = gameObject;
            }

            public GameObject GameObject { get; }
            public bool HiddenByBridge { get; set; }
            public Mesh Mesh { get; set; }
            public uint ContentHash { get; set; }
            public uint[] MaterialIndices { get; set; }
            public bool HasMesh { get; set; }
        }

        private sealed class SceneState
        {
            public SceneState(bool isStaging)
            {
                IsStaging = isStaging;
                Objects = new Dictionary<Guid, LiveObject>();
                ParentByObject = new Dictionary<Guid, Guid>();
            }

            public bool IsStaging { get; set; }
            public Dictionary<Guid, LiveObject> Objects { get; }
            public Dictionary<Guid, Guid> ParentByObject { get; }
            public GameObject Container { get; set; }
        }
    }
}
