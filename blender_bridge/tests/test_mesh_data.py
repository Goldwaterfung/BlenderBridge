import unittest

from blender_bridge.mesh_data import extract_mesh


class Vector:
    def __init__(self, x, y, z):
        self.x, self.y, self.z = x, y, z


class Loop:
    def __init__(self, vertex_index):
        self.vertex_index = vertex_index
        self.normal = Vector(0, 0, 1)
        self.tangent = Vector(1, 0, 0)
        self.bitangent_sign = -1.0


class Triangle:
    loops = (0, 1, 2)
    material_index = 1


class ColorValue:
    color_srgb = (0.25, 0.5, 1.0, 1.0)


class Attribute:
    domain = "CORNER"
    data = (ColorValue(), ColorValue(), ColorValue())


class UVValue:
    def __init__(self, x, y):
        self.uv = Vector(x, y, 0)


class UVLayer:
    name = "UVMap"
    data = (UVValue(0, 0), UVValue(1, 0), UVValue(0, 1))


class Collection:
    active = UVLayer()
    active_color = Attribute()

    def __iter__(self):
        render_layer = getattr(self, "render_layer", None)
        return iter((self.active, render_layer)) if render_layer is not None else iter((self.active,))

    def get(self, name):
        return next((layer for layer in self if layer.name == name), None)


class FakeMesh:
    loops = (Loop(0), Loop(1), Loop(2))
    vertices = (
        type("Vertex", (), {"co": Vector(0, 0, 0)})(),
        type("Vertex", (), {"co": Vector(1, 0, 0)})(),
        type("Vertex", (), {"co": Vector(0, 1, 0)})(),
    )
    loop_triangles = (Triangle(),)
    uv_layers = Collection()
    color_attributes = Collection()

    def calc_loop_triangles(self):
        pass

    def calc_tangents(self, uvmap):
        self.calculated_uvmap = uvmap


class FakeObject(dict):
    name = "Triangle"

    def __init__(self, mesh):
        super().__init__()
        self["bridge_uuid"] = "9e164fdc-29c5-4ae2-97bc-5bfc81e889cb"
        self.mesh = mesh
        self.cleared = False

    def evaluated_get(self, depsgraph):
        return self

    def to_mesh(self, *, preserve_all_data_layers, depsgraph):
        if preserve_all_data_layers is not True or depsgraph != "deps":
            raise AssertionError("Evaluated mesh extraction omitted required data-layer arguments.")
        return self.mesh

    def to_mesh_clear(self):
        self.cleared = True


class MeshExtractionTests(unittest.TestCase):
    def test_render_active_uv_layer_is_exported_instead_of_ui_active_layer(self):
        mesh = FakeMesh()
        mesh.uv_layers.render_layer = type(
            "RenderUVLayer",
            (),
            {
                "name": "RenderUV",
                "active_render": True,
                "data": (UVValue(0.25, 0.5), UVValue(0.75, 0.5), UVValue(0.25, 1.0)),
            },
        )()

        payload = extract_mesh(FakeObject(mesh), "deps", 1.0)

        self.assertEqual(mesh.calculated_uvmap, "RenderUV")
        self.assertEqual(
            tuple(map(tuple, payload.uvs)),
            ((0.25, 0.5), (0.25, 1.0), (0.75, 0.5)),
        )

    def test_evaluated_mesh_preserves_corner_attributes_and_releases_mesh(self):
        obj = FakeObject(FakeMesh())
        payload = extract_mesh(obj, "deps", 2.0)
        self.assertTrue(obj.cleared)
        self.assertEqual(tuple(map(tuple, payload.positions)), ((0.0, 0.0, 0.0), (0.0, 0.0, -2.0), (-2.0, 0.0, 0.0)))
        self.assertEqual(tuple(map(tuple, payload.normals)), ((0.0, 1.0, 0.0),) * 3)
        self.assertEqual(tuple(map(tuple, payload.tangents)), ((-1.0, 0.0, 0.0, 1.0),) * 3)
        self.assertEqual(tuple(map(tuple, payload.uvs)), ((0.0, 0.0), (0.0, 1.0), (1.0, 0.0)))
        self.assertEqual(tuple(map(tuple, payload.colors)), ((0.25, 0.5, 1.0, 1.0),) * 3)
        self.assertEqual(tuple(payload.indices), (0, 1, 2))
        self.assertEqual(payload.submeshes[0].material_index, 1)

    def test_temporary_mesh_is_cleared_when_extraction_fails(self):
        mesh = FakeMesh()
        mesh.calc_loop_triangles = lambda: (_ for _ in ()).throw(RuntimeError("failed"))
        obj = FakeObject(mesh)
        with self.assertRaisesRegex(RuntimeError, "failed"):
            extract_mesh(obj, "deps", 1.0)
        self.assertTrue(obj.cleared)


if __name__ == "__main__":
    unittest.main()
