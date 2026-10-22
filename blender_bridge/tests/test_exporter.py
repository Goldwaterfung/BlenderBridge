import unittest

import sys
import tempfile
from types import SimpleNamespace
from pathlib import Path
from unittest.mock import patch

from blender_bridge.exporter import (
    _assets_output_folder,
    _export_name,
    _lod_manifest_payload,
    export_fbx,
    safe_asset_name,
)


class FakeMesh(dict):
    name = "Mesh"
    name_full = name
    type = "MESH"
    parent = None
    modifiers = []
    animation_data = None
    data = SimpleNamespace(shape_keys=None)
    material_slots = []

    def select_set(self, selected):
        self.selected = selected


class ExporterTests(unittest.TestCase):
    def test_filename_sanitization_handles_illegal_and_reserved_names(self):
        self.assertEqual(safe_asset_name('Bad/Name:*?'), "Bad_Name___")
        self.assertEqual(safe_asset_name("CON"), "CON_")
        self.assertEqual(safe_asset_name("..."), "BlenderAsset")

    def test_lod_manifest_records_exact_sparse_level_contract(self):
        payload = _lod_manifest_payload(
            "Tree.fbx",
            [
                {
                    "sourceName": "Tree",
                    "sourceId": "b98f1937-4364-44e4-9119-7ff0521e1269",
                    "groupName": "Tree_LODGroup",
                    "levels": [
                        {
                            "level": 0,
                            "nodeName": "Tree_LODGroup_LOD0",
                            "meshName": "Tree_LOD0_Mesh",
                            "assignedObjectName": "TreeHigh",
                            "triangleCap": 100_000,
                            "triangleCount": 80_000,
                            "vertexCount": 42_000,
                        },
                        {
                            "level": 2,
                            "nodeName": "Tree_LODGroup_LOD2",
                            "meshName": "Tree_LOD2_Mesh",
                            "assignedObjectName": "TreeLow",
                            "triangleCap": 10_000,
                            "triangleCount": 8_000,
                            "vertexCount": 4_200,
                        },
                    ],
                }
            ],
        )

        self.assertEqual(payload["schemaVersion"], 1)
        self.assertEqual(payload["fbxFile"], "Tree.fbx")
        self.assertEqual(payload["coordinateSpace"], "source-root-local")
        self.assertEqual(
            [level["nodeName"] for level in payload["lodSets"][0]["levels"]],
            ["Tree_LODGroup_LOD0", "Tree_LODGroup_LOD2"],
        )
        self.assertEqual(payload["lodSets"][0]["levels"][1]["triangleCap"], 10_000)

    def test_export_name_uses_object_or_scope_name(self):
        obj = SimpleNamespace(name="Prop")
        context = SimpleNamespace(
            view_layer=SimpleNamespace(
                active_layer_collection=SimpleNamespace(collection=SimpleNamespace(name="Props")),
                objects=SimpleNamespace(active=obj),
            )
        )
        scene = SimpleNamespace(name="Scene")
        self.assertEqual(_export_name(scene, context, [obj], "SCENE"), "Prop")
        self.assertEqual(_export_name(scene, context, [obj, obj], "COLLECTION"), "Props")
        self.assertEqual(_export_name(scene, context, [obj, obj], "SCENE"), "Scene")

    def test_export_name_uses_custom_file_name_when_provided(self):
        obj = SimpleNamespace(name="Prop")
        context = SimpleNamespace(
            view_layer=SimpleNamespace(
                active_layer_collection=SimpleNamespace(collection=SimpleNamespace(name="Props")),
                objects=SimpleNamespace(active=obj),
            )
        )
        scene = SimpleNamespace(name="Scene")
        self.assertEqual(_export_name(scene, context, [obj], "SCENE", file_name="CustomProp"), "CustomProp")
        self.assertEqual(_export_name(scene, context, [obj, obj], "SCENE", file_name="CustomProp.fbx"), "CustomProp")
        self.assertEqual(_export_name(scene, context, [obj, obj], "SCENE", file_name="  CustomProp.FBX  "), "CustomProp")
        self.assertEqual(_export_name(scene, context, [obj, obj], "SCENE", file_name="   "), "Scene")
        self.assertEqual(_export_name(scene, context, [obj], "SCENE", file_name="Bad/Name:*?"), "Bad_Name___")

    def test_export_scales_fbx_for_unity(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            assets = root / "Assets"
            assets.mkdir()
            blend_file = root / "scene.blend"
            blend_file.touch()

            mesh = FakeMesh()
            mesh["bridge_uuid"] = "b98f1937-4364-44e4-9119-7ff0521e1269"
            scene = SimpleNamespace(objects=[mesh], name="Scene")
            context = SimpleNamespace(
                scene=scene,
                selected_objects=[],
                view_layer=SimpleNamespace(
                    objects=SimpleNamespace(active=mesh),
                    active_layer_collection=None,
                ),
            )
            export_options = {}

            def export_scene_fbx(**kwargs):
                export_options.update(kwargs)
                Path(kwargs["filepath"]).touch()
                return {"FINISHED"}

            bpy = SimpleNamespace(
                data=SimpleNamespace(filepath=str(blend_file)),
                path=SimpleNamespace(abspath=lambda path, library=None: path),
                ops=SimpleNamespace(
                    export_scene=SimpleNamespace(fbx=export_scene_fbx)
                ),
            )

            with patch.dict(sys.modules, {"bpy": bpy}):
                fbx_path = export_fbx(
                    scene=scene,
                    context=context,
                    project_root=root,
                    output_path="Art/Blender/Props",
                    scope="SCENE",
                )

            self.assertEqual(export_options["global_scale"], 0.01)
            self.assertEqual(
                fbx_path,
                assets / "Art" / "Blender" / "Props" / "Mesh.fbx",
            )

    def test_export_uses_custom_file_name(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            assets = root / "Assets"
            assets.mkdir()
            blend_file = root / "scene.blend"
            blend_file.touch()

            mesh = FakeMesh()
            mesh["bridge_uuid"] = "b98f1937-4364-44e4-9119-7ff0521e1269"
            scene = SimpleNamespace(objects=[mesh], name="Scene", blender_bridge_file_name="ConfiguredAsset")
            context = SimpleNamespace(
                scene=scene,
                selected_objects=[],
                view_layer=SimpleNamespace(
                    objects=SimpleNamespace(active=mesh),
                    active_layer_collection=None,
                ),
            )

            def export_scene_fbx(**kwargs):
                Path(kwargs["filepath"]).touch()
                return {"FINISHED"}

            bpy = SimpleNamespace(
                data=SimpleNamespace(filepath=str(blend_file)),
                path=SimpleNamespace(abspath=lambda path, library=None: path),
                ops=SimpleNamespace(
                    export_scene=SimpleNamespace(fbx=export_scene_fbx)
                ),
            )

            with patch.dict(sys.modules, {"bpy": bpy}):
                fbx_path = export_fbx(
                    scene=scene,
                    context=context,
                    project_root=root,
                    output_path="Art/Blender/Props",
                    scope="SCENE",
                )

            self.assertEqual(
                fbx_path,
                assets / "Art" / "Blender" / "Props" / "ConfiguredAsset.fbx",
            )

    def test_export_includes_empty_parents_for_mesh_transforms(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            (root / "Assets").mkdir()
            blend_file = root / "scene.blend"
            blend_file.touch()

            parent = FakeMesh()
            parent.name = "Parent"
            parent.type = "EMPTY"
            child = FakeMesh()
            child.parent = parent
            scene = SimpleNamespace(objects=[parent, child], name="Scene")
            context = SimpleNamespace(
                scene=scene,
                selected_objects=[],
                view_layer=SimpleNamespace(
                    objects=SimpleNamespace(active=child),
                    active_layer_collection=None,
                ),
            )
            export_options = {}
            selected_during_export = []

            def export_scene_fbx(**kwargs):
                export_options.update(kwargs)
                selected_during_export.extend(
                    obj.name for obj in scene.objects if obj.selected
                )
                Path(kwargs["filepath"]).touch()
                return {"FINISHED"}

            bpy = SimpleNamespace(
                data=SimpleNamespace(filepath=str(blend_file)),
                path=SimpleNamespace(abspath=lambda path, library=None: path),
                ops=SimpleNamespace(
                    export_scene=SimpleNamespace(fbx=export_scene_fbx)
                ),
            )

            with patch.dict(sys.modules, {"bpy": bpy}):
                export_fbx(
                    scene=scene,
                    context=context,
                    project_root=root,
                    output_path="Art",
                    scope="SCENE",
                )

            self.assertEqual(set(selected_during_export), {"Parent", "Mesh"})
            self.assertEqual(export_options["object_types"], {"MESH", "EMPTY"})

    def test_export_path_must_remain_relative_to_assets(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            assets = Path(temp_dir) / "Assets"
            assets.mkdir()
            self.assertEqual(
                _assets_output_folder(assets, "my_fbx"),
                assets / "my_fbx",
            )
            for unsafe_path in ("../outside", "C:/outside", "/outside", "Art//Models"):
                with self.subTest(path=unsafe_path):
                    with self.assertRaises(ValueError):
                        _assets_output_folder(assets, unsafe_path)


if __name__ == "__main__":
    unittest.main()
