using System;
using System.IO;
using BlenderBridge;
using BlenderBridge.Editor;
using NUnit.Framework;

namespace BlenderBridge.Tests
{
    public sealed class BridgeSnapshotTransactionTests
    {
        private static readonly Guid ObjectId =
            new Guid("10000000-0000-0000-0000-000000000001");

        [Test]
        public void CommitsOnlyAfterAllDeclaredObjectStateArrives()
        {
            BridgeSnapshotTransaction transaction = CreateMeshSnapshot();

            Assert.That(transaction.TryAdd(Hierarchy(), out _), Is.False);
            Assert.That(transaction.TryAdd(Visibility(), out _), Is.False);
            Assert.That(transaction.TryAdd(Transform(), out _), Is.False);
            Assert.That(transaction.TryAdd(Mesh(), out _), Is.False);
            Assert.That(transaction.TryAdd(Complete(1), out BridgeInboundMessage[] committed), Is.True);

            Assert.That(committed.Length, Is.EqualTo(5));
            Assert.That(committed[0].Type, Is.EqualTo(BridgePacketType.Hello));
            Assert.That(committed[1].Type, Is.EqualTo(BridgePacketType.Hierarchy));
            Assert.That(committed[2].Type, Is.EqualTo(BridgePacketType.Transform));
            Assert.That(committed[3].Type, Is.EqualTo(BridgePacketType.Visibility));
            Assert.That(committed[4].Type, Is.EqualTo(BridgePacketType.MeshFull));
        }

        [Test]
        public void RejectsMissingRequiredPacketAtCompletion()
        {
            BridgeSnapshotTransaction transaction = CreateMeshSnapshot();
            transaction.TryAdd(Hierarchy(), out _);
            transaction.TryAdd(Visibility(), out _);
            transaction.TryAdd(Transform(), out _);

            Assert.Throws<InvalidDataException>(() =>
                transaction.TryAdd(Complete(1), out _));
        }

        [Test]
        public void CommitsOptionalMaterialStateBeforeMesh()
        {
            BridgeSnapshotTransaction transaction = CreateMeshSnapshot();
            transaction.TryAdd(Hierarchy(), out _);
            transaction.TryAdd(Visibility(), out _);
            transaction.TryAdd(Transform(), out _);
            transaction.TryAdd(SnapshotMessage(
                BridgePacketType.Materials,
                4,
                new MaterialsPayload { ObjectId = ObjectId, MetadataJson = "{\"materials\":[]}" }),
                out _);
            transaction.TryAdd(Mesh(), out _);

            Assert.That(transaction.TryAdd(Complete(1), out BridgeInboundMessage[] committed), Is.True);
            Assert.That(committed[4].Type, Is.EqualTo(BridgePacketType.Materials));
            Assert.That(committed[5].Type, Is.EqualTo(BridgePacketType.MeshFull));
        }

        [Test]
        public void TransformOnlyObjectsCommitWithoutMeshAndRejectUnexpectedMesh()
        {
            BridgeSnapshotTransaction transaction = CreateTransformOnlySnapshot();
            transaction.TryAdd(Hierarchy(), out _);
            transaction.TryAdd(Visibility(), out _);
            transaction.TryAdd(Transform(), out _);

            Assert.That(transaction.TryAdd(Complete(1), out BridgeInboundMessage[] committed), Is.True);
            Assert.That(committed.Length, Is.EqualTo(4));

            BridgeSnapshotTransaction unexpectedMesh = CreateTransformOnlySnapshot();
            Assert.Throws<InvalidDataException>(() =>
                unexpectedMesh.TryAdd(Mesh(), out _));
        }

        [Test]
        public void SnapshotStagingHonorsItsMemoryBudget()
        {
            BridgeInboundMessage hello = SnapshotHello(new[]
            {
                new HelloObject { ObjectId = ObjectId, HasMesh = false }
            });

            Assert.Throws<BridgeInboxCapacityException>(() =>
                new BridgeSnapshotTransaction(hello, 128));
        }

        [Test]
        public void RejectsDuplicateAndUndeclaredObjectState()
        {
            BridgeSnapshotTransaction transaction = CreateMeshSnapshot();
            transaction.TryAdd(Hierarchy(), out _);

            Assert.Throws<InvalidDataException>(() =>
                transaction.TryAdd(Hierarchy(), out _));
            Assert.Throws<InvalidDataException>(() =>
                transaction.TryAdd(SnapshotMessage(
                    BridgePacketType.Visibility,
                    2,
                    new VisibilityPayload { ObjectId = Guid.NewGuid() }),
                    out _));
        }

        [Test]
        public void RejectsMismatchedCompletionAndHierarchyCycle()
        {
            BridgeSnapshotTransaction mismatched = CreateTransformOnlySnapshot();
            mismatched.TryAdd(Hierarchy(), out _);
            mismatched.TryAdd(Visibility(), out _);
            mismatched.TryAdd(Transform(), out _);
            Assert.Throws<InvalidDataException>(() =>
                mismatched.TryAdd(Complete(1, 8), out _));

            var cycleHello = SnapshotHello(new[]
            {
                new HelloObject { ObjectId = ObjectId, HasMesh = false },
                new HelloObject { ObjectId = Guid.NewGuid(), HasMesh = false }
            });
            var cycle = new BridgeSnapshotTransaction(cycleHello);
            Guid otherId = ((HelloPayload)cycleHello.Payload).Objects[1].ObjectId;
            cycle.TryAdd(Hierarchy(ObjectId, otherId), out _);
            cycle.TryAdd(Hierarchy(otherId, ObjectId), out _);
            cycle.TryAdd(Visibility(), out _);
            cycle.TryAdd(Visibility(otherId), out _);
            cycle.TryAdd(Transform(), out _);
            cycle.TryAdd(Transform(otherId), out _);

            Assert.Throws<InvalidDataException>(() =>
                cycle.TryAdd(Complete(2), out _));
        }

        private static BridgeSnapshotTransaction CreateMeshSnapshot()
        {
            return new BridgeSnapshotTransaction(SnapshotHello(new[]
            {
                new HelloObject { ObjectId = ObjectId, HasMesh = true }
            }));
        }

        private static BridgeSnapshotTransaction CreateTransformOnlySnapshot()
        {
            return new BridgeSnapshotTransaction(SnapshotHello(new[]
            {
                new HelloObject { ObjectId = ObjectId, HasMesh = false }
            }));
        }

        private static BridgeInboundMessage SnapshotHello(HelloObject[] objects)
        {
            return SnapshotMessage(
                BridgePacketType.Hello,
                0,
                new HelloPayload { ObjectCount = (uint)objects.Length, Objects = objects });
        }

        private static BridgeInboundMessage Hierarchy(Guid objectId = default(Guid), Guid parentId = default(Guid))
        {
            return SnapshotMessage(
                BridgePacketType.Hierarchy,
                1,
                new HierarchyPayload
                {
                    ObjectId = objectId == Guid.Empty ? ObjectId : objectId,
                    ParentId = parentId
                });
        }

        private static BridgeInboundMessage Visibility(Guid objectId = default(Guid))
        {
            return SnapshotMessage(
                BridgePacketType.Visibility,
                2,
                new VisibilityPayload { ObjectId = objectId == Guid.Empty ? ObjectId : objectId });
        }

        private static BridgeInboundMessage Transform(Guid objectId = default(Guid))
        {
            return SnapshotMessage(
                BridgePacketType.Transform,
                3,
                new TransformPayload { ObjectId = objectId == Guid.Empty ? ObjectId : objectId });
        }

        private static BridgeInboundMessage Mesh()
        {
            return SnapshotMessage(
                BridgePacketType.MeshFull,
                4,
                new MeshPayload { ObjectId = ObjectId });
        }

        private static BridgeInboundMessage Complete(uint objectCount, uint resyncId = 9)
        {
            return SnapshotMessage(
                BridgePacketType.ResyncComplete,
                5,
                new ResyncCompletePayload { ResyncId = resyncId, ObjectCount = objectCount });
        }

        private static BridgeInboundMessage SnapshotMessage(
            BridgePacketType type,
            uint sequence,
            BridgePayload payload)
        {
            var inbox = new BridgeInbox(16, 16, 16L * 1024 * 1024);
            inbox.Enqueue(sequence, type, payload, isSnapshot: true, snapshotId: 9);
            return inbox.Drain(1)[0];
        }
    }
}
