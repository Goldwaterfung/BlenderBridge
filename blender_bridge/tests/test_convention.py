import unittest
import uuid
from types import SimpleNamespace

from blender_bridge.convention import (
    effective_unit_scale,
    ensure_unique_uuids,
    resolve_export_scope,
    to_unity_dir,
    to_unity_pos,
    to_unity_rot,
    validate_unit_scale,
    validate_no_shear,
)


class FakeObject(dict):
    def __init__(self, name):
        super().__init__()
        self.name = name
        self.name_full = name


class ScopeObject(FakeObject):
    def __init__(self, name, object_type):
        super().__init__(name)
        self.type = object_type
        self.parent = None


class BasisVector:
    def __init__(self, x, y, z):
        self.x, self.y, self.z = x, y, z
        self.length = (x * x + y * y + z * z) ** 0.5

    def __truediv__(self, value):
        return BasisVector(self.x / value, self.y / value, self.z / value)

    def dot(self, other):
        return self.x * other.x + self.y * other.y + self.z * other.z


class Matrix:
    def __init__(self, columns):
        self.col = columns

    def to_3x3(self):
        return self


class ConventionTests(unittest.TestCase):
    def test_axis_conversion_is_the_normative_rotation(self):
        self.assertEqual(to_unity_pos((1, 2, 3), 0.01), (-0.01, 0.03, -0.02))
        self.assertEqual(to_unity_dir((1, 0, 0)), (-1.0, 0.0, 0.0))
        self.assertEqual(to_unity_dir((0, 1, 0)), (0.0, 0.0, -1.0))
        self.assertEqual(to_unity_dir((0, 0, 1)), (0.0, 1.0, 0.0))

    def test_quaternion_is_returned_in_wxyz_order(self):
        self.assertEqual(to_unity_rot((1.0, 0.0, 0.0, 0.0)), (1.0, 0.0, 0.0, 0.0))
        self.assertEqual(
            to_unity_rot((0.7071, 0.0, 0.0, 0.7071)),
            (0.7071, 0.0, -0.7071, 0.0),
        )

    def test_unit_scale_falls_back_for_zero_scene_scale(self):
        scene = type("Scene", (), {"unit_settings": type("Units", (), {"scale_length": 0.0})()})()
        self.assertEqual(effective_unit_scale(scene), 1.0)
        self.assertEqual(effective_unit_scale(scene, 0.25), 0.25)
        with self.assertRaises(ValueError):
            validate_unit_scale(0.0)

    def test_duplicate_and_invalid_uuids_are_repaired(self):
        duplicate = str(uuid.uuid4())
        first = FakeObject("First")
        second = FakeObject("Second")
        malformed = FakeObject("Malformed")
        first["bridge_uuid"] = duplicate
        second["bridge_uuid"] = duplicate
        malformed["bridge_uuid"] = "not-a-uuid"
        inventory = ensure_unique_uuids((first, second, malformed))
        self.assertEqual(len(inventory), 3)
        self.assertEqual(inventory[uuid.UUID(duplicate)], first)
        self.assertEqual(len({obj["bridge_uuid"] for obj in inventory.values()}), 3)

    def test_selected_scope_includes_transform_ancestors(self):
        parent = ScopeObject("Parent", "EMPTY")
        mesh = ScopeObject("Mesh", "MESH")
        mesh.parent = parent
        scene = SimpleNamespace(objects=[parent, mesh])
        context = SimpleNamespace(
            selected_objects=[mesh],
            view_layer=SimpleNamespace(active_layer_collection=None),
        )
        result = resolve_export_scope(scene, context, "SELECTED")
        self.assertEqual(len(result), 2)
        self.assertTrue(any(obj is parent for obj in result))
        self.assertTrue(any(obj is mesh for obj in result))

    def test_shear_is_rejected_but_zero_scale_is_representable(self):
        validate_no_shear(
            Matrix(
                [
                    BasisVector(1, 0, 0),
                    BasisVector(0, 2, 0),
                    BasisVector(0, 0, 0),
                ]
            )
        )
        with self.assertRaisesRegex(ValueError, "shear"):
            validate_no_shear(
                Matrix(
                    [
                        BasisVector(1, 0, 0),
                        BasisVector(1, 1, 0),
                        BasisVector(0, 0, 1),
                    ]
                )
            )


if __name__ == "__main__":
    unittest.main()
