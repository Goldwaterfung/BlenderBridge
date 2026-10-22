import unittest
import uuid
from types import SimpleNamespace
from unittest.mock import patch

from blender_bridge.collector import ChangeCollector


class FakeMesh:
    vertices = ()


class FakeObject(dict):
    def __init__(self, name, data):
        super().__init__()
        self.name = name
        self.name_full = name
        self.type = "MESH"
        self.data = data
        self.parent = None
        self.hide_viewport = False
        self.users_scene = []
        self.material_slots = []

    def as_pointer(self):
        return id(self)


class CollectorTests(unittest.TestCase):
    def test_shared_mesh_updates_mark_every_exported_object_dirty(self):
        mesh = FakeMesh()
        first = FakeObject("First", mesh)
        second = FakeObject("Second", mesh)
        first["bridge_uuid"] = str(uuid.uuid4())
        second["bridge_uuid"] = str(uuid.uuid4())
        objects = [first, second]
        collector = ChangeCollector(lambda: objects, lambda _batch: None)
        collector._reconcile()
        collector._dirty_meshes.clear()

        collector._on_depsgraph_update(
            None,
            SimpleNamespace(updates=(SimpleNamespace(id=mesh),)),
        )

        self.assertEqual(collector._dirty_meshes, {uuid.UUID(first["bridge_uuid"]), uuid.UUID(second["bridge_uuid"])})

    def test_evaluated_object_update_marks_original_transform_dirty(self):
        obj = FakeObject("Object", FakeMesh())
        obj["bridge_uuid"] = str(uuid.uuid4())
        collector = ChangeCollector(lambda: [obj], lambda _batch: None)
        collector._reconcile()
        collector._dirty_transforms.clear()

        evaluated_object = SimpleNamespace(
            type="MESH",
            data=obj.data,
            original=obj,
            as_pointer=lambda: id(obj) + 1,
        )
        wrapped_original = SimpleNamespace(
            type="MESH",
            data=obj.data,
            as_pointer=obj.as_pointer,
        )
        collector._on_depsgraph_update(
            None,
            SimpleNamespace(
                updates=(
                    SimpleNamespace(
                        id=evaluated_object,
                        is_updated_transform=True,
                        is_updated_geometry=False,
                        is_updated_shading=False,
                    ),
                    SimpleNamespace(
                        id=wrapped_original,
                        is_updated_transform=True,
                        is_updated_geometry=False,
                        is_updated_shading=False,
                    ),
                )
            ),
        )

        self.assertEqual(collector._dirty_transforms, {uuid.UUID(obj["bridge_uuid"])})

    def test_shading_only_update_marks_materials_but_not_meshes_dirty(self):
        obj = FakeObject("Object", FakeMesh())
        obj["bridge_uuid"] = str(uuid.uuid4())
        collector = ChangeCollector(lambda: [obj], lambda _batch: None)
        collector._reconcile()
        collector._dirty_meshes.clear()
        collector._dirty_materials.clear()

        collector._on_depsgraph_update(
            None,
            SimpleNamespace(
                updates=(
                    SimpleNamespace(
                        id=obj,
                        is_updated_transform=False,
                        is_updated_geometry=False,
                        is_updated_shading=True,
                    ),
                )
            ),
        )

        object_id = uuid.UUID(obj["bridge_uuid"])
        self.assertEqual(collector._dirty_materials, {object_id})
        self.assertEqual(collector._dirty_meshes, set())

    def test_sibling_indices_follow_parent_child_order(self):
        parent = FakeObject("Parent", FakeMesh())
        first = FakeObject("First", FakeMesh())
        second = FakeObject("Second", FakeMesh())
        for obj in (parent, first, second):
            obj["bridge_uuid"] = str(uuid.uuid4())
        parent.children = [second, first]
        first.parent = parent
        second.parent = parent
        objects = [parent, first, second]
        collector = ChangeCollector(lambda: objects, lambda _batch: None)
        collector._reconcile()

        self.assertEqual(collector.sibling_index(second), 0)
        self.assertEqual(collector.sibling_index(first), 1)

    def test_material_image_update_marks_assigned_objects_materials_dirty(self):
        obj = FakeObject("Object", FakeMesh())
        obj["bridge_uuid"] = str(uuid.uuid4())
        image = SimpleNamespace(as_pointer=lambda: 200)
        image_node = SimpleNamespace(type="TEX_IMAGE", image=image)
        material = SimpleNamespace(
            as_pointer=lambda: 100,
            node_tree=SimpleNamespace(nodes=(image_node,)),
        )
        obj.material_slots.append(SimpleNamespace(material=material))
        collector = ChangeCollector(lambda: [obj], lambda _batch: None)
        collector._reconcile()
        collector._dirty_materials.clear()

        collector._on_depsgraph_update(
            None,
            SimpleNamespace(updates=(SimpleNamespace(id=image),)),
        )

        self.assertEqual(collector._dirty_materials, {uuid.UUID(obj["bridge_uuid"])})

    def test_periodic_reconciliation_detects_creation_and_deletion(self):
        first = FakeObject("First", FakeMesh())
        first["bridge_uuid"] = str(uuid.uuid4())
        objects = [first]
        collector = ChangeCollector(lambda: objects, lambda _batch: None)
        collector._reconcile()
        collector._dirty_transforms.clear()
        collector._dirty_meshes.clear()
        collector._dirty_materials.clear()
        collector._dirty_hierarchy.clear()
        collector._dirty_visibility.clear()
        first_id = uuid.UUID(first["bridge_uuid"])

        second = FakeObject("Second", FakeMesh())
        second["bridge_uuid"] = str(uuid.uuid4())
        objects[:] = [second]
        collector._reconcile()

        self.assertIn(first_id, collector._deleted)
        self.assertIn(uuid.UUID(second["bridge_uuid"]), collector._dirty_meshes)
        self.assertIn(uuid.UUID(second["bridge_uuid"]), collector._dirty_hierarchy)

    def test_sibling_order_and_visibility_are_reconciled(self):
        first = FakeObject("First", FakeMesh())
        second = FakeObject("Second", FakeMesh())
        first["bridge_uuid"] = str(uuid.uuid4())
        second["bridge_uuid"] = str(uuid.uuid4())
        objects = [first, second]
        scene = SimpleNamespace(objects=objects)
        first.users_scene = [scene]
        second.users_scene = [scene]
        collector = ChangeCollector(lambda: objects, lambda _batch: None)
        collector._reconcile()
        collector._dirty_transforms.clear()
        collector._dirty_meshes.clear()
        collector._dirty_materials.clear()
        collector._dirty_hierarchy.clear()
        collector._dirty_visibility.clear()
        first_id = uuid.UUID(first["bridge_uuid"])
        second_id = uuid.UUID(second["bridge_uuid"])

        objects.reverse()
        second.hide_viewport = True
        collector._reconcile()

        self.assertEqual(collector._dirty_hierarchy, {first_id, second_id})
        self.assertEqual(collector._dirty_visibility, {second_id})

    def test_trailing_flush_retains_changes_until_dispatch_deadline(self):
        obj = FakeObject("Object", FakeMesh())
        obj["bridge_uuid"] = str(uuid.uuid4())
        batches = []
        collector = ChangeCollector(lambda: [obj], batches.append)
        collector._reconcile()
        collector._dirty_transforms.clear()
        collector._dirty_meshes.clear()
        collector._dirty_materials.clear()
        collector._dirty_hierarchy.clear()
        collector._dirty_visibility.clear()
        collector._registered = True
        object_id = uuid.UUID(obj["bridge_uuid"])
        collector._dirty_transforms.add(object_id)

        with patch("blender_bridge.collector.time.monotonic", return_value=0.01):
            collector._timer_callback()
        self.assertFalse(batches)
        self.assertIn(object_id, collector._dirty_transforms)

        with patch("blender_bridge.collector.time.monotonic", return_value=0.04):
            collector._timer_callback()
        self.assertEqual(batches[0].transforms, frozenset({object_id}))
        self.assertFalse(collector._dirty_transforms)

    def test_register_adds_bound_method_as_depsgraph_handler(self):
        handlers = SimpleNamespace(depsgraph_update_post=[])
        timers = SimpleNamespace(
            register=lambda _callback, first_interval: None,
            is_registered=lambda _callback: False,
        )
        bpy = SimpleNamespace(app=SimpleNamespace(handlers=handlers, timers=timers))
        collector = ChangeCollector(lambda: [], lambda _batch: None)

        with patch.dict("sys.modules", {"bpy": bpy}):
            collector.register()
            self.assertIn(collector._on_depsgraph_update, handlers.depsgraph_update_post)
            collector.unregister()


if __name__ == "__main__":
    unittest.main()
