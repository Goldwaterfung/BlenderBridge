using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace BlenderBridge
{
    public enum BridgePacketType : byte
    {
        Transform = 0x01,
        MeshFull = 0x02,
        ObjectDeleted = 0x03,
        Visibility = 0x04,
        Hierarchy = 0x05,
        Ping = 0x06,
        Pong = 0x07,
        Hello = 0x08,
        SceneResync = 0x09,
        ResyncComplete = 0x0A,
        Materials = 0x0B,
        ProjectRootRequest = 0x0C,
        ProjectRoot = 0x0D
    }

    public struct Rgba32Value
    {
        public byte R;
        public byte G;
        public byte B;
        public byte A;

        public Rgba32Value(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }
    }

    [Flags]
    public enum BridgePacketFlags : ushort
    {
        None = 0,
        PayloadCompressed = 1
    }

    [Flags]
    public enum MeshAttributeFlags : byte
    {
        None = 0,
        Normals = 1,
        Uv0 = 2,
        Tangents = 4,
        Colors = 8
    }

    public sealed class BridgePacket
    {
        public BridgePacket(BridgePacketType type, ushort rawType, BridgePacketFlags flags, uint sequence, byte[] payload)
        {
            Type = type;
            RawType = rawType;
            Flags = flags;
            Sequence = sequence;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public BridgePacketType Type { get; }
        public ushort RawType { get; }
        public BridgePacketFlags Flags { get; }
        public uint Sequence { get; }
        public byte[] Payload { get; }
        public bool IsKnownType => RawType >= 1 && RawType <= 13;
    }

    public struct BridgeFrameHeader
    {
        public byte Version;
        public byte PacketType;
        public BridgePacketFlags Flags;
        public uint Sequence;
        public uint PayloadLength;
    }

    public struct Vector2Value
    {
        public float X;
        public float Y;

        public Vector2Value(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    public struct Vector3Value
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3Value(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    public struct Vector4Value
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public Vector4Value(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
    }

    public abstract class BridgePayload
    {
    }

    public abstract class ObjectBridgePayload : BridgePayload
    {
        public Guid ObjectId;
        public string Name;
    }

    public sealed class TransformPayload : ObjectBridgePayload
    {
        public Vector3Value LocalPosition;
        public Vector4Value LocalRotation;
        public Vector3Value LocalScale;
    }

    public sealed class ObjectDeletedPayload : ObjectBridgePayload
    {
    }

    public sealed class VisibilityPayload : ObjectBridgePayload
    {
        public bool Visible;
    }

    public sealed class HierarchyPayload : ObjectBridgePayload
    {
        public Guid ParentId;
        public uint SiblingIndex;
    }

    public sealed class PingPayload : BridgePayload
    {
        public uint TimestampMilliseconds;
    }

    public sealed class SceneResyncPayload : BridgePayload
    {
        public uint ResyncId;
    }

    public sealed class ProjectRootRequestPayload : BridgePayload
    {
    }

    public sealed class ProjectRootPayload : BridgePayload
    {
        public string ProjectRoot;
    }

    public sealed class ResyncCompletePayload : BridgePayload
    {
        public uint ResyncId;
        public uint ObjectCount;
    }

    public sealed class MaterialsPayload : ObjectBridgePayload
    {
        public string MetadataJson;
    }

    public sealed class HelloObject
    {
        public Guid ObjectId;
        public bool HasMesh;
    }

    public sealed class HelloPayload : BridgePayload
    {
        public byte ProtocolVersion;
        public byte BlenderMajorVersion;
        public byte BlenderMinorVersion;
        public float UnitScale;
        public string SessionName;
        public uint ObjectCount;
        public string MetadataJson;
        public string SessionId;
        public HelloObject[] Objects;
    }

    public sealed class SubmeshPayload
    {
        public uint IndexOffset;
        public uint IndexCount;
        public uint MaterialIndex;
    }

    public sealed class MeshPayload : ObjectBridgePayload
    {
        public uint ContentHash;
        public MeshAttributeFlags AttributeFlags;
        public Vector3Value[] Positions;
        public Vector3Value[] Normals;
        public Vector4Value[] Tangents;
        public Vector2Value[] Uvs;
        public Rgba32Value[] Colors;
        public uint[] Indices;
        public SubmeshPayload[] Submeshes;
        public Vector3Value BoundsCenter;
        public Vector3Value BoundsExtents;
    }

    public static class BridgeFrameCodec
    {
        public const uint Magic = 0x4C495645;
        public const byte ProtocolVersion = 1;
        public const int HeaderSize = 16;
        public const int CrcSize = 4;
        public const int MaximumPayloadBytes = 256 * 1024 * 1024;

        private const ushort SupportedFlags = (ushort)BridgePacketFlags.PayloadCompressed;
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static BridgeFrameHeader ParseHeader(byte[] bytes)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length != HeaderSize)
                throw new InvalidDataException("Frame header must be exactly 16 bytes.");

            uint magic = ReadUInt32(bytes, 0);
            byte version = bytes[4];
            ushort flags = ReadUInt16(bytes, 6);
            uint payloadLength = ReadUInt32(bytes, 12);

            if (magic != Magic)
                throw new InvalidDataException("Invalid bridge frame magic.");
            if (version != ProtocolVersion)
                throw new InvalidDataException($"Unsupported protocol version {version}.");
            if ((flags & ~SupportedFlags) != 0)
                throw new InvalidDataException($"Unsupported frame flags 0x{flags:X4}.");
            if (payloadLength > MaximumPayloadBytes)
                throw new InvalidDataException("Declared frame payload exceeds the protocol limit.");

            return new BridgeFrameHeader
            {
                Version = version,
                PacketType = bytes[5],
                Flags = (BridgePacketFlags)flags,
                Sequence = ReadUInt32(bytes, 8),
                PayloadLength = payloadLength
            };
        }

        public static byte[] EncodeFrame(byte packetType, byte[] payload, uint sequence, bool compress = false)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if ((long)payload.Length + CrcSize > MaximumPayloadBytes)
                throw new InvalidDataException("Uncompressed payload exceeds the protocol limit.");

            byte[] checkedPayload = new byte[payload.Length + CrcSize];
            Buffer.BlockCopy(payload, 0, checkedPayload, 0, payload.Length);
            WriteUInt32(checkedPayload, payload.Length, ComputeCrc32(payload, 0, payload.Length));

            byte[] wirePayload = compress ? CompressZlib(checkedPayload) : checkedPayload;
            if (wirePayload.Length > MaximumPayloadBytes)
                throw new InvalidDataException("Framed payload exceeds the protocol limit.");

            byte[] frame = new byte[HeaderSize + wirePayload.Length];
            WriteUInt32(frame, 0, Magic);
            frame[4] = ProtocolVersion;
            frame[5] = packetType;
            WriteUInt16(frame, 6, compress ? (ushort)BridgePacketFlags.PayloadCompressed : (ushort)0);
            WriteUInt32(frame, 8, sequence);
            WriteUInt32(frame, 12, (uint)wirePayload.Length);
            Buffer.BlockCopy(wirePayload, 0, frame, HeaderSize, wirePayload.Length);
            return frame;
        }

        public static BridgePacket DecodeFrame(byte[] frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (frame.Length < HeaderSize)
                throw new InvalidDataException("Truncated frame header.");

            var headerBytes = new byte[HeaderSize];
            Buffer.BlockCopy(frame, 0, headerBytes, 0, HeaderSize);
            BridgeFrameHeader header = ParseHeader(headerBytes);
            if ((long)frame.Length != HeaderSize + (long)header.PayloadLength)
                throw new InvalidDataException("Frame length does not match its header.");

            byte[] payload;
            if ((header.Flags & BridgePacketFlags.PayloadCompressed) != 0)
            {
                payload = DecompressBounded(frame, HeaderSize, (int)header.PayloadLength);
            }
            else
            {
                payload = new byte[(int)header.PayloadLength];
                Buffer.BlockCopy(frame, HeaderSize, payload, 0, payload.Length);
            }

            if (payload.Length < CrcSize)
                throw new InvalidDataException("Frame payload is missing its CRC32 trailer.");

            int bodyLength = payload.Length - CrcSize;
            uint receivedCrc = ReadUInt32(payload, bodyLength);
            uint expectedCrc = ComputeCrc32(payload, 0, bodyLength);
            if (receivedCrc != expectedCrc)
                throw new InvalidDataException("Frame payload CRC32 mismatch.");

            byte rawType = header.PacketType;
            BridgePacketType knownType = Enum.IsDefined(typeof(BridgePacketType), rawType)
                ? (BridgePacketType)rawType
                : 0;
            var body = new byte[bodyLength];
            Buffer.BlockCopy(payload, 0, body, 0, bodyLength);
            return new BridgePacket(knownType, rawType, header.Flags, header.Sequence, body);
        }

        public static BridgePacket ReadPacket(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            byte[] headerBytes = ReadExactly(stream, HeaderSize);
            BridgeFrameHeader header = ParseHeader(headerBytes);
            byte[] payload = ReadExactly(stream, (int)header.PayloadLength);
            var frame = new byte[HeaderSize + payload.Length];
            Buffer.BlockCopy(headerBytes, 0, frame, 0, HeaderSize);
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
            return DecodeFrame(frame);
        }

        public static byte[] ReadExactly(Stream stream, int count)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (count < 0 || count > MaximumPayloadBytes + HeaderSize)
                throw new InvalidDataException("Requested read length is outside the protocol limit.");

            byte[] result = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(result, offset, count - offset);
                if (read <= 0)
                    throw new EndOfStreamException("Bridge peer closed the connection during a frame.");
                offset += read;
            }

            return result;
        }

        public static bool IsSequenceNewer(uint candidate, uint previous)
        {
            uint difference = unchecked(candidate - previous);
            return difference != 0 && difference < 0x80000000u;
        }

        public static uint ComputeCrc32(byte[] data, int offset, int count)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));

            uint crc = 0xFFFFFFFFu;
            int end = offset + count;
            for (int i = offset; i < end; i++)
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static byte[] CompressZlib(byte[] payload)
        {
            byte[] deflated;
            using (var output = new MemoryStream())
            {
                using (var compressor = new DeflateStream(output, CompressionLevel.Fastest, true))
                    compressor.Write(payload, 0, payload.Length);
                deflated = output.ToArray();
            }

            var result = new byte[deflated.Length + 6];
            result[0] = 0x78;
            result[1] = 0x01;
            Buffer.BlockCopy(deflated, 0, result, 2, deflated.Length);
            WriteUInt32BigEndian(result, result.Length - 4, ComputeAdler32(payload));
            return result;
        }

        private static byte[] DecompressBounded(byte[] source, int offset, int count)
        {
            if (count < 6)
                throw new InvalidDataException("Compressed payload is too short to contain a zlib stream.");
            byte cmf = source[offset];
            byte flg = source[offset + 1];
            if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 ||
                (((uint)cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
                throw new InvalidDataException("Compressed payload has an invalid zlib header.");

            using (var input = new MemoryStream(source, offset + 2, count - 6, false))
            using (var decompressor = new DeflateStream(input, CompressionMode.Decompress, true))
            using (var output = new MemoryStream(Math.Min(count * 2, 1024 * 1024)))
            {
                byte[] buffer = new byte[8192];
                int total = 0;
                while (true)
                {
                    int read = decompressor.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        break;
                    if (read > MaximumPayloadBytes - total)
                        throw new InvalidDataException("Decompressed payload exceeds the protocol limit.");
                    output.Write(buffer, 0, read);
                    total += read;
                }

                byte[] result = output.ToArray();
                uint expectedAdler = ReadUInt32BigEndian(source, offset + count - 4);
                if (ComputeAdler32(result) != expectedAdler)
                    throw new InvalidDataException("Compressed payload has an invalid zlib trailer or trailing bytes.");
                return result;
            }
        }

        private static uint ComputeAdler32(byte[] data)
        {
            const uint Modulus = 65521;
            uint a = 1;
            uint b = 0;
            foreach (byte value in data)
            {
                a = (a + value) % Modulus;
                b = (b + a) % Modulus;
            }

            return (b << 16) | a;
        }

        private static uint ReadUInt32BigEndian(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24)
                | ((uint)data[offset + 1] << 16)
                | ((uint)data[offset + 2] << 8)
                | data[offset + 3];
        }

        private static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[i] = value;
            }

            return table;
        }

        internal static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        internal static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)data[offset]
                | ((uint)data[offset + 1] << 8)
                | ((uint)data[offset + 2] << 16)
                | ((uint)data[offset + 3] << 24);
        }

        internal static void WriteUInt16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
        }

        internal static void WriteUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }
    }

    public static class BridgePayloadCodec
    {
        private const int PrefixFixedSize = 17;
        private const int MeshCountsSize = 20;
        private const int SubmeshSize = 12;
        private const int MaximumObjects = 1_000_000;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static BridgePayload Decode(BridgePacket packet)
        {
            if (packet == null)
                throw new ArgumentNullException(nameof(packet));
            if (!packet.IsKnownType)
                return null;

            var reader = new PayloadReader(packet.Payload);
            BridgePayload payload;
            switch (packet.Type)
            {
                case BridgePacketType.Transform:
                    payload = DecodeTransform(reader);
                    break;
                case BridgePacketType.MeshFull:
                    payload = DecodeMesh(reader);
                    break;
                case BridgePacketType.ObjectDeleted:
                    payload = DecodeObjectDeleted(reader);
                    break;
                case BridgePacketType.Visibility:
                    payload = DecodeVisibility(reader);
                    break;
                case BridgePacketType.Hierarchy:
                    payload = DecodeHierarchy(reader);
                    break;
                case BridgePacketType.Materials:
                    payload = DecodeMaterials(reader);
                    break;
                case BridgePacketType.Ping:
                case BridgePacketType.Pong:
                    payload = new PingPayload { TimestampMilliseconds = reader.ReadUInt32() };
                    break;
                case BridgePacketType.Hello:
                    payload = DecodeHello(reader);
                    break;
                case BridgePacketType.SceneResync:
                    payload = new SceneResyncPayload { ResyncId = reader.ReadUInt32() };
                    break;
                case BridgePacketType.ResyncComplete:
                    payload = new ResyncCompletePayload
                    {
                        ResyncId = reader.ReadUInt32(),
                        ObjectCount = reader.ReadUInt32()
                    };
                    break;
                case BridgePacketType.ProjectRootRequest:
                    payload = new ProjectRootRequestPayload();
                    break;
                case BridgePacketType.ProjectRoot:
                    payload = new ProjectRootPayload
                    {
                        ProjectRoot = reader.ReadUtf8(reader.Remaining)
                    };
                    break;
                default:
                    return null;
            }

            reader.EnsureFinished();
            return payload;
        }

        public static byte[] EncodeObjectPrefix(Guid objectId, string name)
        {
            byte[] encodedName = EncodeName(name);
            var output = new byte[PrefixFixedSize + encodedName.Length];
            WriteGuid(output, 0, objectId);
            output[16] = (byte)encodedName.Length;
            Buffer.BlockCopy(encodedName, 0, output, PrefixFixedSize, encodedName.Length);
            return output;
        }

        public static byte[] EncodeTransform(Guid objectId, string name, Vector3Value position,
            Vector4Value rotationXyzw, Vector3Value scale)
        {
            using (var stream = new MemoryStream())
            {
                WriteBytes(stream, EncodeObjectPrefix(objectId, name));
                WriteVector3(stream, position);
                WriteVector4(stream, rotationXyzw);
                WriteVector3(stream, scale);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeMaterials(Guid objectId, string name, string metadataJson)
        {
            byte[] json = StrictUtf8.GetBytes(metadataJson ?? "{\"materials\":[]}");
            if ((long)PrefixFixedSize + EncodeName(name).Length + 4 + json.Length + BridgeFrameCodec.CrcSize >
                BridgeFrameCodec.MaximumPayloadBytes)
                throw new InvalidDataException("Material metadata exceeds the protocol payload limit.");

            using (var stream = new MemoryStream())
            {
                WriteBytes(stream, EncodeObjectPrefix(objectId, name));
                WriteUInt32(stream, (uint)json.Length);
                WriteBytes(stream, json);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeObjectDeleted(Guid objectId, string name)
        {
            return EncodeObjectPrefix(objectId, name);
        }

        public static byte[] EncodeVisibility(Guid objectId, string name, bool visible)
        {
            byte[] prefix = EncodeObjectPrefix(objectId, name);
            var payload = new byte[prefix.Length + 1];
            Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
            payload[prefix.Length] = visible ? (byte)1 : (byte)0;
            return payload;
        }

        public static byte[] EncodeHierarchy(Guid objectId, string name, Guid parentId, uint siblingIndex)
        {
            using (var stream = new MemoryStream())
            {
                WriteBytes(stream, EncodeObjectPrefix(objectId, name));
                WriteGuid(stream, parentId);
                WriteUInt32(stream, siblingIndex);
                return stream.ToArray();
            }
        }

        public static byte[] EncodePing(uint timestampMilliseconds)
        {
            return EncodeUInt32Payload(timestampMilliseconds);
        }

        public static byte[] EncodePong(uint timestampMilliseconds)
        {
            return EncodeUInt32Payload(timestampMilliseconds);
        }

        public static byte[] EncodeSceneResync(uint resyncId)
        {
            return EncodeUInt32Payload(resyncId);
        }

        public static byte[] EncodeProjectRoot(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new InvalidDataException("Unity project root must not be empty.");
            return StrictUtf8.GetBytes(projectRoot);
        }

        public static byte[] EncodeResyncComplete(uint resyncId, uint objectCount)
        {
            using (var stream = new MemoryStream(8))
            {
                WriteUInt32(stream, resyncId);
                WriteUInt32(stream, objectCount);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeHello(byte blenderMajorVersion, byte blenderMinorVersion, float unitScale,
            string sessionName, uint objectCount, string metadataJson)
        {
            RequireFinite(unitScale, "Hello unit scale");
            if (unitScale <= 0)
                throw new InvalidDataException("Hello unit scale must be positive.");

            string safeSessionName = sessionName ?? string.Empty;
            string safeMetadataJson = metadataJson ?? "{}";
            int nameByteCount = StrictUtf8.GetByteCount(safeSessionName);
            int metadataByteCount = StrictUtf8.GetByteCount(safeMetadataJson);
            if ((long)nameByteCount + metadataByteCount + 20 + BridgeFrameCodec.CrcSize >
                BridgeFrameCodec.MaximumPayloadBytes)
                throw new InvalidDataException("Hello metadata exceeds the protocol payload limit.");

            byte[] name = StrictUtf8.GetBytes(safeSessionName);
            byte[] metadata = StrictUtf8.GetBytes(safeMetadataJson);
            HelloJsonInventory inventory = HelloJsonReader.Parse(safeMetadataJson);
            if (inventory.Objects.Length != objectCount)
                throw new InvalidDataException("Hello object count does not match its inventory.");

            using (var stream = new MemoryStream())
            {
                stream.WriteByte(BridgeFrameCodec.ProtocolVersion);
                stream.WriteByte(blenderMajorVersion);
                stream.WriteByte(blenderMinorVersion);
                stream.WriteByte(0);
                WriteSingle(stream, unitScale);
                WriteUInt32(stream, (uint)name.Length);
                WriteBytes(stream, name);
                WriteUInt32(stream, objectCount);
                WriteUInt32(stream, (uint)metadata.Length);
                WriteBytes(stream, metadata);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeMesh(MeshPayload mesh)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            Vector3Value[] positions = mesh.Positions ?? Array.Empty<Vector3Value>();
            Vector3Value[] normals = mesh.Normals ?? Array.Empty<Vector3Value>();
            Vector4Value[] tangents = mesh.Tangents ?? Array.Empty<Vector4Value>();
            Vector2Value[] uvs = mesh.Uvs ?? Array.Empty<Vector2Value>();
            Rgba32Value[] colors = mesh.Colors ?? Array.Empty<Rgba32Value>();
            uint[] indices = mesh.Indices ?? Array.Empty<uint>();
            SubmeshPayload[] submeshes = mesh.Submeshes ?? Array.Empty<SubmeshPayload>();
            int vertexCount = positions.Length;

            if (indices.Length % 3 != 0)
                throw new InvalidDataException("Mesh index count must be a multiple of three.");
            if ((normals.Length != 0 && normals.Length != vertexCount) ||
                (tangents.Length != 0 && tangents.Length != vertexCount) ||
                (uvs.Length != 0 && uvs.Length != vertexCount) ||
                (colors.Length != 0 && colors.Length != vertexCount))
                throw new InvalidDataException("Each present mesh attribute must have one entry per vertex.");

            ValidateSubmeshes(submeshes, (uint)indices.Length);
            foreach (uint index in indices)
            {
                if (index >= vertexCount)
                    throw new InvalidDataException("Mesh contains an index outside the vertex buffer.");
            }

            MeshAttributeFlags flags = MeshAttributeFlags.None;
            if (normals.Length != 0) flags |= MeshAttributeFlags.Normals;
            if (uvs.Length != 0) flags |= MeshAttributeFlags.Uv0;
            if (tangents.Length != 0) flags |= MeshAttributeFlags.Tangents;
            if (colors.Length != 0) flags |= MeshAttributeFlags.Colors;

            int encodedNameLength = EncodeName(mesh.Name).Length;
            long maximumOutputSize = PrefixFixedSize + encodedNameLength + MeshCountsSize
                + (long)vertexCount * 12
                + (normals.Length == 0 ? 0 : (long)vertexCount * 12)
                + (tangents.Length == 0 ? 0 : (long)vertexCount * 16)
                + (uvs.Length == 0 ? 0 : (long)vertexCount * 8)
                + (colors.Length == 0 ? 0 : (long)vertexCount * 4)
                + (long)indices.Length * 4
                + (long)submeshes.Length * SubmeshSize
                + 24 + BridgeFrameCodec.CrcSize;
            if (maximumOutputSize > BridgeFrameCodec.MaximumPayloadBytes)
                throw new InvalidDataException("Mesh payload exceeds the protocol payload limit.");

            using (var stream = new MemoryStream())
            {
                WriteBytes(stream, EncodeObjectPrefix(mesh.ObjectId, mesh.Name));
                WriteUInt32(stream, (uint)vertexCount);
                WriteUInt32(stream, (uint)indices.Length);
                WriteUInt32(stream, (uint)submeshes.Length);
                long hashOffset = stream.Position;
                WriteUInt32(stream, 0);
                stream.WriteByte((byte)flags);
                stream.WriteByte(uvs.Length == 0 ? (byte)0 : (byte)1);
                stream.WriteByte(colors.Length == 0 ? (byte)0 : (byte)1);
                stream.WriteByte(0);

                long canonicalOffset = stream.Position;
                foreach (Vector3Value value in positions) WriteVector3(stream, value);
                foreach (Vector3Value value in normals) WriteVector3(stream, value);
                foreach (Vector4Value value in tangents) WriteVector4(stream, value);
                foreach (Vector2Value value in uvs) WriteVector2(stream, value);
                foreach (Rgba32Value value in colors)
                {
                    stream.WriteByte(value.R);
                    stream.WriteByte(value.G);
                    stream.WriteByte(value.B);
                    stream.WriteByte(value.A);
                }
                foreach (uint index in indices) WriteUInt32(stream, index);
                foreach (SubmeshPayload submesh in submeshes)
                {
                    WriteUInt32(stream, submesh.IndexOffset);
                    WriteUInt32(stream, submesh.IndexCount);
                    WriteUInt32(stream, submesh.MaterialIndex);
                }

                ValidateBounds(mesh.BoundsCenter, mesh.BoundsExtents);
                WriteVector3(stream, mesh.BoundsCenter);
                WriteVector3(stream, mesh.BoundsExtents);

                if (stream.Length + BridgeFrameCodec.CrcSize > BridgeFrameCodec.MaximumPayloadBytes)
                    throw new InvalidDataException("Mesh payload exceeds the protocol payload limit.");

                byte[] output = stream.ToArray();
                uint contentHash = BridgeFrameCodec.ComputeCrc32(
                    output, checked((int)canonicalOffset), output.Length - checked((int)canonicalOffset));
                BridgeFrameCodec.WriteUInt32(output, checked((int)hashOffset), contentHash);
                return output;
            }
        }

        public static Guid ReadGuid(byte[] bytes, int offset)
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || offset > bytes.Length - 16)
                throw new InvalidDataException("Truncated UUID.");

            var hex = new StringBuilder(32);
            for (int i = 0; i < 16; i++)
                hex.Append(bytes[offset + i].ToString("x2", CultureInfo.InvariantCulture));
            if (!Guid.TryParseExact(hex.ToString(), "N", out Guid result))
                throw new InvalidDataException("Invalid UUID bytes.");
            return result;
        }

        public static byte[] WriteGuid(Guid value)
        {
            var output = new byte[16];
            WriteGuid(output, 0, value);
            return output;
        }

        private static BridgePayload DecodeTransform(PayloadReader reader)
        {
            ObjectPrefix prefix = ReadObjectPrefix(reader);
            var payload = new TransformPayload
            {
                ObjectId = prefix.ObjectId,
                Name = prefix.Name,
                LocalPosition = reader.ReadVector3(),
                LocalRotation = reader.ReadVector4(),
                LocalScale = reader.ReadVector3()
            };
            return payload;
        }

        private static BridgePayload DecodeObjectDeleted(PayloadReader reader)
        {
            ObjectPrefix prefix = ReadObjectPrefix(reader);
            return new ObjectDeletedPayload { ObjectId = prefix.ObjectId, Name = prefix.Name };
        }

        private static BridgePayload DecodeVisibility(PayloadReader reader)
        {
            ObjectPrefix prefix = ReadObjectPrefix(reader);
            byte flags = reader.ReadByte();
            if ((flags & 0xFE) != 0)
                throw new InvalidDataException("Visibility packet has reserved flag bits set.");
            return new VisibilityPayload
            {
                ObjectId = prefix.ObjectId,
                Name = prefix.Name,
                Visible = (flags & 1) != 0
            };
        }

        private static BridgePayload DecodeHierarchy(PayloadReader reader)
        {
            ObjectPrefix prefix = ReadObjectPrefix(reader);
            return new HierarchyPayload
            {
                ObjectId = prefix.ObjectId,
                Name = prefix.Name,
                ParentId = reader.ReadGuid(),
                SiblingIndex = reader.ReadUInt32()
            };
        }

        private static BridgePayload DecodeMaterials(PayloadReader reader)
        {
            ObjectPrefix prefix = ReadObjectPrefix(reader);
            uint jsonLength = reader.ReadUInt32();
            if (jsonLength > reader.Remaining)
                throw new InvalidDataException("Material metadata is truncated.");
            return new MaterialsPayload
            {
                ObjectId = prefix.ObjectId,
                Name = prefix.Name,
                MetadataJson = reader.ReadUtf8(checked((int)jsonLength))
            };
        }

        private static BridgePayload DecodeHello(PayloadReader reader)
        {
            byte version = reader.ReadByte();
            byte major = reader.ReadByte();
            byte minor = reader.ReadByte();
            byte reserved = reader.ReadByte();
            if (reserved != 0)
                throw new InvalidDataException("Hello reserved field must be zero.");
            if (version != BridgeFrameCodec.ProtocolVersion)
                throw new InvalidDataException($"Unsupported Hello protocol version {version}.");

            float scale = reader.ReadSingle();
            RequireFinite(scale, "Hello unit scale");
            if (scale <= 0)
                throw new InvalidDataException("Hello unit scale must be positive.");

            uint nameLength = reader.ReadUInt32();
            string sessionName = reader.ReadUtf8(CheckedLength(nameLength, reader.Remaining));
            uint objectCount = reader.ReadUInt32();
            uint jsonLength = reader.ReadUInt32();
            if (objectCount > MaximumObjects)
                throw new InvalidDataException("Hello object count exceeds the configured limit.");
            string json = reader.ReadUtf8(CheckedLength(jsonLength, reader.Remaining));

            HelloJsonInventory inventory = HelloJsonReader.Parse(json);
            if (inventory.Objects.Length != objectCount)
                throw new InvalidDataException("Hello object count does not match its inventory.");

            var resultObjects = new HelloObject[inventory.Objects.Length];
            var uniqueIds = new HashSet<Guid>();
            for (int i = 0; i < inventory.Objects.Length; i++)
            {
                if (!Guid.TryParse(inventory.Objects[i].Uuid, out Guid objectId) ||
                    objectId == Guid.Empty || !uniqueIds.Add(objectId))
                    throw new InvalidDataException("Hello inventory contains an invalid or duplicate object UUID.");
                resultObjects[i] = new HelloObject
                {
                    ObjectId = objectId,
                    HasMesh = inventory.Objects[i].HasMesh
                };
            }

            return new HelloPayload
            {
                ProtocolVersion = version,
                BlenderMajorVersion = major,
                BlenderMinorVersion = minor,
                UnitScale = scale,
                SessionName = sessionName,
                ObjectCount = objectCount,
                MetadataJson = json,
                SessionId = inventory.SessionId ?? string.Empty,
                Objects = resultObjects
            };
        }

        private static BridgePayload DecodeMesh(PayloadReader reader)
        {
            ObjectPrefix prefix = ReadObjectPrefix(reader);
            uint vertexCountValue = reader.ReadUInt32();
            uint indexCountValue = reader.ReadUInt32();
            uint submeshCountValue = reader.ReadUInt32();
            uint contentHash = reader.ReadUInt32();
            byte rawFlags = reader.ReadByte();
            byte uvChannelCount = reader.ReadByte();
            byte colorChannelCount = reader.ReadByte();
            byte reserved = reader.ReadByte();

            if ((rawFlags & ~0x0F) != 0)
                throw new InvalidDataException("Mesh has unsupported attribute flag bits.");
            if (reserved != 0)
                throw new InvalidDataException("Mesh reserved field must be zero.");
            if (uvChannelCount > 1 || colorChannelCount > 1)
                throw new InvalidDataException("Mesh v1 supports only UV0 and one vertex-colour channel.");
            if (indexCountValue % 3 != 0)
                throw new InvalidDataException("Mesh index count must be a multiple of three.");
            if (vertexCountValue > int.MaxValue || indexCountValue > int.MaxValue ||
                submeshCountValue > int.MaxValue)
                throw new InvalidDataException("Mesh counts exceed supported array limits.");

            var flags = (MeshAttributeFlags)rawFlags;
            ValidateAttributeCount(flags, MeshAttributeFlags.Uv0, uvChannelCount, "UV");
            ValidateAttributeCount(flags, MeshAttributeFlags.Colors, colorChannelCount, "colour");
            int vertexCount = (int)vertexCountValue;
            int indexCount = (int)indexCountValue;
            int submeshCount = (int)submeshCountValue;

            long expectedDataBytes = (long)vertexCount * 12
                + ((flags & MeshAttributeFlags.Normals) != 0 ? (long)vertexCount * 12 : 0)
                + ((flags & MeshAttributeFlags.Tangents) != 0 ? (long)vertexCount * 16 : 0)
                + ((flags & MeshAttributeFlags.Uv0) != 0 ? (long)vertexCount * 8 : 0)
                + ((flags & MeshAttributeFlags.Colors) != 0 ? (long)vertexCount * 4 : 0)
                + (long)indexCount * 4
                + (long)submeshCount * SubmeshSize
                + 24;
            if (expectedDataBytes != reader.Remaining)
                throw new InvalidDataException("Mesh counts and attributes do not match the payload length.");
            if (submeshCount > MaximumObjects)
                throw new InvalidDataException("Mesh submesh count exceeds the configured limit.");

            long canonicalOffset = reader.Position;
            var mesh = new MeshPayload
            {
                ObjectId = prefix.ObjectId,
                Name = prefix.Name,
                ContentHash = contentHash,
                AttributeFlags = flags,
                Positions = ReadVector3Array(reader, vertexCount),
                Normals = (flags & MeshAttributeFlags.Normals) != 0
                    ? ReadVector3Array(reader, vertexCount) : Array.Empty<Vector3Value>(),
                Tangents = (flags & MeshAttributeFlags.Tangents) != 0
                    ? ReadVector4Array(reader, vertexCount) : Array.Empty<Vector4Value>(),
                Uvs = (flags & MeshAttributeFlags.Uv0) != 0
                    ? ReadVector2Array(reader, vertexCount) : Array.Empty<Vector2Value>(),
                Colors = (flags & MeshAttributeFlags.Colors) != 0
                    ? ReadColorArray(reader, vertexCount) : Array.Empty<Rgba32Value>(),
                Indices = ReadUInt32Array(reader, indexCount),
                Submeshes = ReadSubmeshes(reader, submeshCount),
                BoundsCenter = reader.ReadVector3(),
                BoundsExtents = reader.ReadVector3()
            };

            ValidateBounds(mesh.BoundsCenter, mesh.BoundsExtents);
            foreach (uint index in mesh.Indices)
            {
                if (index >= vertexCountValue)
                    throw new InvalidDataException("Mesh contains an index outside the vertex buffer.");
            }
            ValidateSubmeshes(mesh.Submeshes, indexCountValue);

            uint computedHash = BridgeFrameCodec.ComputeCrc32(
                reader.Data, checked((int)canonicalOffset), checked((int)expectedDataBytes));
            if (computedHash != contentHash)
                throw new InvalidDataException("Mesh content hash does not match its canonical mesh data.");
            return mesh;
        }

        private static Vector3Value[] ReadVector3Array(PayloadReader reader, int count)
        {
            var result = new Vector3Value[count];
            for (int i = 0; i < count; i++) result[i] = reader.ReadVector3();
            return result;
        }

        private static Vector4Value[] ReadVector4Array(PayloadReader reader, int count)
        {
            var result = new Vector4Value[count];
            for (int i = 0; i < count; i++) result[i] = reader.ReadVector4();
            return result;
        }

        private static Vector2Value[] ReadVector2Array(PayloadReader reader, int count)
        {
            var result = new Vector2Value[count];
            for (int i = 0; i < count; i++) result[i] = reader.ReadVector2();
            return result;
        }

        private static Rgba32Value[] ReadColorArray(PayloadReader reader, int count)
        {
            var result = new Rgba32Value[count];
            for (int i = 0; i < count; i++)
                result[i] = new Rgba32Value(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            return result;
        }

        private static uint[] ReadUInt32Array(PayloadReader reader, int count)
        {
            var result = new uint[count];
            for (int i = 0; i < count; i++) result[i] = reader.ReadUInt32();
            return result;
        }

        private static SubmeshPayload[] ReadSubmeshes(PayloadReader reader, int count)
        {
            var result = new SubmeshPayload[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = new SubmeshPayload
                {
                    IndexOffset = reader.ReadUInt32(),
                    IndexCount = reader.ReadUInt32(),
                    MaterialIndex = reader.ReadUInt32()
                };
            }

            return result;
        }

        private static void ValidateSubmeshes(SubmeshPayload[] submeshes, uint indexCount)
        {
            ulong total = 0;
            var sorted = new List<SubmeshPayload>(submeshes);
            foreach (SubmeshPayload submesh in submeshes)
            {
                ulong end = (ulong)submesh.IndexOffset + submesh.IndexCount;
                if (submesh.IndexOffset % 3 != 0 || submesh.IndexCount % 3 != 0 || end > indexCount)
                    throw new InvalidDataException("Mesh contains an invalid submesh range.");
                total += submesh.IndexCount;
            }

            if (total != indexCount)
                throw new InvalidDataException("Submesh ranges must account for every mesh index.");

            sorted.Sort((left, right) => left.IndexOffset.CompareTo(right.IndexOffset));
            uint cursor = 0;
            foreach (SubmeshPayload submesh in sorted)
            {
                if (submesh.IndexOffset != cursor)
                    throw new InvalidDataException("Submesh ranges must partition the index buffer without gaps or overlap.");
                cursor = checked(cursor + submesh.IndexCount);
            }
        }

        private static void ValidateAttributeCount(MeshAttributeFlags flags, MeshAttributeFlags flag,
            byte count, string label)
        {
            bool present = (flags & flag) != 0;
            if (present != (count == 1))
                throw new InvalidDataException($"Mesh {label} count does not match its attribute flags.");
        }

        private static void ValidateBounds(Vector3Value center, Vector3Value extents)
        {
            RequireFinite(center.X, "Bounds center");
            RequireFinite(center.Y, "Bounds center");
            RequireFinite(center.Z, "Bounds center");
            RequireFinite(extents.X, "Bounds extents");
            RequireFinite(extents.Y, "Bounds extents");
            RequireFinite(extents.Z, "Bounds extents");
            if (extents.X < 0 || extents.Y < 0 || extents.Z < 0)
                throw new InvalidDataException("Bounds extents must be non-negative.");
        }

        private static ObjectPrefix ReadObjectPrefix(PayloadReader reader)
        {
            Guid objectId = reader.ReadGuid();
            byte nameLength = reader.ReadByte();
            string name = reader.ReadUtf8(nameLength);
            return new ObjectPrefix(objectId, name);
        }

        private static byte[] EncodeUInt32Payload(uint value)
        {
            using (var stream = new MemoryStream(4))
            {
                WriteUInt32(stream, value);
                return stream.ToArray();
            }
        }

        private static byte[] EncodeName(string name)
        {
            byte[] encoded = StrictUtf8.GetBytes(name ?? string.Empty);
            if (encoded.Length <= byte.MaxValue)
                return encoded;

            int length = byte.MaxValue;
            while (length > 0)
            {
                try
                {
                    StrictUtf8.GetString(encoded, 0, length);
                    var truncated = new byte[length];
                    Buffer.BlockCopy(encoded, 0, truncated, 0, length);
                    return truncated;
                }
                catch (DecoderFallbackException)
                {
                    length--;
                }
            }

            return Array.Empty<byte>();
        }

        private static void WriteGuid(byte[] destination, int offset, Guid value)
        {
            string hex = value.ToString("N");
            for (int i = 0; i < 16; i++)
                destination[offset + i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static void WriteGuid(Stream stream, Guid value)
        {
            WriteBytes(stream, WriteGuid(value));
        }

        private static void WriteVector2(Stream stream, Vector2Value value)
        {
            WriteSingle(stream, value.X);
            WriteSingle(stream, value.Y);
        }

        private static void WriteVector3(Stream stream, Vector3Value value)
        {
            WriteSingle(stream, value.X);
            WriteSingle(stream, value.Y);
            WriteSingle(stream, value.Z);
        }

        private static void WriteVector4(Stream stream, Vector4Value value)
        {
            WriteSingle(stream, value.X);
            WriteSingle(stream, value.Y);
            WriteSingle(stream, value.Z);
            WriteSingle(stream, value.W);
        }

        private static void WriteSingle(Stream stream, float value)
        {
            RequireFinite(value, "Float value");
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            WriteBytes(stream, bytes);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            byte[] bytes = new byte[4];
            BridgeFrameCodec.WriteUInt32(bytes, 0, value);
            WriteBytes(stream, bytes);
        }

        private static void WriteBytes(Stream stream, byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        private static int CheckedLength(uint value, int remaining)
        {
            if (value > int.MaxValue || value > remaining)
                throw new InvalidDataException("Declared string length exceeds the remaining payload.");
            return (int)value;
        }

        private static void RequireFinite(float value, string label)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidDataException($"{label} contains a non-finite float32 value.");
        }

        private struct ObjectPrefix
        {
            public ObjectPrefix(Guid objectId, string name)
            {
                ObjectId = objectId;
                Name = name;
            }

            public Guid ObjectId;
            public string Name;
        }

        private sealed class HelloJsonInventory
        {
            public string SessionId;
            public HelloInventoryObject[] Objects;
        }

        private sealed class HelloInventoryObject
        {
            public string Uuid;
            public bool HasMesh;
        }

        private sealed class HelloJsonReader
        {
            private const int MaximumDepth = 64;
            private readonly string json;
            private int position;

            private HelloJsonReader(string json)
            {
                this.json = json;
            }

            public static HelloJsonInventory Parse(string json)
            {
                var parser = new HelloJsonReader(json);
                return parser.ReadRoot();
            }

            private HelloJsonInventory ReadRoot()
            {
                SkipWhitespace();
                Expect('{');
                SkipWhitespace();

                string sessionId = null;
                List<HelloInventoryObject> objects = null;
                bool sawSessionId = false;
                bool sawObjects = false;

                if (!TryConsume('}'))
                {
                    while (true)
                    {
                        string key = ReadString();
                        SkipWhitespace();
                        Expect(':');
                        SkipWhitespace();

                        if (key == "session_id")
                        {
                            if (sawSessionId)
                                Fail("Duplicate session_id field.");
                            sawSessionId = true;
                            sessionId = ReadString();
                        }
                        else if (key == "objects")
                        {
                            if (sawObjects)
                                Fail("Duplicate objects field.");
                            sawObjects = true;
                            objects = ReadObjects();
                        }
                        else
                        {
                            SkipValue(1);
                        }

                        SkipWhitespace();
                        if (TryConsume('}'))
                            break;
                        Expect(',');
                        SkipWhitespace();
                    }
                }

                SkipWhitespace();
                if (position != json.Length)
                    Fail("Trailing characters after JSON metadata.");
                if (!sawObjects)
                    Fail("Hello metadata must contain an objects inventory.");
                if (!sawSessionId || string.IsNullOrEmpty(sessionId) ||
                    !Guid.TryParse(sessionId, out Guid parsedSessionId) || parsedSessionId == Guid.Empty)
                    Fail("Hello metadata must contain a valid session UUID.");

                return new HelloJsonInventory
                {
                    SessionId = sessionId,
                    Objects = objects.ToArray()
                };
            }

            private List<HelloInventoryObject> ReadObjects()
            {
                var objects = new List<HelloInventoryObject>();
                Expect('[');
                SkipWhitespace();
                if (TryConsume(']'))
                    return objects;

                while (true)
                {
                    objects.Add(ReadInventoryObject());
                    if (objects.Count > MaximumObjects)
                        Fail("Hello object count exceeds the configured limit.");

                    SkipWhitespace();
                    if (TryConsume(']'))
                        return objects;
                    Expect(',');
                    SkipWhitespace();
                }
            }

            private HelloInventoryObject ReadInventoryObject()
            {
                Expect('{');
                SkipWhitespace();
                string uuid = null;
                bool? hasMesh = null;
                bool sawUuid = false;
                bool sawHasMesh = false;

                if (!TryConsume('}'))
                {
                    while (true)
                    {
                        string key = ReadString();
                        SkipWhitespace();
                        Expect(':');
                        SkipWhitespace();

                        if (key == "uuid")
                        {
                            if (sawUuid)
                                Fail("Duplicate object UUID field.");
                            sawUuid = true;
                            uuid = ReadString();
                        }
                        else if (key == "has_mesh")
                        {
                            if (sawHasMesh)
                                Fail("Duplicate has_mesh field.");
                            sawHasMesh = true;
                            hasMesh = ReadBoolean();
                        }
                        else
                        {
                            SkipValue(1);
                        }

                        SkipWhitespace();
                        if (TryConsume('}'))
                            break;
                        Expect(',');
                        SkipWhitespace();
                    }
                }

                if (!sawUuid || !sawHasMesh || string.IsNullOrEmpty(uuid) || !Guid.TryParse(uuid, out _))
                    Fail("Each Hello inventory entry must have a UUID and has_mesh boolean.");
                return new HelloInventoryObject { Uuid = uuid, HasMesh = hasMesh.Value };
            }

            private void SkipValue(int depth)
            {
                if (depth > MaximumDepth)
                    Fail("Hello metadata nesting exceeds the supported depth.");
                SkipWhitespace();
                if (position >= json.Length)
                    Fail("Unexpected end of Hello metadata.");

                switch (json[position])
                {
                    case '"':
                        ReadString();
                        return;
                    case '{':
                        SkipObject(depth + 1);
                        return;
                    case '[':
                        SkipArray(depth + 1);
                        return;
                    case 't':
                        ReadLiteral("true");
                        return;
                    case 'f':
                        ReadLiteral("false");
                        return;
                    case 'n':
                        ReadLiteral("null");
                        return;
                    default:
                        ReadNumber();
                        return;
                }
            }

            private void SkipObject(int depth)
            {
                Expect('{');
                SkipWhitespace();
                if (TryConsume('}'))
                    return;

                while (true)
                {
                    ReadString();
                    SkipWhitespace();
                    Expect(':');
                    SkipValue(depth);
                    SkipWhitespace();
                    if (TryConsume('}'))
                        return;
                    Expect(',');
                    SkipWhitespace();
                }
            }

            private void SkipArray(int depth)
            {
                Expect('[');
                SkipWhitespace();
                if (TryConsume(']'))
                    return;

                while (true)
                {
                    SkipValue(depth);
                    SkipWhitespace();
                    if (TryConsume(']'))
                        return;
                    Expect(',');
                    SkipWhitespace();
                }
            }

            private string ReadString()
            {
                Expect('"');
                var result = new StringBuilder();
                while (position < json.Length)
                {
                    char value = json[position++];
                    if (value == '"')
                        return result.ToString();
                    if (value < 0x20)
                        Fail("Unescaped control character in JSON string.");
                    if (value != '\\')
                    {
                        result.Append(value);
                        continue;
                    }

                    if (position >= json.Length)
                        Fail("Truncated escape sequence in JSON string.");
                    char escape = json[position++];
                    switch (escape)
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u': AppendUnicodeEscape(result); break;
                        default: Fail("Invalid JSON string escape."); break;
                    }
                }

                Fail("Unterminated JSON string.");
                return null;
            }

            private void AppendUnicodeEscape(StringBuilder result)
            {
                char first = ReadHexCodeUnit();
                if (char.IsLowSurrogate(first))
                    Fail("Invalid unpaired low surrogate in JSON string.");
                if (!char.IsHighSurrogate(first))
                {
                    result.Append(first);
                    return;
                }

                if (position + 2 > json.Length || json[position] != '\\' || json[position + 1] != 'u')
                    Fail("Invalid unpaired high surrogate in JSON string.");
                position += 2;
                char second = ReadHexCodeUnit();
                if (!char.IsLowSurrogate(second))
                    Fail("Invalid surrogate pair in JSON string.");
                result.Append(first);
                result.Append(second);
            }

            private char ReadHexCodeUnit()
            {
                if (position > json.Length - 4)
                    Fail("Truncated Unicode escape in JSON string.");
                int value = 0;
                for (int i = 0; i < 4; i++)
                {
                    char digit = json[position++];
                    int nibble = digit >= '0' && digit <= '9' ? digit - '0'
                        : digit >= 'a' && digit <= 'f' ? digit - 'a' + 10
                        : digit >= 'A' && digit <= 'F' ? digit - 'A' + 10
                        : -1;
                    if (nibble < 0)
                        Fail("Invalid hexadecimal digit in JSON Unicode escape.");
                    value = (value << 4) | nibble;
                }

                return (char)value;
            }

            private bool ReadBoolean()
            {
                if (ConsumeLiteral("true"))
                    return true;
                if (ConsumeLiteral("false"))
                    return false;
                Fail("Expected a JSON boolean.");
                return false;
            }

            private void ReadNumber()
            {
                int start = position;
                TryConsume('-');
                if (!TryConsume('0'))
                {
                    if (!IsDigit(position) || json[position] == '0')
                        Fail("Invalid JSON number.");
                    while (IsDigit(position)) position++;
                }

                if (TryConsume('.'))
                {
                    if (!IsDigit(position))
                        Fail("Invalid JSON fraction.");
                    while (IsDigit(position)) position++;
                }

                if (TryConsume('e') || TryConsume('E'))
                {
                    if (!TryConsume('+'))
                        TryConsume('-');
                    if (!IsDigit(position))
                        Fail("Invalid JSON exponent.");
                    while (IsDigit(position)) position++;
                }

                if (position == start)
                    Fail("Invalid JSON value.");
            }

            private bool IsDigit(int index)
            {
                return index < json.Length && json[index] >= '0' && json[index] <= '9';
            }

            private bool ConsumeLiteral(string literal)
            {
                if (position > json.Length - literal.Length ||
                    string.CompareOrdinal(json, position, literal, 0, literal.Length) != 0)
                    return false;
                position += literal.Length;
                return true;
            }

            private void ReadLiteral(string literal)
            {
                if (!ConsumeLiteral(literal))
                    Fail($"Invalid JSON literal; expected {literal}.");
            }

            private void SkipWhitespace()
            {
                while (position < json.Length &&
                    (json[position] == ' ' || json[position] == '\t' ||
                     json[position] == '\n' || json[position] == '\r'))
                    position++;
            }

            private bool TryConsume(char expected)
            {
                if (position >= json.Length || json[position] != expected)
                    return false;
                position++;
                return true;
            }

            private void Expect(char expected)
            {
                if (!TryConsume(expected))
                    Fail($"Expected '{expected}' in Hello metadata.");
            }

            private void Fail(string message)
            {
                throw new InvalidDataException($"{message} (character {position}).");
            }
        }

        private sealed class PayloadReader
        {
            public PayloadReader(byte[] data)
            {
                Data = data ?? throw new ArgumentNullException(nameof(data));
            }

            public byte[] Data { get; }
            public int Position { get; private set; }
            public int Remaining => Data.Length - Position;

            public byte ReadByte()
            {
                Require(1);
                return Data[Position++];
            }

            public uint ReadUInt32()
            {
                Require(4);
                uint value = BridgeFrameCodec.ReadUInt32(Data, Position);
                Position += 4;
                return value;
            }

            public float ReadSingle()
            {
                Require(4);
                byte[] bytes = new byte[4];
                Buffer.BlockCopy(Data, Position, bytes, 0, 4);
                Position += 4;
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(bytes);
                float value = BitConverter.ToSingle(bytes, 0);
                RequireFinite(value, "Payload");
                return value;
            }

            public Guid ReadGuid()
            {
                Require(16);
                Guid value = BridgePayloadCodec.ReadGuid(Data, Position);
                Position += 16;
                return value;
            }

            public string ReadUtf8(int length)
            {
                Require(length);
                try
                {
                    string value = StrictUtf8.GetString(Data, Position, length);
                    Position += length;
                    return value;
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException("Payload contains invalid UTF-8.", exception);
                }
            }

            public Vector2Value ReadVector2()
            {
                return new Vector2Value(ReadSingle(), ReadSingle());
            }

            public Vector3Value ReadVector3()
            {
                return new Vector3Value(ReadSingle(), ReadSingle(), ReadSingle());
            }

            public Vector4Value ReadVector4()
            {
                return new Vector4Value(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
            }

            public void EnsureFinished()
            {
                if (Remaining != 0)
                    throw new InvalidDataException("Known packet payload contains trailing bytes.");
            }

            private void Require(int count)
            {
                if (count < 0 || count > Remaining)
                    throw new InvalidDataException("Truncated packet payload.");
            }
        }
    }
}
