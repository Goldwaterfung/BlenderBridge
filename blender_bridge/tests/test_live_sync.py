import os
import sys
import tempfile
import unittest
from types import ModuleType, SimpleNamespace
from unittest.mock import Mock, patch

from blender_bridge.collector import ChangeBatch
from blender_bridge.live_sync import (
    BridgeRuntime,
    _TextureCache,
    _image_metadata,
    _relative_path_or_absolute,
)
from blender_bridge.protocol import PacketType


class LiveSyncTests(unittest.TestCase):
    def test_material_updates_skip_identical_payloads_and_send_changed_payloads(self):
        runtime = BridgeRuntime.__new__(BridgeRuntime)
        runtime.sender = SimpleNamespace(
            connection_state="connected",
            set_hello_payload=Mock(),
            enqueue_state=Mock(),
        )
        runtime._last_material_hashes = {}
        runtime._hello_payload = lambda: b"hello"
        runtime._unit_scale = lambda: 1.0
        payloads = iter((b"same material", b"same material", b"changed material"))
        runtime._materials_packet = lambda _obj: next(payloads)
        object_id = "10000000-0000-0000-0000-000000000001"
        batch = ChangeBatch(
            objects={object_id: SimpleNamespace(name="Material Object")},
            transforms=frozenset(),
            meshes=frozenset(),
            materials=frozenset((object_id,)),
            hierarchy=frozenset(),
            visibility=frozenset(),
            deleted=(),
        )

        runtime._on_changes(batch)
        runtime._on_changes(batch)
        runtime._on_changes(batch)

        self.assertEqual(runtime.sender.enqueue_state.call_count, 2)

    def test_texture_metadata_prefers_embedded_pixels_when_source_file_exists(self):
        with tempfile.NamedTemporaryFile(delete=False) as texture_file:
            texture_path = texture_file.name
        self.addCleanup(lambda: os.path.exists(texture_path) and os.remove(texture_path))

        bpy = ModuleType("bpy")
        bpy.path = SimpleNamespace(abspath=lambda _path, library=None: texture_path)
        image = SimpleNamespace(
            filepath=texture_path,
            library=None,
            is_dirty=False,
        )
        with patch.dict(sys.modules, {"bpy": bpy}):
            with patch(
                "blender_bridge.live_sync._image_png_bytes",
                return_value=b"embedded image",
            ):
                metadata = _image_metadata(image, bpy)

        self.assertTrue(metadata["prefer_embedded"])
        self.assertEqual(metadata["png_base64"], "ZW1iZWRkZWQgaW1hZ2U=")

    def test_texture_cache_omits_unchanged_bytes_until_image_changes(self):
        with tempfile.NamedTemporaryFile(delete=False) as texture_file:
            texture_path = texture_file.name
        self.addCleanup(lambda: os.path.exists(texture_path) and os.remove(texture_path))

        bpy = ModuleType("bpy")
        bpy.path = SimpleNamespace(abspath=lambda _path, library=None: texture_path)
        image = SimpleNamespace(
            name="Base Color",
            filepath=texture_path,
            library=None,
            is_dirty=False,
            as_pointer=lambda: 42,
        )
        cache = _TextureCache()

        with patch.dict(sys.modules, {"bpy": bpy}):
            with patch(
                "blender_bridge.live_sync._image_png_bytes",
                return_value=b"first image",
            ):
                first = cache.metadata(image, bpy, "object", "base_color")
                repeated = cache.metadata(image, bpy, "object", "base_color")

            self.assertTrue(first["png_base64"])
            self.assertFalse(repeated["png_base64"])
            self.assertEqual(first["hash"], repeated["hash"])

            file_stat = os.stat(texture_path)
            os.utime(
                texture_path,
                ns=(file_stat.st_atime_ns, file_stat.st_mtime_ns + 1_000_000_000),
            )
            with patch(
                "blender_bridge.live_sync._image_png_bytes",
                return_value=b"second image",
            ):
                changed = cache.metadata(image, bpy, "object", "base_color")

        self.assertTrue(changed["png_base64"])
        self.assertNotEqual(first["hash"], changed["hash"])

    def test_texture_cache_sends_bytes_for_each_object_and_channel_once(self):
        bpy = ModuleType("bpy")
        bpy.path = SimpleNamespace(abspath=lambda _path, library=None: "")
        image = SimpleNamespace(
            name="Base Color",
            filepath="",
            library=None,
            is_dirty=False,
            as_pointer=lambda: 42,
        )
        cache = _TextureCache()

        with patch.dict(sys.modules, {"bpy": bpy}):
            with patch(
                "blender_bridge.live_sync._image_png_bytes",
                return_value=b"image",
            ):
                first = cache.metadata(image, bpy, "object-a", "base_color")
                other_object = cache.metadata(image, bpy, "object-b", "base_color")
                other_channel = cache.metadata(image, bpy, "object-a", "emission")

        self.assertTrue(first["png_base64"])
        self.assertTrue(other_object["png_base64"])
        self.assertTrue(other_channel["png_base64"])

    def test_snapshot_forces_texture_bytes_after_an_incremental_send(self):
        bpy = ModuleType("bpy")
        bpy.path = SimpleNamespace(abspath=lambda _path, library=None: "")
        image = SimpleNamespace(
            name="Base Color",
            filepath="",
            library=None,
            is_dirty=False,
            as_pointer=lambda: 42,
        )
        cache = _TextureCache()

        with patch.dict(sys.modules, {"bpy": bpy}):
            with patch(
                "blender_bridge.live_sync._image_png_bytes",
                return_value=b"image",
            ):
                first = cache.metadata(image, bpy, "object", "base_color")
                repeated = cache.metadata(image, bpy, "object", "base_color")
                cache.prepare_snapshot()
                snapshot = cache.metadata(image, bpy, "object", "base_color")

        self.assertTrue(first["png_base64"])
        self.assertFalse(repeated["png_base64"])
        self.assertTrue(snapshot["png_base64"])

    def test_transform_dispatch_survives_a_failed_hello_refresh(self):
        runtime = BridgeRuntime.__new__(BridgeRuntime)
        queued = []
        runtime.sender = SimpleNamespace(
            connection_state="connected",
            set_hello_payload=Mock(side_effect=RuntimeError("hello failed")),
            enqueue_state=Mock(
                side_effect=lambda packet_type, object_id, payload: queued.append(
                    (packet_type, object_id, payload)
                )
            ),
        )
        runtime._last_material_hashes = {}
        runtime._texture_cache = _TextureCache()
        runtime._slot_materials = {}
        runtime._unit_scale = lambda: 1.0
        runtime._hello_payload = Mock(return_value=b"hello")
        runtime._transform_packet = Mock(return_value=b"transform")
        runtime.collector = SimpleNamespace(sibling_index=lambda _obj: 0)

        object_id = "10000000-0000-0000-0000-000000000001"
        batch = ChangeBatch(
            objects={object_id: SimpleNamespace(name="Object", parent=None)},
            transforms=frozenset((object_id,)),
            meshes=frozenset(),
            materials=frozenset(),
            hierarchy=frozenset((object_id,)),
            visibility=frozenset(),
            deleted=(),
        )

        runtime._on_changes(batch)

        self.assertIn((PacketType.TRANSFORM, object_id, b"transform"), queued)
        runtime.sender.set_hello_payload.assert_called_once_with(b"hello")

    def test_relative_texture_path_falls_back_to_absolute_across_mounts(self):
        absolute_path = "C:\\Textures\\base_color.png"

        def relpath(_path):
            raise ValueError("path is on mount 'C', start on mount 'D'")

        bpy = SimpleNamespace(path=SimpleNamespace(relpath=relpath))

        self.assertEqual(_relative_path_or_absolute(bpy, absolute_path), absolute_path)

    def test_relative_texture_path_uses_blender_relative_path_when_available(self):
        bpy = SimpleNamespace(
            path=SimpleNamespace(relpath=lambda _path: "//textures/base_color.png")
        )

        self.assertEqual(
            _relative_path_or_absolute(bpy, "D:\\Project\\textures\\base_color.png"),
            "//textures/base_color.png",
        )

    def test_received_project_root_and_resync_queue_snapshot(self):
        runtime = BridgeRuntime.__new__(BridgeRuntime)
        runtime.sender = SimpleNamespace(
            poll_events=Mock(
                return_value=[
                    ("connected", 1),
                    ("resync", 42),
                    ("project_root", "D:/Unity/Bridge"),
                ]
            )
        )
        runtime.scene = SimpleNamespace(blender_bridge_project_root="")
        runtime.last_error = ""
        queued = []
        runtime._queue_snapshot = lambda resync_id, include_hello: queued.append(
            (resync_id, include_hello)
        )

        self.assertEqual(runtime._poll_events(), 0.05)
        self.assertEqual(runtime.scene.blender_bridge_project_root, "D:/Unity/Bridge")
        self.assertEqual(queued, [(42, True)])


if __name__ == "__main__":
    unittest.main()
