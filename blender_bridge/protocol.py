"""Version 1 wire protocol for the Blender-to-Unity live bridge."""

from __future__ import annotations

import json
import math
import socket
import struct
import uuid
import zlib
from dataclasses import dataclass
from enum import IntEnum, IntFlag
from typing import Iterable, Sequence


MAGIC = 0x4C495645
PROTOCOL_VERSION = 1
MAX_PAYLOAD_BYTES = 256 * 1024 * 1024
MAX_FLOAT32 = 3.4028234663852886e38
HEADER = struct.Struct("<IBBHII")
CRC = struct.Struct("<I")
PREFIX_FIXED = struct.Struct("<16sB")
MESH_COUNTS = struct.Struct("<IIII4B")
SUBMESH = struct.Struct("<III")


class PacketType(IntEnum):
    TRANSFORM = 0x01
    MESH_FULL = 0x02
    OBJECT_DELETED = 0x03
    VISIBILITY = 0x04
    HIERARCHY = 0x05
    PING = 0x06
    PONG = 0x07
    HELLO = 0x08
    SCENE_RESYNC = 0x09
    RESYNC_COMPLETE = 0x0A
    MATERIALS = 0x0B
    PROJECT_ROOT_REQUEST = 0x0C
    PROJECT_ROOT = 0x0D


class PacketFlags(IntFlag):
    NONE = 0
    PAYLOAD_COMPRESSED = 1


class AttributeFlags(IntFlag):
    NORMALS = 1
    UV0 = 2
    TANGENTS = 4
    COLORS = 8


@dataclass(frozen=True)
class Packet:
    packet_type: int
    sequence: int
    flags: int
    payload: bytes


@dataclass(frozen=True)
class Submesh:
    index_offset: int
    index_count: int
    material_index: int


@dataclass(frozen=True)
class MeshPayload:
    object_uuid: uuid.UUID | str
    name: str
    positions: Sequence[Sequence[float]]
    indices: Sequence[int]
    submeshes: Sequence[Submesh]
    bounds_center: Sequence[float]
    bounds_extents: Sequence[float]
    normals: Sequence[Sequence[float]] = ()
    uvs: Sequence[Sequence[float]] = ()
    tangents: Sequence[Sequence[float]] = ()
    colors: Sequence[Sequence[float]] = ()


def _uuid_bytes(value: uuid.UUID | str | bytes | None) -> bytes:
    if value is None:
        return bytes(16)
    if isinstance(value, bytes):
        if len(value) != 16:
            raise ValueError("UUID byte representation must be exactly 16 bytes.")
        return value
    return value.bytes if isinstance(value, uuid.UUID) else uuid.UUID(str(value)).bytes


def _finite_values(values: Iterable[float], label: str) -> tuple[float, ...]:
    result = tuple(float(value) for value in values)
    if not all(math.isfinite(value) and abs(value) <= MAX_FLOAT32 for value in result):
        raise ValueError(f"{label} contains a non-finite or out-of-range float32 value.")
    return result


def _name_bytes(name: str) -> bytes:
    encoded = name.encode("utf-8")
    if len(encoded) <= 255:
        return encoded
    encoded = encoded[:255]
    while encoded:
        try:
            encoded.decode("utf-8")
            return encoded
        except UnicodeDecodeError as exc:
            encoded = encoded[:exc.start]
    return b""


def encode_object_prefix(object_uuid: uuid.UUID | str, name: str) -> bytes:
    encoded_name = _name_bytes(name)
    return PREFIX_FIXED.pack(_uuid_bytes(object_uuid), len(encoded_name)) + encoded_name


def decode_object_prefix(payload: bytes, offset: int = 0) -> tuple[uuid.UUID, str, int]:
    if len(payload) - offset < PREFIX_FIXED.size:
        raise ValueError("Truncated object prefix.")
    raw_uuid, name_length = PREFIX_FIXED.unpack_from(payload, offset)
    offset += PREFIX_FIXED.size
    if len(payload) - offset < name_length:
        raise ValueError("Truncated object name.")
    name = payload[offset : offset + name_length].decode("utf-8")
    return uuid.UUID(bytes=raw_uuid), name, offset + name_length


def encode_transform(
    object_uuid: uuid.UUID | str,
    name: str,
    position: Sequence[float],
    rotation_xyzw: Sequence[float],
    scale: Sequence[float],
) -> bytes:
    values = _finite_values((*position, *rotation_xyzw, *scale), "Transform")
    if len(values) != 10:
        raise ValueError("Transform requires position[3], rotation[4], and scale[3].")
    return encode_object_prefix(object_uuid, name) + struct.pack("<10f", *values)


def encode_object_deleted(object_uuid: uuid.UUID | str, name: str) -> bytes:
    return encode_object_prefix(object_uuid, name)


def encode_materials(
    object_uuid: uuid.UUID | str,
    name: str,
    materials: Sequence[dict],
) -> bytes:
    encoded = json.dumps(
        {"materials": materials},
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
        allow_nan=False,
    ).encode("utf-8")
    payload = encode_object_prefix(object_uuid, name) + struct.pack("<I", len(encoded)) + encoded
    if len(payload) + CRC.size > MAX_PAYLOAD_BYTES:
        raise ValueError("Material payload exceeds the protocol payload limit.")
    return payload


def encode_visibility(object_uuid: uuid.UUID | str, name: str, visible: bool) -> bytes:
    return encode_object_prefix(object_uuid, name) + bytes((1 if visible else 0,))


def encode_hierarchy(
    object_uuid: uuid.UUID | str,
    name: str,
    parent_uuid: uuid.UUID | str | None,
    sibling_index: int,
) -> bytes:
    sibling_index = int(sibling_index)
    if not 0 <= sibling_index <= 0xFFFFFFFF:
        raise ValueError("Sibling index must fit in u32.")
    return (
        encode_object_prefix(object_uuid, name)
        + _uuid_bytes(parent_uuid)
        + struct.pack("<I", sibling_index)
    )


def encode_ping(timestamp_ms: int) -> bytes:
    return struct.pack("<I", int(timestamp_ms) & 0xFFFFFFFF)


def encode_resync_complete(resync_id: int, object_count: int) -> bytes:
    if not 0 <= int(resync_id) <= 0xFFFFFFFF or not 0 <= int(object_count) <= 0xFFFFFFFF:
        raise ValueError("Resync identifiers and counts must fit in u32.")
    return struct.pack("<II", int(resync_id), int(object_count))


def parse_scene_resync(payload: bytes) -> int:
    if len(payload) != 4:
        raise ValueError("SceneResync payload must contain exactly one u32 ID.")
    return struct.unpack("<I", payload)[0]


def encode_hello(
    *,
    blender_version: tuple[int, int],
    unit_scale: float,
    session_name: str,
    metadata: dict,
) -> bytes:
    major, minor = (int(blender_version[0]), int(blender_version[1]))
    if not (0 <= major <= 255 and 0 <= minor <= 255):
        raise ValueError("Blender version components must fit in u8.")
    unit_scale = float(unit_scale)
    if not math.isfinite(unit_scale) or unit_scale <= 0.0 or unit_scale > MAX_FLOAT32:
        raise ValueError("Hello unit scale must be finite and positive.")
    name = session_name.encode("utf-8")
    body = json.dumps(
        metadata,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
        allow_nan=False,
    ).encode("utf-8")
    if len(name) > MAX_PAYLOAD_BYTES or len(body) > MAX_PAYLOAD_BYTES:
        raise ValueError("Hello metadata exceeds the protocol payload limit.")
    return (
        struct.pack("<4BfI", PROTOCOL_VERSION, major, minor, 0, unit_scale, len(name))
        + name
        + struct.pack("<II", len(metadata.get("objects", ())), len(body))
        + body
    )


def _pack_float_vectors(vectors: Sequence[Sequence[float]], width: int, label: str) -> bytes:
    output = bytearray()
    for vector in vectors:
        values = _finite_values(vector, label)
        if len(values) != width:
            raise ValueError(f"{label} entries must contain exactly {width} floats.")
        output.extend(struct.pack(f"<{width}f", *values))
    return bytes(output)


def _pack_colors(colors: Sequence[Sequence[float]]) -> bytes:
    output = bytearray()
    for color in colors:
        values = _finite_values(color, "Vertex colour")
        if len(values) != 4:
            raise ValueError("Vertex colours must contain RGBA values.")
        output.extend(max(0, min(255, round(component * 255.0))) for component in values)
    return bytes(output)


def encode_mesh(mesh: MeshPayload) -> bytes:
    vertex_count = len(mesh.positions)
    index_count = len(mesh.indices)
    submesh_count = len(mesh.submeshes)
    if vertex_count > 0xFFFFFFFF or index_count > 0xFFFFFFFF or submesh_count > 0xFFFFFFFF:
        raise ValueError("Mesh counts must fit in u32.")
    if index_count % 3:
        raise ValueError("Mesh index count must be a multiple of three.")
    if mesh.normals and len(mesh.normals) != vertex_count:
        raise ValueError("Normals must have exactly one entry per vertex.")
    if mesh.tangents and len(mesh.tangents) != vertex_count:
        raise ValueError("Tangents must have exactly one entry per vertex.")
    if mesh.uvs and len(mesh.uvs) != vertex_count:
        raise ValueError("UV0 must have exactly one entry per vertex.")
    if mesh.colors and len(mesh.colors) != vertex_count:
        raise ValueError("Vertex colours must have exactly one entry per vertex.")

    indices = tuple(int(index) for index in mesh.indices)
    if any(index < 0 or index >= vertex_count for index in indices):
        raise ValueError("Mesh contains an index outside the vertex buffer.")
    submeshes = tuple(mesh.submeshes)
    for submesh in submeshes:
        end = submesh.index_offset + submesh.index_count
        if (
            submesh.index_offset < 0
            or submesh.index_count < 0
            or submesh.index_offset % 3
            or submesh.index_count % 3
            or end > index_count
            or not 0 <= submesh.material_index <= 0xFFFFFFFF
        ):
            raise ValueError("Mesh contains an invalid submesh range.")
    if sum(submesh.index_count for submesh in submeshes) != index_count:
        raise ValueError("Submesh ranges must account for every mesh index.")
    cursor = 0
    for submesh in sorted(submeshes, key=lambda item: item.index_offset):
        if submesh.index_offset != cursor:
            raise ValueError("Submesh ranges must partition the index buffer without gaps or overlap.")
        cursor += submesh.index_count

    name_length = len(_name_bytes(mesh.name))
    predicted_payload_size = (
        PREFIX_FIXED.size
        + name_length
        + MESH_COUNTS.size
        + vertex_count * 12
        + (vertex_count * 12 if mesh.normals else 0)
        + (vertex_count * 16 if mesh.tangents else 0)
        + (vertex_count * 8 if mesh.uvs else 0)
        + (vertex_count * 4 if mesh.colors else 0)
        + index_count * 4
        + submesh_count * SUBMESH.size
        + 24
        + CRC.size
    )
    if predicted_payload_size > MAX_PAYLOAD_BYTES:
        raise ValueError("Mesh payload exceeds the protocol payload limit.")

    positions = _pack_float_vectors(mesh.positions, 3, "Positions")
    normals = _pack_float_vectors(mesh.normals, 3, "Normals") if mesh.normals else b""
    tangents = _pack_float_vectors(mesh.tangents, 4, "Tangents") if mesh.tangents else b""
    uvs = _pack_float_vectors(mesh.uvs, 2, "UVs") if mesh.uvs else b""
    colors = _pack_colors(mesh.colors) if mesh.colors else b""
    index_bytes = struct.pack(f"<{index_count}I", *indices) if indices else b""
    submesh_bytes = b"".join(
        SUBMESH.pack(item.index_offset, item.index_count, item.material_index)
        for item in submeshes
    )
    bounds = _finite_values((*mesh.bounds_center, *mesh.bounds_extents), "Bounds")
    if len(bounds) != 6 or any(value < 0.0 for value in bounds[3:]):
        raise ValueError("Bounds require a center[3] and non-negative extents[3].")
    bounds_bytes = struct.pack("<6f", *bounds)

    flags = AttributeFlags(0)
    if normals:
        flags |= AttributeFlags.NORMALS
    if uvs:
        flags |= AttributeFlags.UV0
    if tangents:
        flags |= AttributeFlags.TANGENTS
    if colors:
        flags |= AttributeFlags.COLORS
    canonical = positions + normals + tangents + uvs + colors + index_bytes + submesh_bytes + bounds_bytes
    content_hash = zlib.crc32(canonical) & 0xFFFFFFFF
    header = MESH_COUNTS.pack(
        vertex_count,
        index_count,
        submesh_count,
        content_hash,
        int(flags),
        1 if uvs else 0,
        1 if colors else 0,
        0,
    )
    body = (
        encode_object_prefix(mesh.object_uuid, mesh.name)
        + header
        + positions
        + normals
        + tangents
        + uvs
        + colors
        + index_bytes
        + submesh_bytes
        + bounds_bytes
    )
    if len(body) + CRC.size > MAX_PAYLOAD_BYTES:
        raise ValueError("Mesh payload exceeds the protocol payload limit.")
    return body


def encode_frame(
    packet_type: int | PacketType,
    payload: bytes,
    sequence: int,
    *,
    compress: bool = False,
) -> bytes:
    if not 0 <= int(packet_type) <= 0xFF:
        raise ValueError("Packet type must fit in u8.")
    if not 0 <= int(sequence) <= 0xFFFFFFFF:
        raise ValueError("Sequence number must fit in u32.")
    if len(payload) + CRC.size > MAX_PAYLOAD_BYTES:
        raise ValueError("Uncompressed payload exceeds the protocol payload limit.")
    checked = payload + CRC.pack(zlib.crc32(payload) & 0xFFFFFFFF)
    flags = PacketFlags.NONE
    if compress:
        checked = zlib.compress(checked, level=2)
        flags |= PacketFlags.PAYLOAD_COMPRESSED
    if len(checked) > MAX_PAYLOAD_BYTES:
        raise ValueError("Framed payload exceeds the protocol payload limit.")
    header = HEADER.pack(
        MAGIC,
        PROTOCOL_VERSION,
        int(packet_type),
        int(flags),
        int(sequence),
        len(checked),
    )
    return header + checked


def parse_header(header: bytes) -> tuple[int, int, int, int, int]:
    if len(header) != HEADER.size:
        raise ValueError("Frame header must be exactly 16 bytes.")
    magic, version, packet_type, flags, sequence, payload_length = HEADER.unpack(header)
    if magic != MAGIC:
        raise ValueError("Invalid bridge frame magic.")
    if version != PROTOCOL_VERSION:
        raise ValueError(f"Unsupported protocol version {version}.")
    if flags & ~int(PacketFlags.PAYLOAD_COMPRESSED):
        raise ValueError(f"Unsupported frame flags 0x{flags:04x}.")
    if payload_length > MAX_PAYLOAD_BYTES:
        raise ValueError("Declared frame payload exceeds the protocol limit.")
    return packet_type, flags, sequence, payload_length, version


def decode_frame(frame: bytes) -> Packet:
    if len(frame) < HEADER.size:
        raise ValueError("Truncated frame header.")
    packet_type, flags, sequence, payload_length, _ = parse_header(frame[: HEADER.size])
    if len(frame) != HEADER.size + payload_length:
        raise ValueError("Frame length does not match its header.")
    payload = frame[HEADER.size :]
    if flags & int(PacketFlags.PAYLOAD_COMPRESSED):
        decompressor = zlib.decompressobj()
        payload = decompressor.decompress(payload, MAX_PAYLOAD_BYTES + 1)
        if len(payload) > MAX_PAYLOAD_BYTES or decompressor.unconsumed_tail:
            raise ValueError("Decompressed payload exceeds the protocol limit.")
        payload += decompressor.flush(MAX_PAYLOAD_BYTES + 1 - len(payload))
        if len(payload) > MAX_PAYLOAD_BYTES or not decompressor.eof or decompressor.unused_data:
            raise ValueError("Invalid or oversized compressed frame.")
    if len(payload) < CRC.size:
        raise ValueError("Frame payload is missing its CRC32 trailer.")
    body, received_crc = payload[:-CRC.size], CRC.unpack(payload[-CRC.size :])[0]
    expected_crc = zlib.crc32(body) & 0xFFFFFFFF
    if received_crc != expected_crc:
        raise ValueError("Frame payload CRC32 mismatch.")
    return Packet(packet_type, sequence, flags, body)


def read_exact(sock: socket.socket, byte_count: int) -> bytes:
    data = bytearray()
    while len(data) < byte_count:
        chunk = sock.recv(byte_count - len(data))
        if not chunk:
            raise EOFError("Bridge peer closed the connection.")
        data.extend(chunk)
    return bytes(data)


def read_packet(sock: socket.socket) -> Packet:
    header = read_exact(sock, HEADER.size)
    _, _, _, payload_length, _ = parse_header(header)
    return decode_frame(header + read_exact(sock, payload_length))


def frame_from_buffer(buffer: bytearray) -> bytes | None:
    """Remove and return one complete frame from a receive buffer, if available."""
    if len(buffer) < HEADER.size:
        return None
    _, _, _, payload_length, _ = parse_header(bytes(buffer[: HEADER.size]))
    frame_length = HEADER.size + payload_length
    if len(buffer) < frame_length:
        return None
    frame = bytes(buffer[:frame_length])
    del buffer[:frame_length]
    return frame