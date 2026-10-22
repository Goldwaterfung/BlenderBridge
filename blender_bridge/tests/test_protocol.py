import struct
import unittest
import uuid
import zlib

from blender_bridge.protocol import (
    HEADER,
    MAX_PAYLOAD_BYTES,
    MeshPayload,
    PacketType,
    Submesh,
    decode_frame,
    decode_object_prefix,
    encode_frame,
    encode_materials,
    encode_mesh,
    encode_transform,
    parse_header,
)


class ProtocolTests(unittest.TestCase):
    def test_frame_round_trip_and_compression(self):
        body = "bridge payload".encode()
        frame = encode_frame(PacketType.HELLO, body, 17, compress=True)
        packet = decode_frame(frame)
        self.assertEqual(packet.packet_type, PacketType.HELLO)
        self.assertEqual(packet.sequence, 17)
        self.assertEqual(packet.payload, body)

    def test_crc_corruption_is_rejected(self):
        frame = bytearray(encode_frame(PacketType.PING, b"1234", 0))
        frame[-5] ^= 0x01
        with self.assertRaisesRegex(ValueError, "CRC32"):
            decode_frame(bytes(frame))

    def test_header_rejects_oversized_frames_before_reading_payload(self):
        header = HEADER.pack(0x4C495645, 1, 1, 0, 0, MAX_PAYLOAD_BYTES + 1)
        with self.assertRaisesRegex(ValueError, "exceeds"):
            parse_header(header)

    def test_object_name_truncation_preserves_utf8(self):
        object_id = uuid.uuid4()
        payload = encode_transform(object_id, "é" * 200, (1, 2, 3), (0, 0, 0, 1), (1, 1, 1))
        decoded_id, name, offset = decode_object_prefix(payload)
        self.assertEqual(decoded_id, object_id)
        self.assertLessEqual(len(name.encode("utf-8")), 255)
        self.assertEqual(len(payload) - offset, 40)

    def test_material_payload_contains_slot_metadata_and_texture_fallback(self):
        object_id = uuid.uuid4()
        material = {
            "object_uuid": str(object_id),
            "index": 0,
            "textures": {
                "base_color": {
                    "path": "C:\\Textures\\base.png",
                    "prefer_embedded": False,
                    "hash": "abc123",
                    "png_base64": "cG5n",
                }
            },
        }

        payload = encode_materials(object_id, "Triangle", [material])
        _, _, offset = decode_object_prefix(payload)
        json_length = struct.unpack_from("<I", payload, offset)[0]
        metadata = payload[offset + 4 : offset + 4 + json_length].decode("utf-8")

        self.assertIn('"base_color"', metadata)
        self.assertIn('"hash":"abc123"', metadata)
        self.assertIn('"png_base64":"cG5n"', metadata)
        self.assertEqual(len(payload), offset + 4 + json_length)

    def test_mesh_payload_has_expected_counts_hash_and_attributes(self):
        mesh = MeshPayload(
            object_uuid=uuid.uuid4(),
            name="Triangle",
            positions=((0, 0, 0), (1, 0, 0), (0, 1, 0)),
            normals=((0, 0, 1),) * 3,
            uvs=((0, 0), (1, 0), (0, 1)),
            tangents=((1, 0, 0, 1),) * 3,
            colors=((1, 0, 0, 1),) * 3,
            indices=(0, 1, 2),
            submeshes=(Submesh(0, 3, 0),),
            bounds_center=(0.5, 0.5, 0),
            bounds_extents=(0.5, 0.5, 0),
        )
        payload = encode_mesh(mesh)
        _, _, offset = decode_object_prefix(payload)
        vertex_count, index_count, submesh_count, content_hash, flags, uv_count, color_count, reserved = (
            struct.unpack_from("<IIII4B", payload, offset)
        )
        self.assertEqual((vertex_count, index_count, submesh_count), (3, 3, 1))
        self.assertEqual(flags, 0x0F)
        self.assertEqual((uv_count, color_count, reserved), (1, 1, 0))
        self.assertNotEqual(content_hash, 0)
        self.assertEqual(content_hash, zlib.crc32(payload[offset + 20 :]) & 0xFFFFFFFF)

    def test_mesh_rejects_invalid_indices_and_partition(self):
        base = dict(
            object_uuid=uuid.uuid4(),
            name="Invalid",
            positions=((0, 0, 0), (1, 0, 0), (0, 1, 0)),
            indices=(0, 1, 3),
            submeshes=(Submesh(0, 3, 0),),
            bounds_center=(0, 0, 0),
            bounds_extents=(1, 1, 1),
        )
        with self.assertRaisesRegex(ValueError, "outside"):
            encode_mesh(MeshPayload(**base))
        base["indices"] = (0, 1, 2, 0, 1, 2)
        base["submeshes"] = (Submesh(0, 6, 0), Submesh(3, 0, 1))
        with self.assertRaisesRegex(ValueError, "partition"):
            encode_mesh(MeshPayload(**base))


if __name__ == "__main__":
    unittest.main()
