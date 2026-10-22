"""Main-thread integration of scope, snapshots, packet encoding, and the sender."""

from __future__ import annotations

import base64
import hashlib
import logging
import os
import struct
import tempfile
import uuid
import zlib
from typing import Any

from .collector import ChangeBatch, ChangeCollector
from .convention import (
    effective_unit_scale,
    is_mesh_object,
    local_trs,
    object_uuid,
    resolve_export_scope,
    to_unity_pos,
    to_unity_rot,
)
from .mesh_data import extract_mesh
from .protocol import (
    encode_hello,
    encode_hierarchy,
    encode_materials,
    encode_mesh,
    encode_object_deleted,
    encode_resync_complete,
    encode_transform,
    encode_visibility,
    PacketType,
)
from .sender import BridgeSender, OutboundPacket

log = logging.getLogger("blender_bridge")


def _property_value(value: Any) -> Any:
    if isinstance(value, (str, int, float, bool)) or value is None:
        return value
    if hasattr(value, "to_list"):
        return value.to_list()
    if isinstance(value, (list, tuple)):
        return [_property_value(item) for item in value]
    return str(value)


def _socket(node: Any, *names: str) -> Any | None:
    for name in names:
        socket = node.inputs.get(name)
        if socket is not None:
            return socket
    return None


def _relative_path_or_absolute(bpy: Any, path: str) -> str:
    try:
        return bpy.path.relpath(path)
    except ValueError:
        return path


def _image_png_bytes(image: Any, bpy: Any) -> bytes:
    width, height = (int(value) for value in image.size)
    if width < 1 or height < 1:
        raise ValueError(f"Image {image.name!r} has invalid dimensions.")

    temporary_image = bpy.data.images.new(
        name=f"Blender Bridge temporary {image.name}",
        width=width,
        height=height,
        alpha=True,
        float_buffer=False,
    )
    try:
        source_space = getattr(
            getattr(image, "colorspace_settings", None),
            "name",
            None,
        )
        if source_space:
            temporary_image.colorspace_settings.name = source_space
        temporary_image.pixels[:] = image.pixels[:]
        temporary_image.file_format = "PNG"
        with tempfile.TemporaryDirectory(prefix="blender-bridge-") as directory:
            path = os.path.join(directory, "texture.png")
            temporary_image.save(filepath=path, save_copy=True)
            with open(path, "rb") as texture_file:
                return texture_file.read()
    finally:
        bpy.data.images.remove(temporary_image)


def _image_absolute_path(image: Any, bpy: Any) -> str:
    resolved_path = bpy.path.abspath(
        image.filepath,
        library=getattr(image, "library", None),
    )
    return os.path.abspath(resolved_path) if resolved_path else ""


def _image_pointer(image: Any) -> int:
    as_pointer = getattr(image, "as_pointer", None)
    return int(as_pointer()) if callable(as_pointer) else id(image)


def _image_signature(image: Any, bpy: Any) -> tuple[Any, ...]:
    """Cheap change signal that avoids hashing or re-encoding unchanged images."""
    try:
        size = tuple(int(value) for value in image.size)
    except (AttributeError, TypeError, ValueError):
        size = ()

    absolute_path = _image_absolute_path(image, bpy)
    packed = getattr(image, "packed_file", None)
    packed_size = int(getattr(packed, "size", 0) or 0)
    modified_ns = 0
    file_size = 0
    if absolute_path:
        try:
            stat = os.stat(absolute_path)
            modified_ns = stat.st_mtime_ns
            file_size = stat.st_size
        except OSError:
            pass

    return (
        str(getattr(image, "name", "")),
        size,
        absolute_path,
        bool(getattr(image, "is_dirty", False)),
        str(getattr(getattr(image, "colorspace_settings", None), "name", "")),
        str(getattr(image, "source", "")),
        int(getattr(image, "frame_current", 0) or 0),
        packed_size,
        modified_ns,
        file_size,
    )


def _image_metadata(image: Any, bpy: Any) -> dict[str, Any]:
    png_bytes = _image_png_bytes(image, bpy)
    return {
        "path": _image_absolute_path(image, bpy),
        "prefer_embedded": True,
        "hash": hashlib.sha256(png_bytes).hexdigest(),
        "png_base64": base64.b64encode(png_bytes).decode("ascii"),
    }


class _TextureCache:
    """Reuses encoded PNGs and omits bytes Unity already holds for an object/channel."""

    def __init__(self) -> None:
        self._encoded: dict[int, tuple[tuple[Any, ...], str, str]] = {}
        self._sent: dict[tuple[str, str, int], str] = {}

    def metadata(
        self,
        image: Any,
        bpy: Any,
        object_id: str,
        channel: str,
    ) -> dict[str, Any]:
        pointer = _image_pointer(image)
        signature = _image_signature(image, bpy)
        encoded = self._encoded.get(pointer)
        if encoded is None or encoded[0] != signature:
            png_bytes = _image_png_bytes(image, bpy)
            encoded = (
                signature,
                hashlib.sha256(png_bytes).hexdigest(),
                base64.b64encode(png_bytes).decode("ascii"),
            )
            self._encoded[pointer] = encoded

        _, digest, base64_text = encoded
        key = (object_id, channel, pointer)
        already_sent = self._sent.get(key) == digest
        self._sent[key] = digest
        return {
            "path": _image_absolute_path(image, bpy),
            "prefer_embedded": True,
            "hash": digest,
            "png_base64": "" if already_sent else base64_text,
        }

    def forget(self, prefix: str) -> None:
        for key in [
            key
            for key in self._sent
            if key[0] == prefix or key[0].startswith(prefix + ":")
        ]:
            del self._sent[key]

    def prepare_snapshot(self) -> None:
        """Force a snapshot to carry bytes for every texture channel.

        A snapshot is the only guaranteed recovery point for Unity. Reusing the
        incremental "already sent" set here can leave Unity with hash-only
        metadata for a material it has just re-resolved (for example after a
        resync), which results in a missing texture. The encoded-PNG cache is
        kept, so this does not re-encode images.
        """
        self._sent.clear()

    def clear(self) -> None:
        self._encoded.clear()
        self._sent.clear()


def _linked_image(socket: Any, *, through_normal_map: bool = False) -> Any | None:
    if socket is None or not socket.is_linked:
        return None
    for link in socket.links:
        node = link.from_node
        if getattr(node, "type", "") == "TEX_IMAGE":
            return getattr(node, "image", None)
        if through_normal_map and getattr(node, "type", "") == "NORMAL_MAP":
            color_socket = _socket(node, "Color")
            image = _linked_image(color_socket)
            if image is not None:
                return image
    return None


def _material_metadata(
    material: Any,
    object_id: str,
    slot_index: int,
    *,
    include_textures: bool = True,
    texture_cache: _TextureCache | None = None,
) -> dict[str, Any]:
    import bpy

    base_color = tuple(float(value) for value in material.diffuse_color)
    metallic = 0.0
    roughness = 0.5
    textures: dict[str, Any] = {}
    tree = getattr(material, "node_tree", None)
    if tree is not None:
        bsdf = next(
            (node for node in tree.nodes if node.type == "BSDF_PRINCIPLED"),
            None,
        )
        if bsdf is not None:
            base_socket = _socket(bsdf, "Base Color")
            metallic_socket = _socket(bsdf, "Metallic")
            roughness_socket = _socket(bsdf, "Roughness")
            if base_socket is not None:
                base_color = tuple(float(value) for value in base_socket.default_value)
            if metallic_socket is not None:
                metallic = float(metallic_socket.default_value)
            if roughness_socket is not None:
                roughness = float(roughness_socket.default_value)
            channel_sockets = {
                "base_color": base_socket,
                "metallic": metallic_socket,
                "roughness": roughness_socket,
                "normal": _socket(bsdf, "Normal"),
                "emission": _socket(bsdf, "Emission Color", "Emission"),
            }
            channel_images = {
                channel: _linked_image(socket, through_normal_map=channel == "normal")
                for channel, socket in channel_sockets.items()
            }
            if channel_images["base_color"] is not None:
                base_color = (1.0, 1.0, 1.0, 1.0)
            if channel_images["metallic"] is not None:
                metallic = 1.0
            if channel_images["roughness"] is not None:
                roughness = 0.0
            if include_textures:
                for channel, image in channel_images.items():
                    if image is not None:
                        textures[channel] = (
                            texture_cache.metadata(
                                image,
                                bpy,
                                f"{object_id}:{slot_index}",
                                channel,
                            )
                            if texture_cache is not None
                            else _image_metadata(image, bpy)
                        )
    return {
        "object_uuid": object_id,
        "index": int(slot_index),
        "name": material.name,
        "base_color": list(base_color),
        "metallic": metallic,
        "smoothness": 1.0 - roughness,
        "textures": textures,
    }


class BridgeRuntime:
    def __init__(self, scene: Any) -> None:
        self.scene = scene
        self.session_id = str(uuid.uuid4())
        self.sender: BridgeSender | None = None
        self.collector: ChangeCollector | None = None
        self.last_error = ""
        self.objects_applied = 0
        self._event_timer_registered = False
        self._last_material_hashes: dict[str, bytes] = {}
        self._texture_cache = _TextureCache()
        self._slot_materials: dict[str, tuple[int, ...]] = {}

    def _scope(self) -> list[Any]:
        import bpy

        return resolve_export_scope(self.scene, bpy.context, self.scene.blender_bridge_scope)

    def _unit_scale(self) -> float:
        return effective_unit_scale(self.scene, self.scene.blender_bridge_unit_scale)

    def _hello_payload(self) -> bytes:
        import bpy

        objects = self._scope()
        inventory = []
        collections: set[str] = set()
        materials = []
        custom_properties: dict[str, dict[str, Any]] = {}
        for obj in objects:
            object_id = str(object_uuid(obj))
            inventory.append({"uuid": object_id, "has_mesh": is_mesh_object(obj)})
            collections.update(collection.name for collection in getattr(obj, "users_collection", ()))
            custom_properties[object_id] = {
                key: _property_value(obj[key])
                for key in obj.keys()
                if key != "_RNA_UI"
            }
            if is_mesh_object(obj):
                for index, slot in enumerate(obj.material_slots):
                    if slot.material is not None:
                        materials.append(
                            _material_metadata(
                                slot.material,
                                object_id,
                                index,
                                include_textures=False,
                            )
                        )
        metadata = {
            "blend_path": bpy.data.filepath,
            "session_id": self.session_id,
            "objects": inventory,
            "collections": sorted(collections),
            "materials": materials,
            "custom_props": custom_properties,
        }
        version = bpy.app.version
        session_name = bpy.path.basename(bpy.data.filepath) or "Untitled"
        return encode_hello(
            blender_version=(version[0], version[1]),
            unit_scale=self._unit_scale(),
            session_name=session_name,
            metadata=metadata,
        )

    def start(self) -> None:
        if self.sender is not None:
            return
        import bpy

        # A new connection starts from a fresh Unity handshake; never assume prior state.
        self._texture_cache.clear()
        self._slot_materials.clear()
        self._last_material_hashes.clear()
        self.sender = BridgeSender(
            self.scene.blender_bridge_port,
            self._hello_payload(),
        )
        self.collector = ChangeCollector(self._scope, self._on_changes)
        try:
            self.collector.register()
            self.sender.start()
            bpy.app.timers.register(self._poll_events, first_interval=0.05)
            self._event_timer_registered = True
        except Exception:
            self.stop()
            raise

    def stop(self) -> None:
        import bpy

        if self.collector is not None:
            self.collector.unregister()
            self.collector = None
        if bpy.app.timers.is_registered(self._poll_events):
            bpy.app.timers.unregister(self._poll_events)
        self._event_timer_registered = False
        if self.sender is not None:
            self.sender.stop()
            self.sender = None

    def new_session(self) -> None:
        self.session_id = str(uuid.uuid4())
        self._last_material_hashes.clear()
        self._texture_cache.clear()
        self._slot_materials.clear()
        if self.sender is not None:
            if self.collector is not None:
                self.collector.unregister()
            self.collector = ChangeCollector(self._scope, self._on_changes)
            self.collector.register()
            self.sender.set_hello_payload(self._hello_payload())
            self._queue_snapshot(resync_id=0, include_hello=True)
            self.sender.enqueue_control(PacketType.PROJECT_ROOT_REQUEST, b"")

    def refresh_settings(self, scene: Any) -> None:
        self.scene = scene
        if self.sender is None:
            return
        if self.sender.port != int(scene.blender_bridge_port):
            self.stop()
            self.start()
            return
        self.sender.set_hello_payload(self._hello_payload())
        if self.sender.connection_state != "connected":
            return
        import bpy

        def refresh() -> None:
            # Scope or unit changes may reveal objects Unity has never seen.
            self._texture_cache.clear()
            self._slot_materials.clear()
            self._queue_snapshot(resync_id=0, include_hello=True)
            return None

        bpy.app.timers.register(refresh, first_interval=0.0)

    def _transform_packet(self, obj: Any, unit_scale: float) -> bytes:
        import bpy

        depsgraph = bpy.context.evaluated_depsgraph_get()
        evaluated = obj.evaluated_get(depsgraph)
        location, rotation, scale = local_trs(evaluated)
        unity_rotation_wxyz = to_unity_rot(rotation)
        rotation_xyzw = (
            unity_rotation_wxyz[1],
            unity_rotation_wxyz[2],
            unity_rotation_wxyz[3],
            unity_rotation_wxyz[0],
        )
        return encode_transform(
            object_uuid(obj),
            obj.name,
            to_unity_pos(location, unit_scale),
            rotation_xyzw,
            tuple(float(value) for value in scale),
        )

    def _make_snapshot(self, resync_id: int, include_hello: bool) -> list[OutboundPacket]:
        import bpy

        if self.sender is None:
            return []
        # A snapshot must be self-contained: Unity may have discarded or
        # re-resolved material state since the last live packet. Reuse encoded
        # PNGs, but include their bytes again.
        self._texture_cache.prepare_snapshot()
        objects = self._scope()
        unit_scale = self._unit_scale()
        collector = getattr(self, "collector", None)
        sibling_index = (
            collector.sibling_index
            if collector is not None
            else ChangeCollector._sibling_index
        )
        packets: list[OutboundPacket] = []
        if include_hello:
            hello = self._hello_payload()
            self.sender.set_hello_payload(hello)
            packets.append(OutboundPacket(PacketType.HELLO, hello))
        encoded: dict[str, tuple[Any, bytes, bytes, bytes, bytes, bytes]] = {}
        for obj in objects:
            identity = str(object_uuid(obj))
            parent = getattr(obj, "parent", None)
            parent_uuid = object_uuid(parent) if parent is not None else None
            hierarchy = encode_hierarchy(
                identity,
                obj.name,
                parent_uuid,
                sibling_index(obj),
            )
            visibility = encode_visibility(identity, obj.name, not obj.hide_get())
            transform = self._transform_packet(obj, unit_scale)
            mesh_payload = encode_mesh(extract_mesh(obj, bpy.context.evaluated_depsgraph_get(), unit_scale)) if is_mesh_object(obj) else b""
            material_payload = self._materials_packet(obj)
            self._last_material_hashes[identity] = hashlib.sha256(material_payload).digest()
            encoded[identity] = (obj, hierarchy, visibility, transform, mesh_payload, material_payload)

        for identity, (obj, hierarchy, _visibility, _transform, _mesh, _materials) in encoded.items():
            packets.append(OutboundPacket(PacketType.HIERARCHY, hierarchy, (identity, PacketType.HIERARCHY)))
        for identity, (obj, _hierarchy, visibility, _transform, _mesh, _materials) in encoded.items():
            packets.append(OutboundPacket(PacketType.VISIBILITY, visibility, (identity, PacketType.VISIBILITY)))
        for identity, (obj, _hierarchy, _visibility, transform, _mesh, _materials) in encoded.items():
            packets.append(OutboundPacket(PacketType.TRANSFORM, transform, (identity, PacketType.TRANSFORM)))
        for identity, (obj, _hierarchy, _visibility, _transform, _mesh, material_payload) in encoded.items():
            packets.append(
                OutboundPacket(
                    PacketType.MATERIALS,
                    material_payload,
                    (identity, PacketType.MATERIALS),
                )
            )
        for identity, (obj, _hierarchy, _visibility, _transform, mesh_payload, _materials) in encoded.items():
            if mesh_payload:
                packets.append(OutboundPacket(PacketType.MESH_FULL, mesh_payload, (identity, PacketType.MESH_FULL)))
        packets.append(
            OutboundPacket(
                PacketType.RESYNC_COMPLETE,
                encode_resync_complete(resync_id, len(objects)),
            )
        )
        return packets

    def _queue_snapshot(self, resync_id: int, include_hello: bool) -> None:
        if self.sender is None or self.sender.connection_state != "connected":
            return
        try:
            self.sender.enqueue_snapshot(self._make_snapshot(resync_id, include_hello))
            self.objects_applied = 0
            self.last_error = ""
        except (
            AttributeError,
            IndexError,
            OSError,
            OverflowError,
            ReferenceError,
            RuntimeError,
            struct.error,
            TypeError,
            ValueError,
            zlib.error,
        ) as exc:
            self.last_error = str(exc)
            # The snapshot may have partially consumed the "already sent" tracking,
            # so force the next material packets to carry full texture bytes.
            texture_cache = getattr(self, "_texture_cache", None)
            if texture_cache is not None:
                texture_cache.clear()
            slot_materials = getattr(self, "_slot_materials", None)
            if slot_materials is not None:
                slot_materials.clear()
            log.exception("Could not build a complete Blender bridge snapshot.")
            self.sender.fail_snapshot(self.last_error)

    def _on_changes(self, batch: ChangeBatch) -> None:
        if self.sender is None or self.sender.connection_state != "connected":
            return
        try:
            unit_scale = self._unit_scale()
        except Exception:
            log.exception("Cannot determine the bridge unit scale; using 1.0.")
            unit_scale = 1.0

        # Deletions first so Unity drops stale objects before any replacement.
        for object_id, name in batch.deleted:
            identity = str(object_id)
            self._last_material_hashes.pop(identity, None)
            texture_cache = getattr(self, "_texture_cache", None)
            if texture_cache is not None:
                texture_cache.forget(identity)
            slot_materials = getattr(self, "_slot_materials", None)
            if slot_materials is not None:
                slot_materials.pop(identity, None)
            try:
                self.sender.enqueue_control(
                    PacketType.OBJECT_DELETED,
                    encode_object_deleted(object_id, name),
                    object_uuid=object_id,
                )
            except BufferError as exc:
                # The sender already closed the socket to force a fresh snapshot.
                # Do not abort the rest of this batch: the calls below become
                # no-ops until the reconnected snapshot is queued.
                self.last_error = str(exc)
                log.error("%s", exc)

        # Transforms are the highest-frequency and most user-visible update.
        # Stream them before any inventory/material work so a failure in a
        # heavier section can never starve them.
        for object_id in batch.transforms:
            obj = batch.objects.get(object_id)
            if obj is None:
                continue
            try:
                payload = self._transform_packet(obj, unit_scale)
            except Exception as exc:
                self.last_error = f"{obj.name}: {exc}"
                log.exception("Cannot stream transform: %s", self.last_error)
                continue
            try:
                self.sender.enqueue_state(PacketType.TRANSFORM, object_id, payload)
            except Exception:
                log.exception("Cannot queue transform for %s.", obj.name)

        collector = getattr(self, "collector", None)
        sibling_index = (
            collector.sibling_index
            if collector is not None
            else ChangeCollector._sibling_index
        )
        for object_id in batch.hierarchy:
            obj = batch.objects.get(object_id)
            if obj is None:
                continue
            try:
                parent = getattr(obj, "parent", None)
                payload = encode_hierarchy(
                    object_id,
                    obj.name,
                    object_uuid(parent) if parent is not None else None,
                    sibling_index(obj),
                )
                self.sender.enqueue_state(PacketType.HIERARCHY, object_id, payload)
            except Exception:
                log.exception("Cannot stream hierarchy for %s.", obj.name)

        for object_id in batch.visibility:
            obj = batch.objects.get(object_id)
            if obj is None:
                continue
            try:
                self.sender.enqueue_state(
                    PacketType.VISIBILITY,
                    object_id,
                    encode_visibility(object_id, obj.name, not obj.hide_get()),
                )
            except Exception:
                log.exception("Cannot stream visibility for %s.", obj.name)

        for object_id in batch.materials:
            obj = batch.objects.get(object_id)
            if obj is None:
                continue
            try:
                material_payload = self._materials_packet(obj)
                material_hash = hashlib.sha256(material_payload).digest()
                identity = str(object_id)
                if self._last_material_hashes.get(identity) == material_hash:
                    continue

                self.sender.enqueue_state(PacketType.MATERIALS, object_id, material_payload)
                self._last_material_hashes[identity] = material_hash
            except Exception as exc:
                self.last_error = f"{obj.name}: {exc}"
                log.exception("Cannot stream Blender material textures: %s", self.last_error)

        for object_id in batch.meshes:
            obj = batch.objects.get(object_id)
            if obj is None:
                continue
            try:
                import bpy

                mesh_payload = encode_mesh(
                    extract_mesh(obj, bpy.context.evaluated_depsgraph_get(), unit_scale)
                )
            except Exception as exc:
                self.last_error = f"{obj.name}: {exc}"
                log.exception("Cannot stream evaluated mesh: %s", self.last_error)
                continue
            try:
                self.sender.enqueue_state(PacketType.MESH_FULL, object_id, mesh_payload)
            except Exception:
                log.exception("Cannot queue mesh for %s.", obj.name)

        if batch.deleted or batch.hierarchy:
            # Only inventory changes can make the handshake Hello stale; the
            # resync snapshot carries authoritative material metadata. This is
            # deliberately last: a failure here must not block live transforms.
            try:
                self.sender.set_hello_payload(self._hello_payload())
            except Exception:
                log.exception("Could not refresh the bridge handshake payload.")

    def _materials_packet(self, obj: Any) -> bytes:
        object_id = object_uuid(obj)
        identity = str(object_id)
        slots = list(obj.material_slots)
        texture_cache = getattr(self, "_texture_cache", None)
        slot_materials = getattr(self, "_slot_materials", None)
        if texture_cache is not None and slot_materials is not None:
            pointers = tuple(
                _image_pointer(slot.material) if slot.material is not None else 0
                for slot in slots
            )
            previous = slot_materials.get(identity, ())
            for index, pointer in enumerate(pointers):
                if index >= len(previous) or previous[index] != pointer:
                    texture_cache.forget(f"{identity}:{index}")
            slot_materials[identity] = pointers

        materials = [
            _material_metadata(
                slot.material,
                identity,
                index,
                texture_cache=texture_cache,
            )
            for index, slot in enumerate(slots)
            if slot.material is not None
        ]
        return encode_materials(object_id, obj.name, materials)

    def _poll_events(self) -> float | None:
        if self.sender is None:
            return None
        try:
            for event, value in self.sender.poll_events():
                if event == "connected":
                    # A reconnected Unity may have lost its material state: resend textures in full.
                    texture_cache = getattr(self, "_texture_cache", None)
                    if texture_cache is not None:
                        texture_cache.clear()
                    material_hashes = getattr(self, "_last_material_hashes", None)
                    if material_hashes is not None:
                        material_hashes.clear()
                    slot_materials = getattr(self, "_slot_materials", None)
                    if slot_materials is not None:
                        slot_materials.clear()
                elif event == "project_root":
                    self.scene.blender_bridge_project_root = str(value)
                elif event == "resync":
                    self._queue_snapshot(resync_id=int(value), include_hello=True)
                elif event == "error":
                    self.last_error = str(value)
                    self.scene.blender_bridge_project_root = ""
        except Exception:
            # Blender unregisters a timer that raises. Keep polling so a single
            # bad snapshot cannot permanently stop live updates.
            log.exception("Bridge event processing failed.")
        return 0.05
