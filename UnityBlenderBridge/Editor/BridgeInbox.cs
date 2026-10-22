using System;
using System.Collections.Generic;
using BlenderBridge;

namespace BlenderBridge.Editor
{
    public sealed class BridgeInboundMessage
    {
        internal BridgeInboundMessage(
            BridgePacketType type,
            uint sequence,
            BridgePayload payload,
            bool isSnapshot = false,
            uint snapshotId = 0)
        {
            Type = type;
            Sequence = sequence;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            IsSnapshot = isSnapshot;
            SnapshotId = snapshotId;
        }

        public BridgePacketType Type { get; }
        public uint Sequence { get; }
        public BridgePayload Payload { get; }
        public bool IsSnapshot { get; }
        public uint SnapshotId { get; }
    }

    public sealed class BridgeInboxCapacityException : Exception
    {
        internal BridgeInboxCapacityException(string message) : base(message)
        {
        }
    }

    public sealed class BridgeInbox
    {
        private readonly object gate = new object();
        private readonly int maximumPendingStateSlots;
        private readonly int maximumQueuedControlEvents;
        private readonly long maximumPendingBytes;
        private readonly Dictionary<StateKey, QueuedMessage> latestState =
            new Dictionary<StateKey, QueuedMessage>();
        private readonly SortedSet<QueuedMessage> stateByArrival =
            new SortedSet<QueuedMessage>(QueuedMessageArrivalComparer.Instance);
        private readonly Dictionary<Guid, HashSet<StateKey>> stateKeysByObject =
            new Dictionary<Guid, HashSet<StateKey>>();
        private readonly Queue<QueuedMessage> orderedControls = new Queue<QueuedMessage>();
        private readonly Queue<QueuedMessage> snapshotMessages = new Queue<QueuedMessage>();
        private readonly Dictionary<Guid, uint> deletionTombstones = new Dictionary<Guid, uint>();
        private readonly int maximumSnapshotMessages;
        private long nextArrivalOrder;
        private long pendingBytes;

        public BridgeInbox(
            int maximumPendingStateSlots,
            int maximumQueuedControlEvents,
            long maximumPendingBytes)
        {
            if (maximumPendingStateSlots < 1 ||
                maximumPendingStateSlots > BridgeSettings.MaximumPendingStateSlotsLimit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumPendingStateSlots),
                    $"The value must be between 1 and {BridgeSettings.MaximumPendingStateSlotsLimit}.");
            }

            if (maximumQueuedControlEvents < 1 ||
                maximumQueuedControlEvents > BridgeSettings.MaximumQueuedControlEventsLimit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumQueuedControlEvents),
                    $"The value must be between 1 and {BridgeSettings.MaximumQueuedControlEventsLimit}.");
            }

            long minimumPendingBytes = 16L * 1024 * 1024;
            long maximumAllowedBytes =
                (long)BridgeSettings.MaximumPendingInboxMegabytesLimit * 1024 * 1024;
            if (maximumPendingBytes < minimumPendingBytes || maximumPendingBytes > maximumAllowedBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumPendingBytes),
                    $"The value must be between 16 MiB and {BridgeSettings.MaximumPendingInboxMegabytesLimit} MiB.");
            }

            this.maximumPendingStateSlots = maximumPendingStateSlots;
            this.maximumQueuedControlEvents = maximumQueuedControlEvents;
            this.maximumPendingBytes = maximumPendingBytes;
            maximumSnapshotMessages = (int)Math.Min(
                int.MaxValue,
                Math.Max(4L, (long)maximumPendingStateSlots * 5 + 2));
        }

        public int PendingStateCount
        {
            get
            {
                lock (gate)
                    return latestState.Count;
            }
        }

        public int PendingControlCount
        {
            get
            {
                lock (gate)
                    return orderedControls.Count + snapshotMessages.Count;
            }
        }

        public long PendingBytes
        {
            get
            {
                lock (gate)
                    return pendingBytes;
            }
        }

        public void Enqueue(
            uint sequence,
            BridgePacketType type,
            BridgePayload payload,
            bool isSnapshot = false,
            uint snapshotId = 0)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            ValidatePayloadType(type, payload);

            lock (gate)
            {
                if (isSnapshot)
                {
                    EnqueueSnapshot(sequence, type, payload, snapshotId);
                    return;
                }

                if (IsOrderedControl(type))
                {
                    EnqueueControl(sequence, type, payload);
                    return;
                }

                if (!IsReplaceableState(type) || !(payload is ObjectBridgePayload objectPayload))
                {
                    throw new ArgumentException(
                        $"Packet type {type} is not an inbox state or ordered control.",
                        nameof(type));
                }

                if (deletionTombstones.TryGetValue(objectPayload.ObjectId, out uint deletionSequence) &&
                    !BridgeFrameCodec.IsSequenceNewer(sequence, deletionSequence))
                {
                    return;
                }

                var key = new StateKey(objectPayload.ObjectId, type);
                QueuedMessage replacement = CreateQueuedMessage(
                    sequence,
                    type,
                    payload,
                    objectPayload.ObjectId);
                if (latestState.TryGetValue(key, out QueuedMessage current))
                {
                    if (!BridgeFrameCodec.IsSequenceNewer(sequence, current.Message.Sequence))
                        return;

                    EnsureCapacityForBytes(replacement.EstimatedBytes - current.EstimatedBytes);
                    pendingBytes += replacement.EstimatedBytes - current.EstimatedBytes;
                    stateByArrival.Remove(current);
                    latestState[key] = replacement;
                    stateByArrival.Add(replacement);
                    return;
                }

                if (latestState.Count >= maximumPendingStateSlots)
                {
                    throw new BridgeInboxCapacityException(
                        $"The pending state inbox reached its limit of {maximumPendingStateSlots} object/type slots.");
                }

                EnsureCapacityForBytes(replacement.EstimatedBytes);
                pendingBytes += replacement.EstimatedBytes;
                AddState(key, replacement);
            }
        }

        public BridgeInboundMessage[] Drain(int maximumMessages)
        {
            if (maximumMessages < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumMessages), "The drain limit must be positive.");

            lock (gate)
            {
                int pendingCount =
                    latestState.Count + orderedControls.Count + snapshotMessages.Count;
                if (pendingCount == 0)
                    return Array.Empty<BridgeInboundMessage>();

                int count = Math.Min(maximumMessages, pendingCount);
                var drained = new BridgeInboundMessage[count];
                for (int i = 0; i < count; i++)
                {
                    QueuedMessage next = PeekNextByArrival();
                    drained[i] = next.Message;
                    if (next.IsSnapshot)
                    {
                        snapshotMessages.Dequeue();
                        pendingBytes -= next.EstimatedBytes;
                    }
                    else if (next.IsControl)
                    {
                        orderedControls.Dequeue();
                        pendingBytes -= next.EstimatedBytes;
                        RemoveDrainedTombstone(next);
                    }
                    else
                    {
                        RemoveState(next.Key);
                    }
                }

                return drained;
            }
        }

        private QueuedMessage PeekNextByArrival()
        {
            QueuedMessage next = stateByArrival.Count > 0 ? stateByArrival.Min : null;

            if (orderedControls.Count > 0)
            {
                QueuedMessage candidate = orderedControls.Peek();
                if (next == null || candidate.ArrivalOrder < next.ArrivalOrder)
                    next = candidate;
            }

            if (snapshotMessages.Count > 0)
            {
                QueuedMessage candidate = snapshotMessages.Peek();
                if (next == null || candidate.ArrivalOrder < next.ArrivalOrder)
                    next = candidate;
            }

            return next;
        }

        public void Clear()
        {
            lock (gate)
            {
                latestState.Clear();
                stateByArrival.Clear();
                stateKeysByObject.Clear();
                orderedControls.Clear();
                snapshotMessages.Clear();
                deletionTombstones.Clear();
                pendingBytes = 0;
            }
        }

        private void EnqueueSnapshot(
            uint sequence,
            BridgePacketType type,
            BridgePayload payload,
            uint snapshotId)
        {
            if (!IsSnapshotPacket(type))
            {
                throw new ArgumentException(
                    $"Packet type {type} cannot be part of a resync snapshot.",
                    nameof(type));
            }

            if (snapshotMessages.Count >= maximumSnapshotMessages)
            {
                throw new BridgeInboxCapacityException(
                    $"The resync snapshot inbox reached its limit of {maximumSnapshotMessages} packets.");
            }

            QueuedMessage queuedMessage = CreateQueuedMessage(
                sequence,
                type,
                payload,
                snapshotId: snapshotId,
                isSnapshot: true);
            EnsureCapacityForBytes(queuedMessage.EstimatedBytes);
            snapshotMessages.Enqueue(queuedMessage);
            pendingBytes += queuedMessage.EstimatedBytes;
        }

        private void EnqueueControl(uint sequence, BridgePacketType type, BridgePayload payload)
        {
            Guid objectId = Guid.Empty;
            if (payload is ObjectBridgePayload objectPayload)
            {
                objectId = objectPayload.ObjectId;
                if (deletionTombstones.TryGetValue(objectId, out uint deletionSequence) &&
                    !BridgeFrameCodec.IsSequenceNewer(sequence, deletionSequence))
                {
                    return;
                }
            }

            if (orderedControls.Count >= maximumQueuedControlEvents)
            {
                throw new BridgeInboxCapacityException(
                    $"The ordered control inbox reached its limit of {maximumQueuedControlEvents} events.");
            }

            if (type == BridgePacketType.ObjectDeleted)
            {
                if (deletionTombstones.TryGetValue(objectId, out uint previousDeletion) &&
                    !BridgeFrameCodec.IsSequenceNewer(sequence, previousDeletion))
                {
                    return;
                }

                deletionTombstones[objectId] = sequence;
                RemovePendingState(objectId, sequence);
            }

            QueuedMessage queuedMessage = CreateQueuedMessage(sequence, type, payload, objectId);
            EnsureCapacityForBytes(queuedMessage.EstimatedBytes);
            orderedControls.Enqueue(queuedMessage);
            pendingBytes += queuedMessage.EstimatedBytes;
        }

        private QueuedMessage CreateQueuedMessage(
            uint sequence,
            BridgePacketType type,
            BridgePayload payload,
            Guid objectId = default(Guid),
            uint snapshotId = 0,
            bool isSnapshot = false)
        {
            var message = new BridgeInboundMessage(type, sequence, payload, isSnapshot, snapshotId);
            return new QueuedMessage(
                message,
                checked(nextArrivalOrder++),
                IsOrderedControl(type),
                new StateKey(objectId, type),
                EstimatePayloadBytes(type, payload),
                isSnapshot);
        }

        private void RemovePendingState(Guid objectId, uint deletionSequence)
        {
            if (!stateKeysByObject.TryGetValue(objectId, out HashSet<StateKey> objectKeys))
                return;

            var keys = new List<StateKey>(objectKeys);
            for (int i = 0; i < keys.Count; i++)
            {
                StateKey key = keys[i];
                if (latestState.TryGetValue(key, out QueuedMessage message) &&
                    !BridgeFrameCodec.IsSequenceNewer(
                        message.Message.Sequence,
                        deletionSequence))
                {
                    RemoveState(key);
                }
            }
        }

        private void AddState(StateKey key, QueuedMessage message)
        {
            latestState.Add(key, message);
            stateByArrival.Add(message);
            if (!stateKeysByObject.TryGetValue(key.ObjectId, out HashSet<StateKey> objectKeys))
            {
                objectKeys = new HashSet<StateKey>();
                stateKeysByObject.Add(key.ObjectId, objectKeys);
            }

            objectKeys.Add(key);
        }

        private void RemoveState(StateKey key)
        {
            if (!latestState.TryGetValue(key, out QueuedMessage message))
                return;

            latestState.Remove(key);
            stateByArrival.Remove(message);
            pendingBytes -= message.EstimatedBytes;
            if (!stateKeysByObject.TryGetValue(key.ObjectId, out HashSet<StateKey> objectKeys))
                return;

            objectKeys.Remove(key);
            if (objectKeys.Count == 0)
                stateKeysByObject.Remove(key.ObjectId);
        }

        private void EnsureCapacityForBytes(long additionalBytes)
        {
            if (additionalBytes > maximumPendingBytes - pendingBytes)
            {
                throw new BridgeInboxCapacityException(
                    $"The pending inbox would exceed its memory limit of {maximumPendingBytes / (1024 * 1024)} MiB.");
            }
        }

        private static void ValidatePayloadType(BridgePacketType type, BridgePayload payload)
        {
            bool valid =
                (type == BridgePacketType.Transform && payload is TransformPayload) ||
                (type == BridgePacketType.MeshFull && payload is MeshPayload) ||
                (type == BridgePacketType.Visibility && payload is VisibilityPayload) ||
                (type == BridgePacketType.Hierarchy && payload is HierarchyPayload) ||
                (type == BridgePacketType.Materials && payload is MaterialsPayload) ||
                (type == BridgePacketType.ObjectDeleted && payload is ObjectDeletedPayload) ||
                (type == BridgePacketType.Hello && payload is HelloPayload) ||
                (type == BridgePacketType.ResyncComplete && payload is ResyncCompletePayload);
            if (!valid)
                throw new ArgumentException($"Payload type does not match bridge packet type {type}.", nameof(payload));
        }

        internal static long EstimatePayloadBytes(BridgePacketType type, BridgePayload payload)
        {
            long bytes = 128;
            if (payload is ObjectBridgePayload objectPayload)
                bytes += objectPayload.Name == null ? 0 : (long)objectPayload.Name.Length * sizeof(char);

            if (payload is TransformPayload)
                return bytes + 40;
            if (payload is VisibilityPayload)
                return bytes + 1;
            if (payload is HierarchyPayload)
                return bytes + 20;
            if (payload is ObjectDeletedPayload)
                return bytes;
            if (payload is MaterialsPayload materials)
                return bytes + (materials.MetadataJson == null ? 0 : (long)materials.MetadataJson.Length * sizeof(char));
            if (payload is ResyncCompletePayload)
                return bytes + 8;
            if (payload is HelloPayload hello)
            {
                bytes += (hello.SessionName == null ? 0 : (long)hello.SessionName.Length * sizeof(char));
                bytes += (hello.MetadataJson == null ? 0 : (long)hello.MetadataJson.Length * sizeof(char));
                bytes += hello.Objects == null ? 0 : hello.Objects.LongLength * 24;
                return bytes;
            }

            if (payload is MeshPayload mesh)
            {
                bytes += mesh.Positions == null ? 0 : mesh.Positions.LongLength * 12;
                bytes += mesh.Normals == null ? 0 : mesh.Normals.LongLength * 12;
                bytes += mesh.Tangents == null ? 0 : mesh.Tangents.LongLength * 16;
                bytes += mesh.Uvs == null ? 0 : mesh.Uvs.LongLength * 8;
                bytes += mesh.Colors == null ? 0 : mesh.Colors.LongLength * 4;
                bytes += mesh.Indices == null ? 0 : mesh.Indices.LongLength * 4;
                bytes += mesh.Submeshes == null ? 0 : mesh.Submeshes.LongLength * 12;
                return bytes;
            }

            throw new ArgumentException($"Unsupported payload type for {type}.", nameof(payload));
        }

        private void RemoveDrainedTombstone(QueuedMessage message)
        {
            if (message.Message.Type != BridgePacketType.ObjectDeleted)
                return;

            Guid objectId = ((ObjectDeletedPayload)message.Message.Payload).ObjectId;
            if (deletionTombstones.TryGetValue(objectId, out uint sequence) &&
                sequence == message.Message.Sequence)
            {
                deletionTombstones.Remove(objectId);
            }
        }

        private static bool IsReplaceableState(BridgePacketType type)
        {
            return type == BridgePacketType.Transform ||
                   type == BridgePacketType.MeshFull ||
                   type == BridgePacketType.Visibility ||
                   type == BridgePacketType.Materials;
        }

        private static bool IsOrderedControl(BridgePacketType type)
        {
            return type == BridgePacketType.ObjectDeleted ||
                   type == BridgePacketType.Hierarchy ||
                   type == BridgePacketType.Hello ||
                   type == BridgePacketType.ResyncComplete;
        }

        private static bool IsSnapshotPacket(BridgePacketType type)
        {
            return type == BridgePacketType.Hello ||
                   type == BridgePacketType.Hierarchy ||
                   type == BridgePacketType.Transform ||
                   type == BridgePacketType.Visibility ||
                   type == BridgePacketType.Materials ||
                   type == BridgePacketType.MeshFull ||
                   type == BridgePacketType.ResyncComplete;
        }

        private readonly struct StateKey : IEquatable<StateKey>
        {
            public StateKey(Guid objectId, BridgePacketType type)
            {
                ObjectId = objectId;
                Type = type;
            }

            public Guid ObjectId { get; }
            public BridgePacketType Type { get; }

            public bool Equals(StateKey other)
            {
                return ObjectId.Equals(other.ObjectId) && Type == other.Type;
            }

            public override bool Equals(object obj)
            {
                return obj is StateKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (ObjectId.GetHashCode() * 397) ^ (int)Type;
                }
            }
        }

        private sealed class QueuedMessageArrivalComparer : IComparer<QueuedMessage>
        {
            public static readonly QueuedMessageArrivalComparer Instance =
                new QueuedMessageArrivalComparer();

            private QueuedMessageArrivalComparer()
            {
            }

            public int Compare(QueuedMessage left, QueuedMessage right)
            {
                if (ReferenceEquals(left, right))
                    return 0;
                return left.ArrivalOrder.CompareTo(right.ArrivalOrder);
            }
        }

        private sealed class QueuedMessage
        {
            public QueuedMessage(
                BridgeInboundMessage message,
                long arrivalOrder,
                bool isControl,
                StateKey key,
                long estimatedBytes,
                bool isSnapshot)
            {
                Message = message;
                ArrivalOrder = arrivalOrder;
                IsControl = isControl;
                Key = key;
                EstimatedBytes = estimatedBytes;
                IsSnapshot = isSnapshot;
            }

            public BridgeInboundMessage Message { get; }
            public long ArrivalOrder { get; }
            public bool IsControl { get; }
            public StateKey Key { get; }
            public long EstimatedBytes { get; }
            public bool IsSnapshot { get; }
        }
    }
}
