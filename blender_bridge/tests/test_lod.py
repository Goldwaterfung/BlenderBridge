import unittest
from types import SimpleNamespace

from blender_bridge.lod import (
    DEFAULT_TRIANGLE_CAPS,
    _apply_evaluated_modifiers,
    candidate_levels_for_baseline,
    validate_lod_set,
    validate_triangle_caps,
)


class EvaluatedMesh:
    def __init__(self, triangle_count, vertex_count=12):
        self.loop_triangles = [object()] * triangle_count
        self.vertices = [object()] * vertex_count

    def calc_loop_triangles(self):
        return None

    def copy(self):
        return CopyableMesh("Evaluated", users=0)


class EvaluatedObject:
    def __init__(self, name, triangle_count, vertex_count=12):
        self.name = name
        self.type = "MESH"
        self.mesh = EvaluatedMesh(triangle_count, vertex_count)
        self.cleared = False

    def evaluated_get(self, _depsgraph):
        return self

    def to_mesh(self, *, preserve_all_data_layers, depsgraph):
        self.asserted_to_mesh = preserve_all_data_layers and depsgraph is not None
        return self.mesh

    def to_mesh_clear(self):
        self.cleared = True


class CopyableMesh:
    def __init__(self, name, users=1):
        self.name = name
        self.users = users
        self.removed = False

    def copy(self):
        return CopyableMesh(self.name, users=0)


class ModifierCollection(list):
    def remove(self, modifier):
        super().remove(modifier)


class ModifierCandidate:
    def __init__(self):
        self.name = "Candidate"
        self._data = None
        self.data = CopyableMesh("Original", users=0)
        self.modifiers = ModifierCollection([object(), object()])
        self.evaluated = EvaluatedObject("Evaluated", 4)

    @property
    def data(self):
        return self._data

    @data.setter
    def data(self, mesh):
        if self._data is not None and self._data is not mesh:
            self._data.users -= 1
        if mesh is not self._data:
            mesh.users += 1
        self._data = mesh

    def evaluated_get(self, _depsgraph):
        return self.evaluated


class Root(EvaluatedObject):
    def __init__(self, **levels):
        super().__init__("Root", 1)
        for level in range(4):
            setattr(self, f"blender_bridge_lod{level}", levels.get(f"lod{level}"))


class LodTests(unittest.TestCase):
    def test_applying_candidate_modifiers_bakes_evaluated_mesh_and_removes_stack(self):
        candidate = ModifierCandidate()
        original_mesh = candidate.data
        removed_meshes = []
        bpy = SimpleNamespace(
            data=SimpleNamespace(meshes=SimpleNamespace(remove=removed_meshes.append)),
            context=SimpleNamespace(view_layer=SimpleNamespace(update=lambda: None)),
        )

        _apply_evaluated_modifiers(candidate, object(), bpy)

        self.assertEqual(candidate.data.name, "Candidate_Mesh")
        self.assertIsNot(candidate.data, original_mesh)
        self.assertEqual(candidate.modifiers, [])
        self.assertIs(candidate.evaluated.cleared, True)
        self.assertEqual(removed_meshes, [original_mesh])

    def test_caps_must_be_positive_integer_and_strictly_descending(self):
        self.assertEqual(validate_triangle_caps(DEFAULT_TRIANGLE_CAPS), DEFAULT_TRIANGLE_CAPS)
        for caps in (
            (0, 30_000, 10_000, 5_000),
            (100_000, 30_000, 30_000, 5_000),
            (100_000, 30_000, 10_000),
            (100_000, 30_000, 10_000, 5.5),
        ):
            with self.subTest(caps=caps):
                with self.assertRaises(ValueError):
                    validate_triangle_caps(caps)

    def test_candidate_bands_skip_caps_that_already_fit_source(self):
        cases = (
            (4_000, []),
            (8_000, [3]),
            (20_000, [2, 3]),
            (80_000, [1, 2, 3]),
            (150_000, [0, 1, 2, 3]),
        )
        for baseline, expected in cases:
            with self.subTest(baseline=baseline):
                self.assertEqual(
                    candidate_levels_for_baseline(baseline, DEFAULT_TRIANGLE_CAPS),
                    expected,
                )

    def test_assigned_levels_are_evaluated_and_meshes_released(self):
        lod0 = EvaluatedObject("LOD0", 80_000)
        lod2 = EvaluatedObject("LOD2", 8_000)
        root = Root(lod0=lod0, lod2=lod2)

        records = validate_lod_set(root, DEFAULT_TRIANGLE_CAPS, object())

        self.assertEqual(
            [(record["level"], record["triangles"], record["vertices"]) for record in records],
            [(0, 80_000, 12), (2, 8_000, 12)],
        )
        self.assertTrue(lod0.cleared)
        self.assertTrue(lod2.cleared)

    def test_assigned_level_cap_duplicate_and_order_are_rejected(self):
        with self.subTest(reason="cap"):
            with self.assertRaisesRegex(ValueError, "cap is 5000"):
                validate_lod_set(
                    Root(lod3=EvaluatedObject("TooDense", 5_001)),
                    DEFAULT_TRIANGLE_CAPS,
                    object(),
                )
        with self.subTest(reason="duplicate"):
            shared = EvaluatedObject("Shared", 20_000)
            with self.assertRaisesRegex(ValueError, "more than one level"):
                validate_lod_set(Root(lod0=shared, lod1=shared), DEFAULT_TRIANGLE_CAPS, object())
        with self.subTest(reason="order"):
            with self.assertRaisesRegex(ValueError, "not lower"):
                validate_lod_set(
                    Root(lod0=EvaluatedObject("LOD0", 20_000), lod1=EvaluatedObject("LOD1", 25_000)),
                    DEFAULT_TRIANGLE_CAPS,
                    object(),
                )

    def test_empty_assignment_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "no assigned levels"):
            validate_lod_set(Root(), DEFAULT_TRIANGLE_CAPS, SimpleNamespace())


if __name__ == "__main__":
    unittest.main()
