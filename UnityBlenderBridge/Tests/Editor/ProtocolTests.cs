using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace BlenderBridge.Tests
{
    public sealed class ProtocolTests
    {
        private static readonly Guid KnownGuid = new Guid("00112233-4455-6677-8899-aabbccddeeff");

        [Test]
        public void TransformEncodingMatchesBlenderGoldenFrame()
        {
            byte[] payload = BridgePayloadCodec.EncodeTransform(
                KnownGuid,
                "A",
                new Vector3Value(1f, -2.5f, 3.25f),
                new Vector4Value(0f, 0f, 0.70710677f, 0.70710677f),
                new Vector3Value(1f, 2f, -1f));

            byte[] frame = BridgeFrameCodec.EncodeFrame((byte)BridgePacketType.Transform, payload, 0x12345678);

            Assert.That(
                BitConverter.ToString(frame).Replace("-", string.Empty).ToLowerInvariant(),
                Is.EqualTo("4556494c01010000785634123e00000000112233445566778899aabbccddeeff01410000803f000020c0000050400000000000000000f304353ff304353f0000803f00000040000080bf2d4f5ecf"));

            var transform = (TransformPayload)BridgePayloadCodec.Decode(BridgeFrameCodec.DecodeFrame(frame));
            Assert.That(transform.ObjectId, Is.EqualTo(KnownGuid));
            Assert.That(transform.Name, Is.EqualTo("A"));
            Assert.That(transform.LocalPosition.Y, Is.EqualTo(-2.5f));
            Assert.That(transform.LocalRotation.Z, Is.EqualTo(0.70710677f));
            Assert.That(transform.LocalScale.Z, Is.EqualTo(-1f));
        }

        [Test]
        public void PingEncodingAndFrameMatchBlenderGoldenFrame()
        {
            byte[] frame = BridgeFrameCodec.EncodeFrame(
                (byte)BridgePacketType.Ping,
                BridgePayloadCodec.EncodePing(0x12345678),
                0);

            Assert.That(
                BitConverter.ToString(frame).Replace("-", string.Empty).ToLowerInvariant(),
                Is.EqualTo("4556494c01060000000000000800000078563412d2876daf"));

            var packet = BridgeFrameCodec.DecodeFrame(frame);
            var ping = (PingPayload)BridgePayloadCodec.Decode(packet);
            Assert.That(ping.TimestampMilliseconds, Is.EqualTo(0x12345678));
        }

        [Test]
        public void Crc32MatchesIsoHdlcCheckValue()
        {
            byte[] input = System.Text.Encoding.ASCII.GetBytes("123456789");

            Assert.That(BridgeFrameCodec.ComputeCrc32(input, 0, input.Length), Is.EqualTo(0xCBF43926u));
        }

        [Test]
        public void GuidEncodingUsesCanonicalNetworkByteOrder()
        {
            byte[] bytes = BridgePayloadCodec.WriteGuid(KnownGuid);

            Assert.That(BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant(),
                Is.EqualTo("00112233445566778899aabbccddeeff"));
            Assert.That(BridgePayloadCodec.ReadGuid(bytes, 0), Is.EqualTo(KnownGuid));
        }

        [Test]
        public void CompressedFrameRoundTripsAndRejectsCorruption()
        {
            byte[] payload = BridgePayloadCodec.EncodePing(98765);
            byte[] frame = BridgeFrameCodec.EncodeFrame((byte)BridgePacketType.Ping, payload, 3, true);

            BridgePacket packet = BridgeFrameCodec.DecodeFrame(frame);
            Assert.That(packet.Flags, Is.EqualTo(BridgePacketFlags.PayloadCompressed));
            Assert.That(((PingPayload)BridgePayloadCodec.Decode(packet)).TimestampMilliseconds, Is.EqualTo(98765));

            frame[frame.Length - 1] ^= 0x80;
            Assert.Throws<InvalidDataException>(() => BridgeFrameCodec.DecodeFrame(frame));
        }

        [Test]
        public void CompressedFrameDecodesBlenderZlibGoldenVector()
        {
            byte[] frame = FromHex("4556494c010601000900000010000000785eab083311bad49ebb1e000e1a038a");

            BridgePacket packet = BridgeFrameCodec.DecodeFrame(frame);

            Assert.That(packet.Sequence, Is.EqualTo(9));
            Assert.That(((PingPayload)BridgePayloadCodec.Decode(packet)).TimestampMilliseconds, Is.EqualTo(0x12345678));
        }

        [Test]
        public void CompressedFrameRejectsTrailingBytesAfterZlibStream()
        {
            byte[] frame = BridgeFrameCodec.EncodeFrame(
                (byte)BridgePacketType.Ping,
                BridgePayloadCodec.EncodePing(123),
                2,
                true);
            Array.Resize(ref frame, frame.Length + 1);
            frame[frame.Length - 1] = 0x42;
            uint payloadLength = (uint)(frame.Length - BridgeFrameCodec.HeaderSize);
            for (int i = 0; i < sizeof(uint); i++)
                frame[12 + i] = (byte)(payloadLength >> (8 * i));

            Assert.Throws<InvalidDataException>(() => BridgeFrameCodec.DecodeFrame(frame));
        }

        [Test]
        public void ReadPacketHandlesShortReads()
        {
            byte[] frame = BridgeFrameCodec.EncodeFrame(
                (byte)BridgePacketType.Ping,
                BridgePayloadCodec.EncodePing(42),
                1);
            using (var stream = new ChunkedReadStream(frame, 2))
            {
                BridgePacket packet = BridgeFrameCodec.ReadPacket(stream);
                Assert.That(((PingPayload)BridgePayloadCodec.Decode(packet)).TimestampMilliseconds, Is.EqualTo(42));
            }
        }

        [Test]
        public void ReadPacketRejectsPeerClosingMidFrame()
        {
            byte[] frame = BridgeFrameCodec.EncodeFrame(
                (byte)BridgePacketType.Ping,
                BridgePayloadCodec.EncodePing(42),
                1);
            using (var stream = new MemoryStream(frame, 0, frame.Length - 1))
                Assert.Throws<EndOfStreamException>(() => BridgeFrameCodec.ReadPacket(stream));
        }

        [Test]
        public void UnknownPacketTypeIsSkippedAfterFrameValidation()
        {
            byte[] frame = BridgeFrameCodec.EncodeFrame(0xFE, new byte[] { 1, 2, 3 }, 4);

            BridgePacket packet = BridgeFrameCodec.DecodeFrame(frame);

            Assert.That(packet.IsKnownType, Is.False);
            Assert.That(BridgePayloadCodec.Decode(packet), Is.Null);
        }

        [Test]
        public void ObjectAndControlPayloadsRoundTrip()
        {
            byte[] visibilityBytes = BridgePayloadCodec.EncodeVisibility(KnownGuid, "Visible", true);
            var visibility = (VisibilityPayload)BridgePayloadCodec.Decode(
                new BridgePacket(BridgePacketType.Visibility, 4, BridgePacketFlags.None, 0, visibilityBytes));
            Assert.That(visibility.Visible, Is.True);
            Assert.That(visibility.Name, Is.EqualTo("Visible"));

            var parentId = new Guid("ffeeddcc-bbaa-9988-7766-554433221100");
            byte[] hierarchyBytes = BridgePayloadCodec.EncodeHierarchy(KnownGuid, "Child", parentId, 17);
            var hierarchy = (HierarchyPayload)BridgePayloadCodec.Decode(
                new BridgePacket(BridgePacketType.Hierarchy, 5, BridgePacketFlags.None, 0, hierarchyBytes));
            Assert.That(hierarchy.ParentId, Is.EqualTo(parentId));
            Assert.That(hierarchy.SiblingIndex, Is.EqualTo(17));

            byte[] deletedBytes = BridgePayloadCodec.EncodeObjectDeleted(KnownGuid, "Removed");
            var deleted = (ObjectDeletedPayload)BridgePayloadCodec.Decode(
                new BridgePacket(BridgePacketType.ObjectDeleted, 3, BridgePacketFlags.None, 0, deletedBytes));
            Assert.That(deleted.ObjectId, Is.EqualTo(KnownGuid));
            Assert.That(deleted.Name, Is.EqualTo("Removed"));

            byte[] resyncBytes = BridgePayloadCodec.EncodeSceneResync(123);
            var resync = (SceneResyncPayload)BridgePayloadCodec.Decode(
                new BridgePacket(BridgePacketType.SceneResync, 9, BridgePacketFlags.None, 0, resyncBytes));
            Assert.That(resync.ResyncId, Is.EqualTo(123));

            byte[] completeBytes = BridgePayloadCodec.EncodeResyncComplete(123, 27);
            var complete = (ResyncCompletePayload)BridgePayloadCodec.Decode(
                new BridgePacket(BridgePacketType.ResyncComplete, 10, BridgePacketFlags.None, 0, completeBytes));
            Assert.That(complete.ResyncId, Is.EqualTo(123));
            Assert.That(complete.ObjectCount, Is.EqualTo(27));

            byte[] pongBytes = BridgePayloadCodec.EncodePong(987);
            var pong = (PingPayload)BridgePayloadCodec.Decode(
                new BridgePacket(BridgePacketType.Pong, 7, BridgePacketFlags.None, 0, pongBytes));
            Assert.That(pong.TimestampMilliseconds, Is.EqualTo(987));
        }

        [Test]
        public void MaterialsPayloadRoundTripsMetadataJson()
        {
            const string metadataJson =
                "{\"materials\":[{\"index\":0,\"textures\":{\"base_color\":{\"png_base64\":\"cG5n\"}}}]}";

            byte[] payload = BridgePayloadCodec.EncodeMaterials(KnownGuid, "Mesh", metadataJson);
            var materials = (MaterialsPayload)BridgePayloadCodec.Decode(
                new BridgePacket(BridgePacketType.Materials, 11, BridgePacketFlags.None, 0, payload));

            Assert.That(materials.ObjectId, Is.EqualTo(KnownGuid));
            Assert.That(materials.Name, Is.EqualTo("Mesh"));
            Assert.That(materials.MetadataJson, Is.EqualTo(metadataJson));
        }

        [Test]
        public void HeaderRejectsReservedFlagsAndOversizedLengths()
        {
            byte[] header = BridgeFrameCodec.EncodeFrame(0xFE, Array.Empty<byte>(), 0);
            header[6] = 2;
            Assert.Throws<InvalidDataException>(() => BridgeFrameCodec.DecodeFrame(header));

            header[6] = 0;
            uint oversized = BridgeFrameCodec.MaximumPayloadBytes + 1u;
            for (int i = 0; i < sizeof(uint); i++)
                header[12 + i] = (byte)(oversized >> (8 * i));
            Assert.Throws<InvalidDataException>(() => BridgeFrameCodec.DecodeFrame(header));
        }

        [Test]
        public void SequenceComparisonHandlesWraparound()
        {
            Assert.That(BridgeFrameCodec.IsSequenceNewer(0, uint.MaxValue), Is.True);
            Assert.That(BridgeFrameCodec.IsSequenceNewer(10, 10), Is.False);
            Assert.That(BridgeFrameCodec.IsSequenceNewer(9, 10), Is.False);
        }

        [Test]
        public void HelloDecodesInventoryAndSessionMetadata()
        {
            const string metadata = "{\"session_id\":\"ffeeddcc-bbaa-9988-7766-554433221100\",\"objects\":[{\"uuid\":\"00112233-4455-6677-8899-aabbccddeeff\",\"has_mesh\":true}]}";
            byte[] helloBytes = BridgePayloadCodec.EncodeHello(5, 0, 1f, "sample.blend", 1, metadata);
            byte[] frame = BridgeFrameCodec.EncodeFrame((byte)BridgePacketType.Hello, helloBytes, 0, true);

            var hello = (HelloPayload)BridgePayloadCodec.Decode(BridgeFrameCodec.DecodeFrame(frame));

            Assert.That(hello.ProtocolVersion, Is.EqualTo(1));
            Assert.That(hello.SessionId, Is.EqualTo("ffeeddcc-bbaa-9988-7766-554433221100"));
            Assert.That(hello.Objects.Length, Is.EqualTo(1));
            Assert.That(hello.Objects[0].ObjectId, Is.EqualTo(KnownGuid));
            Assert.That(hello.Objects[0].HasMesh, Is.True);
        }

        [Test]
        public void MeshPayloadRoundTripsAllSupportedAttributes()
        {
            var source = new MeshPayload
            {
                ObjectId = KnownGuid,
                Name = "Triangle",
                Positions = new[]
                {
                    new Vector3Value(0, 0, 0),
                    new Vector3Value(1, 0, 0),
                    new Vector3Value(0, 1, 0)
                },
                Normals = new[] { new Vector3Value(0, 0, 1), new Vector3Value(0, 0, 1), new Vector3Value(0, 0, 1) },
                Tangents = new[] { new Vector4Value(1, 0, 0, 1), new Vector4Value(1, 0, 0, 1), new Vector4Value(1, 0, 0, 1) },
                Uvs = new[] { new Vector2Value(0, 0), new Vector2Value(1, 0), new Vector2Value(0, 1) },
                Colors = new[] { new Rgba32Value(255, 0, 0, 255), new Rgba32Value(0, 255, 0, 255), new Rgba32Value(0, 0, 255, 255) },
                Indices = new uint[] { 0, 1, 2 },
                Submeshes = new[] { new SubmeshPayload { IndexOffset = 0, IndexCount = 3, MaterialIndex = 2 } },
                BoundsCenter = new Vector3Value(0.5f, 0.5f, 0),
                BoundsExtents = new Vector3Value(0.5f, 0.5f, 0)
            };

            byte[] encoded = BridgePayloadCodec.EncodeMesh(source);
            byte[] frame = BridgeFrameCodec.EncodeFrame((byte)BridgePacketType.MeshFull, encoded, 7, true);
            var decoded = (MeshPayload)BridgePayloadCodec.Decode(BridgeFrameCodec.DecodeFrame(frame));

            Assert.That(decoded.Positions.Length, Is.EqualTo(3));
            Assert.That(decoded.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
            Assert.That(decoded.Submeshes[0].MaterialIndex, Is.EqualTo(2));
            Assert.That(decoded.Colors[0].R, Is.EqualTo(255));
            Assert.That(decoded.Colors[0].G, Is.EqualTo(0));
            Assert.That(decoded.ContentHash, Is.Not.EqualTo(0));
        }

        [Test]
        public void MeshPayloadEncodingMatchesBlenderGoldenVector()
        {
            var mesh = new MeshPayload
            {
                ObjectId = KnownGuid,
                Name = "Triangle",
                Positions = new[]
                {
                    new Vector3Value(0, 0, 0),
                    new Vector3Value(1, 0, 0),
                    new Vector3Value(0, 1, 0)
                },
                Normals = new[] { new Vector3Value(0, 0, 1), new Vector3Value(0, 0, 1), new Vector3Value(0, 0, 1) },
                Tangents = new[] { new Vector4Value(1, 0, 0, 1), new Vector4Value(1, 0, 0, 1), new Vector4Value(1, 0, 0, 1) },
                Uvs = new[] { new Vector2Value(0, 0), new Vector2Value(1, 0), new Vector2Value(0, 1) },
                Colors = new[] { new Rgba32Value(255, 0, 0, 255), new Rgba32Value(0, 255, 0, 255), new Rgba32Value(0, 0, 255, 255) },
                Indices = new uint[] { 0, 1, 2 },
                Submeshes = new[] { new SubmeshPayload { IndexOffset = 0, IndexCount = 3, MaterialIndex = 2 } },
                BoundsCenter = new Vector3Value(0.5f, 0.5f, 0),
                BoundsExtents = new Vector3Value(0.5f, 0.5f, 0)
            };
            const string expected = "00112233445566778899aabbccddeeff08547269616e676c6503000000030000000100000043dcd9b70f0101000000000000000000000000000000803f0000000000000000000000000000803f0000000000000000000000000000803f00000000000000000000803f00000000000000000000803f0000803f00000000000000000000803f0000803f00000000000000000000803f0000803f00000000000000000000803f00000000000000000000803f00000000000000000000803fff0000ff00ff00ff0000ffff0000000001000000020000000000000003000000020000000000003f0000003f000000000000003f0000003f00000000";

            byte[] encoded = BridgePayloadCodec.EncodeMesh(mesh);

            Assert.That(ToHex(encoded), Is.EqualTo(expected));
        }

        [Test]
        public void MeshDecoderRejectsInvalidContentHashAndTrailingPayload()
        {
            var mesh = new MeshPayload
            {
                ObjectId = KnownGuid,
                Name = "Triangle",
                Positions = new[]
                {
                    new Vector3Value(0, 0, 0),
                    new Vector3Value(1, 0, 0),
                    new Vector3Value(0, 1, 0)
                },
                Indices = new uint[] { 0, 1, 2 },
                Submeshes = new[] { new SubmeshPayload { IndexOffset = 0, IndexCount = 3, MaterialIndex = 0 } },
                BoundsCenter = new Vector3Value(0.5f, 0.5f, 0),
                BoundsExtents = new Vector3Value(0.5f, 0.5f, 0)
            };

            byte[] payload = BridgePayloadCodec.EncodeMesh(mesh);
            payload[payload.Length - 1] ^= 1;
            byte[] framed = BridgeFrameCodec.EncodeFrame((byte)BridgePacketType.MeshFull, payload, 0);
            Assert.Throws<InvalidDataException>(() => BridgePayloadCodec.Decode(BridgeFrameCodec.DecodeFrame(framed)));

            byte[] withTrailing = BridgePayloadCodec.EncodeObjectDeleted(KnownGuid, "A");
            Array.Resize(ref withTrailing, withTrailing.Length + 1);
            framed = BridgeFrameCodec.EncodeFrame((byte)BridgePacketType.ObjectDeleted, withTrailing, 0);
            Assert.Throws<InvalidDataException>(() => BridgePayloadCodec.Decode(BridgeFrameCodec.DecodeFrame(framed)));
        }

        private sealed class ChunkedReadStream : MemoryStream
        {
            private readonly int maxRead;

            public ChunkedReadStream(byte[] bytes, int maxRead) : base(bytes)
            {
                this.maxRead = maxRead;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return base.Read(buffer, offset, Math.Min(count, maxRead));
            }
        }

        private static byte[] FromHex(string value)
        {
            var bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string ToHex(byte[] value)
        {
            return BitConverter.ToString(value).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
