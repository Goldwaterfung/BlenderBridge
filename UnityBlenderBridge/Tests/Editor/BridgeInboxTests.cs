using System;
using System.Collections.Generic;
using BlenderBridge;
using BlenderBridge.Editor;
using NUnit.Framework;

namespace BlenderBridge.Tests
{
    public sealed class BridgeInboxTests
    {
        private static readonly Guid ObjectId =
            new Guid("00112233-4455-6677-8899-aabbccddeeff");

        [Test]
        public void ReplaceableStatesCoalesceOnlyWithinTheSameObjectAndType()
        {
            var inbox = CreateInbox();
            inbox.Enqueue(1, BridgePacketType.Transform, Transform(ObjectId, 1f));
            inbox.Enqueue(2, BridgePacketType.MeshFull, new MeshPayload { ObjectId = ObjectId });
            inbox.Enqueue(3, BridgePacketType.Transform, Transform(ObjectId, 3f));

            Assert.That(inbox.PendingStateCount, Is.EqualTo(2));
            BridgeInboundMessage[] messages = inbox.Drain(10);

            Assert.That(messages.Length, Is.EqualTo(2));
            Assert.That(messages[0].Type, Is.EqualTo(BridgePacketType.MeshFull));
            Assert.That(messages[0].Sequence, Is.EqualTo(2));
            Assert.That(messages[1].Type, Is.EqualTo(BridgePacketType.Transform));
            Assert.That(messages[1].Sequence, Is.EqualTo(3));
            Assert.That(((TransformPayload)messages[1].Payload).LocalPosition.X, Is.EqualTo(3f));
        }

        [Test]
        public void OlderStateDoesNotReplaceNewerStateIncludingAcrossSequenceWrap()
        {
            var inbox = CreateInbox();
            inbox.Enqueue(uint.MaxValue, BridgePacketType.Transform, Transform(ObjectId, 1f));
            inbox.Enqueue(0, BridgePacketType.Transform, Transform(ObjectId, 2f));
            inbox.Enqueue(uint.MaxValue, BridgePacketType.Transform, Transform(ObjectId, 3f));

            BridgeInboundMessage[] messages = inbox.Drain(1);

            Assert.That(messages.Length, Is.EqualTo(1));
            Assert.That(messages[0].Sequence, Is.Zero);
            Assert.That(((TransformPayload)messages[0].Payload).LocalPosition.X, Is.EqualTo(2f));
        }

        [Test]
        public void DeletionClearsPendingStateAndRejectsOlderUpdates()
        {
            var inbox = CreateInbox();
            inbox.Enqueue(1, BridgePacketType.Transform, Transform(ObjectId, 1f));
            inbox.Enqueue(2, BridgePacketType.ObjectDeleted, new ObjectDeletedPayload { ObjectId = ObjectId });
            inbox.Enqueue(1, BridgePacketType.MeshFull, new MeshPayload { ObjectId = ObjectId });
            inbox.Enqueue(3, BridgePacketType.Transform, Transform(ObjectId, 3f));

            Assert.That(inbox.PendingStateCount, Is.EqualTo(1));
            BridgeInboundMessage[] messages = inbox.Drain(10);

            Assert.That(messages.Length, Is.EqualTo(2));
            Assert.That(messages[0].Type, Is.EqualTo(BridgePacketType.ObjectDeleted));
            Assert.That(messages[0].Sequence, Is.EqualTo(2));
            Assert.That(messages[1].Type, Is.EqualTo(BridgePacketType.Transform));
            Assert.That(messages[1].Sequence, Is.EqualTo(3));
        }

        [Test]
        public void HierarchyAndHandshakeEventsRemainOrderedControls()
        {
            var inbox = CreateInbox();
            inbox.Enqueue(0, BridgePacketType.Hello, new HelloPayload { SessionId = "session" });
            inbox.Enqueue(1, BridgePacketType.Hierarchy, new HierarchyPayload { ObjectId = ObjectId });
            inbox.Enqueue(2, BridgePacketType.ResyncComplete, new ResyncCompletePayload { ResyncId = 0 });

            BridgeInboundMessage[] firstBatch = inbox.Drain(2);
            BridgeInboundMessage[] secondBatch = inbox.Drain(2);

            Assert.That(firstBatch.Length, Is.EqualTo(2));
            Assert.That(firstBatch[0].Type, Is.EqualTo(BridgePacketType.Hello));
            Assert.That(firstBatch[1].Type, Is.EqualTo(BridgePacketType.Hierarchy));
            Assert.That(secondBatch.Length, Is.EqualTo(1));
            Assert.That(secondBatch[0].Type, Is.EqualTo(BridgePacketType.ResyncComplete));
            Assert.That(inbox.PendingBytes, Is.Zero);
        }

        [Test]
        public void SnapshotPacketsRemainOrderedAndDoNotCoalesceWithLiveState()
        {
            var inbox = CreateInbox();
            inbox.Enqueue(
                0,
                BridgePacketType.Hello,
                new HelloPayload(),
                isSnapshot: true,
                snapshotId: 7);
            inbox.Enqueue(
                1,
                BridgePacketType.Transform,
                Transform(ObjectId, 1f),
                isSnapshot: true,
                snapshotId: 7);
            inbox.Enqueue(
                2,
                BridgePacketType.Transform,
                Transform(ObjectId, 2f),
                isSnapshot: true,
                snapshotId: 7);
            inbox.Enqueue(3, BridgePacketType.Transform, Transform(ObjectId, 3f));
            inbox.Enqueue(
                4,
                BridgePacketType.ResyncComplete,
                new ResyncCompletePayload { ResyncId = 7 },
                isSnapshot: true,
                snapshotId: 7);

            BridgeInboundMessage[] messages = inbox.Drain(10);

            Assert.That(messages.Length, Is.EqualTo(5));
            Assert.That(messages[0].Type, Is.EqualTo(BridgePacketType.Hello));
            Assert.That(messages[0].IsSnapshot, Is.True);
            Assert.That(messages[0].SnapshotId, Is.EqualTo(7));
            Assert.That(((TransformPayload)messages[1].Payload).LocalPosition.X, Is.EqualTo(1f));
            Assert.That(((TransformPayload)messages[2].Payload).LocalPosition.X, Is.EqualTo(2f));
            Assert.That(messages[3].IsSnapshot, Is.False);
            Assert.That(((TransformPayload)messages[3].Payload).LocalPosition.X, Is.EqualTo(3f));
            Assert.That(messages[4].Type, Is.EqualTo(BridgePacketType.ResyncComplete));
        }

        [Test]
        public void DrainReturnsMessagesWhenOnlySnapshotQueueIsPending()
        {
            var inbox = CreateInbox();
            inbox.Enqueue(
                0,
                BridgePacketType.Hello,
                new HelloPayload(),
                isSnapshot: true,
                snapshotId: 1);

            BridgeInboundMessage[] messages = inbox.Drain(1);

            Assert.That(messages.Length, Is.EqualTo(1));
            Assert.That(messages[0].IsSnapshot, Is.True);
        }

        [Test]
        public void StateAndControlCapacityFailuresAreExplicit()
        {
            var stateLimited = new BridgeInbox(1, 4, 16L * 1024 * 1024);
            stateLimited.Enqueue(0, BridgePacketType.Transform, Transform(ObjectId, 0f));
            Assert.Throws<BridgeInboxCapacityException>(() =>
                stateLimited.Enqueue(1, BridgePacketType.Visibility,
                    new VisibilityPayload { ObjectId = ObjectId }));

            var controlLimited = new BridgeInbox(4, 1, 16L * 1024 * 1024);
            controlLimited.Enqueue(0, BridgePacketType.Hierarchy,
                new HierarchyPayload { ObjectId = ObjectId });
            Assert.Throws<BridgeInboxCapacityException>(() =>
                controlLimited.Enqueue(1, BridgePacketType.ObjectDeleted,
                    new ObjectDeletedPayload { ObjectId = ObjectId }));
        }

        [Test]
        public void EstimatedPayloadMemoryLimitIsEnforced()
        {
            var inbox = new BridgeInbox(4, 4, 16L * 1024 * 1024);
            var hello = new HelloPayload
            {
                SessionName = "large",
                MetadataJson = new string('x', 9 * 1024 * 1024)
            };

            Assert.Throws<BridgeInboxCapacityException>(() =>
                inbox.Enqueue(0, BridgePacketType.Hello, hello));
            Assert.That(inbox.PendingControlCount, Is.Zero);
            Assert.That(inbox.PendingBytes, Is.Zero);
        }

        [Test]
        public void ClearReleasesAllQueuedMessagesAndByteBudget()
        {
            var inbox = CreateInbox();
            inbox.Enqueue(0, BridgePacketType.Transform, Transform(ObjectId, 0f));
            inbox.Enqueue(1, BridgePacketType.ObjectDeleted, new ObjectDeletedPayload { ObjectId = ObjectId });

            inbox.Clear();

            Assert.That(inbox.PendingStateCount, Is.Zero);
            Assert.That(inbox.PendingControlCount, Is.Zero);
            Assert.That(inbox.PendingBytes, Is.Zero);
        }

        [Test]
        public void DrainInterleavesStateControlAndSnapshotByArrivalOrderAcrossBatches()
        {
            var inbox = CreateInbox();
            inbox.Enqueue(
                0,
                BridgePacketType.Hello,
                new HelloPayload(),
                isSnapshot: true,
                snapshotId: 7);
            inbox.Enqueue(1, BridgePacketType.Hierarchy, new HierarchyPayload { ObjectId = ObjectId });
            inbox.Enqueue(2, BridgePacketType.Transform, Transform(ObjectId, 2f));
            inbox.Enqueue(
                3,
                BridgePacketType.Transform,
                Transform(new Guid("11111111-2222-3333-4444-555555555555"), 3f),
                isSnapshot: true,
                snapshotId: 7);
            inbox.Enqueue(4, BridgePacketType.ObjectDeleted,
                new ObjectDeletedPayload { ObjectId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") });
            inbox.Enqueue(5, BridgePacketType.MeshFull,
                new MeshPayload { ObjectId = new Guid("99999999-8888-7777-6666-555555555555") });

            BridgeInboundMessage[] first = inbox.Drain(2);
            BridgeInboundMessage[] second = inbox.Drain(2);
            BridgeInboundMessage[] third = inbox.Drain(2);

            Assert.That(Sequences(first), Is.EqualTo(new uint[] { 0, 1 }));
            Assert.That(Sequences(second), Is.EqualTo(new uint[] { 2, 3 }));
            Assert.That(Sequences(third), Is.EqualTo(new uint[] { 4, 5 }));
            Assert.That(first[0].IsSnapshot, Is.True);
            Assert.That(second[1].IsSnapshot, Is.True);
            Assert.That(inbox.PendingStateCount, Is.Zero);
            Assert.That(inbox.PendingControlCount, Is.Zero);
            Assert.That(inbox.PendingBytes, Is.Zero);
        }

        [Test]
        public void ReplacedStateDrainsAtItsNewArrivalPosition()
        {
            var inbox = CreateInbox();
            var otherId = new Guid("11111111-2222-3333-4444-555555555555");
            inbox.Enqueue(0, BridgePacketType.Transform, Transform(ObjectId, 0f));
            inbox.Enqueue(1, BridgePacketType.Transform, Transform(otherId, 1f));
            inbox.Enqueue(2, BridgePacketType.Transform, Transform(ObjectId, 2f));

            BridgeInboundMessage[] first = inbox.Drain(1);
            BridgeInboundMessage[] second = inbox.Drain(1);

            Assert.That(first[0].Sequence, Is.EqualTo(1));
            Assert.That(((TransformPayload)first[0].Payload).ObjectId, Is.EqualTo(otherId));
            Assert.That(second[0].Sequence, Is.EqualTo(2));
            Assert.That(((TransformPayload)second[0].Payload).LocalPosition.X, Is.EqualTo(2f));
        }

        [Test]
        public void LargeStateBacklogDrainsOldestFirstWithoutLosingMessages()
        {
            const int objectCount = 5000;
            var inbox = new BridgeInbox(objectCount, 16, 64L * 1024 * 1024);
            for (int i = 0; i < objectCount; i++)
            {
                inbox.Enqueue(
                    (uint)i,
                    BridgePacketType.Transform,
                    Transform(ObjectIdAt(i), i));
            }

            var drained = new List<BridgeInboundMessage>(objectCount);
            while (true)
            {
                BridgeInboundMessage[] batch = inbox.Drain(8);
                if (batch.Length == 0)
                    break;
                drained.AddRange(batch);
                Assert.That(batch.Length, Is.LessThanOrEqualTo(8));
            }

            Assert.That(drained.Count, Is.EqualTo(objectCount));
            for (int i = 0; i < objectCount; i++)
            {
                Assert.That(
                    ((TransformPayload)drained[i].Payload).ObjectId,
                    Is.EqualTo(ObjectIdAt(i)));
                Assert.That(drained[i].Sequence, Is.EqualTo((uint)i));
            }

            Assert.That(inbox.PendingStateCount, Is.Zero);
            Assert.That(inbox.PendingBytes, Is.Zero);
        }

        private static uint[] Sequences(IReadOnlyList<BridgeInboundMessage> messages)
        {
            var sequences = new uint[messages.Count];
            for (int i = 0; i < messages.Count; i++)
                sequences[i] = messages[i].Sequence;
            return sequences;
        }

        private static Guid ObjectIdAt(int index)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(index).CopyTo(bytes, 0);
            return new Guid(bytes);
        }

        private static BridgeInbox CreateInbox()
        {
            return new BridgeInbox(16, 16, 16L * 1024 * 1024);
        }

        private static TransformPayload Transform(Guid objectId, float x)
        {
            return new TransformPayload
            {
                ObjectId = objectId,
                LocalPosition = new Vector3Value(x, 0, 0)
            };
        }
    }
}
