using System;
using System.Collections.Generic;
using System.IO;
using BlenderBridge;

namespace BlenderBridge.Editor
{
    public sealed class BridgeSnapshotTransaction
    {
        private readonly uint snapshotId;
        private readonly BridgeInboundMessage helloMessage;
        private readonly Dictionary<Guid, SnapshotObject> objects =
            new Dictionary<Guid, SnapshotObject>();
        private readonly List<Guid> objectOrder = new List<Guid>();
        private readonly long maximumStagingBytes;
        private long stagedBytes;
        private bool completed;

        public BridgeSnapshotTransaction(
            BridgeInboundMessage hello,
            long maximumStagingBytes = (long)BridgeSettings.DefaultMaximumPendingInboxMegabytes * 1024 * 1024)
        {
            if (hello == null)
                throw new ArgumentNullException(nameof(hello));
            if (!hello.IsSnapshot || hello.Type != BridgePacketType.Hello)
                throw new ArgumentException("A snapshot transaction must begin with a snapshot Hello.", nameof(hello));
            if (maximumStagingBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumStagingBytes));

            snapshotId = hello.SnapshotId;
            helloMessage = hello;
            this.maximumStagingBytes = maximumStagingBytes;
            var payload = (HelloPayload)hello.Payload;
            if (payload.Objects == null || payload.ObjectCount != payload.Objects.Length)
                throw new InvalidDataException("Snapshot Hello has an invalid object inventory.");

            AddEstimatedBytes(hello.Type, hello.Payload);
            AddEstimatedBytes(payload.Objects.LongLength * 64L);
            for (int i = 0; i < payload.Objects.Length; i++)
            {
                HelloObject item = payload.Objects[i];
                if (item == null || item.ObjectId == Guid.Empty || objects.ContainsKey(item.ObjectId))
                    throw new InvalidDataException("Snapshot Hello contains an invalid or duplicate object UUID.");

                objects.Add(item.ObjectId, new SnapshotObject(item.HasMesh));
                objectOrder.Add(item.ObjectId);
            }
        }

        public uint SnapshotId => snapshotId;

        public bool TryAdd(BridgeInboundMessage message, out BridgeInboundMessage[] committedMessages)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (completed)
                throw new InvalidOperationException("The snapshot transaction has already completed.");
            if (!message.IsSnapshot || message.SnapshotId != snapshotId)
                throw new InvalidDataException("Snapshot packets do not belong to the active resync transaction.");

            committedMessages = null;
            AddEstimatedBytes(message.Type, message.Payload);
            if (message.Type == BridgePacketType.ResyncComplete)
            {
                ValidateComplete((ResyncCompletePayload)message.Payload);
                completed = true;
                committedMessages = BuildCommittedMessages();
                return true;
            }

            if (message.Type == BridgePacketType.Hello)
                throw new InvalidDataException("A snapshot contains more than one Hello packet.");

            if (!(message.Payload is ObjectBridgePayload objectPayload) ||
                !objects.TryGetValue(objectPayload.ObjectId, out SnapshotObject snapshotObject))
            {
                throw new InvalidDataException(
                    $"Snapshot packet {message.Type} refers to an undeclared object.");
            }

            switch (message.Type)
            {
                case BridgePacketType.Hierarchy:
                    StoreOnce(ref snapshotObject.Hierarchy, message, "Hierarchy", objectPayload.ObjectId);
                    break;
                case BridgePacketType.Visibility:
                    StoreOnce(ref snapshotObject.Visibility, message, "Visibility", objectPayload.ObjectId);
                    break;
                case BridgePacketType.Transform:
                    StoreOnce(ref snapshotObject.Transform, message, "Transform", objectPayload.ObjectId);
                    break;
                case BridgePacketType.Materials:
                    StoreOnce(ref snapshotObject.Materials, message, "Materials", objectPayload.ObjectId);
                    break;
                case BridgePacketType.MeshFull:
                    if (!snapshotObject.HasMesh)
                    {
                        throw new InvalidDataException(
                            $"Snapshot sent MeshFull for transform-only object {objectPayload.ObjectId}.");
                    }

                    StoreOnce(ref snapshotObject.Mesh, message, "MeshFull", objectPayload.ObjectId);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Packet type {message.Type} is not valid inside a snapshot.");
            }

            return false;
        }

        private void AddEstimatedBytes(BridgePacketType type, BridgePayload payload)
        {
            AddEstimatedBytes(BridgeInbox.EstimatePayloadBytes(type, payload));
        }

        private void AddEstimatedBytes(long byteCount)
        {
            if (byteCount < 0 || byteCount > maximumStagingBytes - stagedBytes)
            {
                throw new BridgeInboxCapacityException(
                    $"The staged resync snapshot would exceed its memory limit of {maximumStagingBytes / (1024 * 1024)} MiB.");
            }

            stagedBytes += byteCount;
        }

        private void ValidateComplete(ResyncCompletePayload complete)
        {
            if (complete.ResyncId != snapshotId)
                throw new InvalidDataException("ResyncComplete does not match the active snapshot transaction.");
            if (complete.ObjectCount != objects.Count)
                throw new InvalidDataException("ResyncComplete object count does not match the declared inventory.");

            foreach (KeyValuePair<Guid, SnapshotObject> pair in objects)
            {
                SnapshotObject item = pair.Value;
                if (item.Hierarchy == null || item.Visibility == null || item.Transform == null)
                {
                    throw new InvalidDataException(
                        $"Snapshot is missing required state for object {pair.Key}.");
                }

                if (item.HasMesh != (item.Mesh != null))
                {
                    throw new InvalidDataException(
                        item.HasMesh
                            ? $"Snapshot is missing MeshFull for object {pair.Key}."
                            : $"Snapshot has unexpected MeshFull for object {pair.Key}.");
                }

                Guid parentId = ((HierarchyPayload)item.Hierarchy.Payload).ParentId;
                if (parentId != Guid.Empty && !objects.ContainsKey(parentId))
                {
                    throw new InvalidDataException(
                        $"Snapshot hierarchy for object {pair.Key} refers to undeclared parent {parentId}.");
                }
            }

            ValidateNoHierarchyCycles();
        }

        private void ValidateNoHierarchyCycles()
        {
            var visitState = new Dictionary<Guid, byte>(objects.Count);
            var path = new List<Guid>();
            foreach (Guid objectId in objectOrder)
            {
                Guid current = objectId;
                path.Clear();
                while (current != Guid.Empty &&
                       (!visitState.TryGetValue(current, out byte state) || state == 1))
                {
                    if (visitState.TryGetValue(current, out state) && state == 1)
                        throw new InvalidDataException("Snapshot hierarchy contains a parent cycle.");

                    visitState[current] = 1;
                    path.Add(current);
                    SnapshotObject item = objects[current];
                    current = ((HierarchyPayload)item.Hierarchy.Payload).ParentId;
                }

                for (int i = 0; i < path.Count; i++)
                    visitState[path[i]] = 2;
            }
        }

        private BridgeInboundMessage[] BuildCommittedMessages()
        {
            var messages = new List<BridgeInboundMessage>(
                1 + objects.Count * 5);
            messages.Add(helloMessage);

            AppendMessages(messages, item => item.Hierarchy);
            AppendMessages(messages, item => item.Transform);
            AppendMessages(messages, item => item.Visibility);
            AppendMessages(messages, item => item.Materials);
            AppendMessages(messages, item => item.Mesh);
            return messages.ToArray();
        }

        private void AppendMessages(
            List<BridgeInboundMessage> messages,
            Func<SnapshotObject, BridgeInboundMessage> select)
        {
            foreach (Guid objectId in objectOrder)
            {
                BridgeInboundMessage message = select(objects[objectId]);
                if (message != null)
                    messages.Add(message);
            }
        }

        private static void StoreOnce(
            ref BridgeInboundMessage current,
            BridgeInboundMessage message,
            string packetName,
            Guid objectId)
        {
            if (current != null)
                throw new InvalidDataException(
                    $"Snapshot contains duplicate {packetName} state for object {objectId}.");
            current = message;
        }

        private sealed class SnapshotObject
        {
            public SnapshotObject(bool hasMesh)
            {
                HasMesh = hasMesh;
            }

            public bool HasMesh { get; }
            public BridgeInboundMessage Hierarchy;
            public BridgeInboundMessage Visibility;
            public BridgeInboundMessage Transform;
            public BridgeInboundMessage Mesh;
            public BridgeInboundMessage Materials;
        }
    }
}
