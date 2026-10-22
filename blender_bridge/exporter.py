"""FBX bake exporter for the Blender-to-Unity static-mesh workflow."""

from __future__ import annotations

import hashlib
import json
import logging
import re
from pathlib import Path, PureWindowsPath
from typing import Any

from .convention import resolve_export_scope, validate_no_shear
from .lod import (
    DEFAULT_TRIANGLE_CAPS,
    GENERATED_MARKER_PROPERTY,
    assigned_lod_objects,
    validate_lod_set,
    validate_triangle_caps,
)

log = logging.getLogger("blender_bridge")
_ILLEGAL_FILENAME = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def safe_asset_name(name: str) -> str:
    cleaned = _ILLEGAL_FILENAME.sub("_", name).strip().strip(". ")
    if not cleaned:
        return "BlenderAsset"
    if cleaned.upper() in {"CON", "PRN", "AUX", "NUL"} or re.fullmatch(
        r"(COM|LPT)[1-9]", cleaned.upper()
    ):
        cleaned += "_"
    return cleaned


def _unity_assets_folder(project_root: str | Path) -> Path:
    root = Path(project_root).expanduser().resolve()
    assets = root / "Assets"
    if not root.is_dir() or not assets.is_dir():
        raise ValueError("Unity project path must point to a project containing an Assets folder.")
    return assets


def _assets_output_folder(assets_folder: Path, output_path: str) -> Path:
    normalized_path = output_path.strip().replace("\\", "/")
    windows_path = PureWindowsPath(normalized_path)
    segments = normalized_path.split("/")
    if (
        not normalized_path
        or normalized_path.startswith("/")
        or windows_path.drive
        or any(
            segment in {"", ".", ".."} or safe_asset_name(segment) != segment
            for segment in segments
        )
    ):
        raise ValueError(
            "Output path must be a valid relative folder path below the Unity project's Assets folder."
        )

    output_folder = assets_folder.joinpath(*segments)
    try:
        output_folder.resolve().relative_to(assets_folder.resolve())
    except ValueError as exc:
        raise ValueError("Output path must remain inside the Unity project's Assets folder.") from exc
    return output_folder


def _material_images(objects: list[Any]) -> list[Any]:
    images: dict[int, Any] = {}
    for obj in objects:
        if getattr(obj, "type", None) != "MESH":
            continue
        for slot in obj.material_slots:
            material = slot.material
            tree = getattr(material, "node_tree", None) if material else None
            if tree is None:
                continue
            for node in tree.nodes:
                image = getattr(node, "image", None)
                if image is not None:
                    images[id(image)] = image
    return list(images.values())


def _export_name(scene: Any, context: Any, objects: list[Any], scope: str, file_name: str = "") -> str:
    cleaned = file_name.strip()
    if cleaned.lower().endswith(".fbx"):
        cleaned = cleaned[:-4].strip()
    if cleaned:
        return safe_asset_name(cleaned)

    if len(objects) == 1:
        name = objects[0].name
    elif scope == "COLLECTION":
        layer_collection = getattr(context.view_layer, "active_layer_collection", None)
        name = getattr(getattr(layer_collection, "collection", None), "name", "Collection")
    elif scope == "SCENE":
        name = scene.name
    else:
        active = getattr(context.view_layer.objects, "active", None)
        name = getattr(active, "name", "Selection") + "_Selection"
    return safe_asset_name(name)


def _stage_image(
    image: Any,
    texture_folder: Path,
    bpy: Any,
    filename: str | None = None,
) -> tuple[Path, str]:
    original_path = str(image.filepath)
    source_path = ""
    if original_path:
        source_path = bpy.path.abspath(original_path, library=getattr(image, "library", None))
    filename = filename or Path(original_path).name or f"{safe_asset_name(image.name)}.png"
    candidate = texture_folder / safe_asset_name(filename)
    if source_path and Path(source_path).is_file():
        source = Path(source_path).resolve()
        if source != candidate.resolve():
            candidate.parent.mkdir(parents=True, exist_ok=True)
            try:
                candidate.write_bytes(source.read_bytes())
            except OSError as exc:
                raise OSError(f"Could not stage texture {source}: {exc}") from exc
    else:
        candidate.parent.mkdir(parents=True, exist_ok=True)
        try:
            image.filepath = str(candidate)
            image.save()
        except (RuntimeError, OSError, TypeError) as exc:
            raise ValueError(
                f"Texture {image.name!r} has no readable source and could not be saved to the bake folder."
            ) from exc
        finally:
            image.filepath = original_path
        if not candidate.is_file():
            raise ValueError(f"Blender did not create the staged texture {candidate}.")
    return candidate, original_path


def _lod_sets(
    objects: list[Any],
    context: Any,
    caps: tuple[int, int, int, int],
) -> tuple[list[dict[str, Any]], set[int], list[Any]]:
    roots: list[dict[str, Any]] = []
    root_ids: set[int] = set()
    assigned_objects: list[Any] = []
    lod_roots = [
        root
        for root in objects
        if getattr(root, "type", None) == "MESH"
        and any(obj is not None for obj in assigned_lod_objects(root))
    ]
    if not lod_roots:
        return roots, set(), assigned_objects
    depsgraph = context.evaluated_depsgraph_get()
    used_group_names: set[str] = set()
    for root in lod_roots:
        records = validate_lod_set(root, caps, depsgraph)
        group_name = f"{safe_asset_name(root.name)}_LODGroup"
        if group_name.casefold() in used_group_names:
            raise ValueError(
                f"LOD source names collide after FBX-safe naming at {group_name!r}; "
                "rename one source object before export."
            )
        used_group_names.add(group_name.casefold())
        try:
            validate_no_shear(root.matrix_world)
            root_inverse = root.matrix_world.inverted()
            for record in records:
                validate_no_shear(root_inverse @ record["object"].matrix_world)
        except (AttributeError, RuntimeError, ValueError) as exc:
            raise ValueError(
                f"LOD set {root.name!r} has a transform that cannot be represented "
                f"relative to its source/root: {exc}"
            ) from exc
        root_ids.add(id(root))
        levels = []
        for record in records:
            obj = record["object"]
            assigned_objects.append(obj)
            levels.append(
                {
                    "level": record["level"],
                    "nodeName": f"{group_name}_LOD{record['level']}",
                    "meshName": f"{safe_asset_name(root.name)}_LOD{record['level']}_Mesh",
                    "assignedObjectName": obj.name,
                    "triangleCap": record["cap"],
                    "triangleCount": record["triangles"],
                    "vertexCount": record["vertices"],
                }
            )
        roots.append(
            {
                "sourceName": root.name,
                "sourceId": str(root.get("bridge_uuid", "")),
                "groupName": group_name,
                "root": root,
                "levels": levels,
            }
        )
    assigned_ids = {id(obj) for obj in assigned_objects}
    return roots, root_ids | assigned_ids, assigned_objects


def _is_generated_lod_candidate(obj: Any) -> bool:
    get_property = getattr(obj, "get", None)
    return bool(get_property(GENERATED_MARKER_PROPERTY, False)) if callable(get_property) else False


def _create_lod_staging(bpy: Any, scene: Any, lod_sets: list[dict[str, Any]]) -> tuple[Any, list[Any]]:
    from mathutils import Matrix

    collection = bpy.data.collections.new("BlenderBridge_LOD_Export")
    staged: list[Any] = []
    identity = Matrix.Identity(4)

    def new_empty(name: str, parent: Any = None) -> Any:
        obj = bpy.data.objects.new(name, None)
        staged.append(obj)
        obj.empty_display_type = "PLAIN_AXES"
        collection.objects.link(obj)
        if parent is not None:
            obj.parent = parent
            obj.matrix_parent_inverse = identity
        return obj

    try:
        scene.collection.children.link(collection)
        for lod_set in lod_sets:
            source = lod_set["root"]
            group = new_empty(lod_set["groupName"])
            group.matrix_world = source.matrix_world.copy()
            staged_levels = []
            for level_record in lod_set["levels"]:
                level = new_empty(level_record["nodeName"], group)
                staged_levels.append((level_record, level))

            bpy.context.view_layer.update()

            for level_record, level in staged_levels:
                for record in _records_for_lod_set(lod_set):
                    if record["level"] != level_record["level"]:
                        continue
                    assigned = record["object"]
                    world_matrix = assigned.matrix_world.copy()
                    mesh_object = assigned.copy()
                    staged.append(mesh_object)
                    mesh_object.data = assigned.data.copy()
                    mesh_object.name = level_record["meshName"]
                    collection.objects.link(mesh_object)
                    mesh_object.parent = level
                    mesh_object.matrix_parent_inverse = identity
                    mesh_object.matrix_world = world_matrix
                    break
        return collection, staged
    except Exception:
        _remove_lod_staging(bpy, collection, staged)
        raise


def _records_for_lod_set(lod_set: dict[str, Any]) -> list[dict[str, Any]]:
    return lod_set["records"]


def _validate_staging_names(bpy: Any, lod_sets: list[dict[str, Any]]) -> None:
    existing_names = {obj.name for obj in bpy.data.objects}
    staging_names: set[str] = set()
    for lod_set in lod_sets:
        names = [lod_set["groupName"]]
        for level in lod_set["levels"]:
            names.extend((level["nodeName"], level["meshName"]))
        for name in names:
            if name in existing_names or name in staging_names:
                raise ValueError(
                    f"LOD export staging name {name!r} is already in use. Rename the conflicting scene object and retry."
                )
            staging_names.add(name)


def _lod_manifest_payload(fbx_file: str, lod_sets: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "fbxFile": fbx_file,
        "coordinateSpace": "source-root-local",
        "lodSets": [
            {
                "sourceName": item["sourceName"],
                "sourceId": item["sourceId"],
                "groupName": item["groupName"],
                "levels": item["levels"],
            }
            for item in lod_sets
        ],
    }


def _remove_lod_staging(bpy: Any, collection: Any, staged: list[Any]) -> None:
    for obj in reversed(staged):
        mesh = getattr(obj, "data", None)
        if obj.name in bpy.data.objects:
            bpy.data.objects.remove(obj, do_unlink=True)
        if mesh is not None and getattr(mesh, "users", 1) == 0:
            bpy.data.meshes.remove(mesh)
    if collection is not None and collection.name in bpy.data.collections:
        bpy.data.collections.remove(collection)


def export_fbx(
    *,
    scene: Any,
    context: Any,
    project_root: str | Path,
    output_path: str,
    scope: str,
    file_name: str = "",
) -> Path:
    """Stage texture dependencies and export a scoped static-mesh FBX."""
    import bpy

    if not bpy.data.filepath or not Path(bpy.data.filepath).is_file():
        raise ValueError("Save the Blender file before baking to Unity.")
    if not output_path.strip():
        raise ValueError("Enter a relative output path under the Unity project's Assets folder.")

    caps = validate_triangle_caps(
        tuple(
            getattr(scene, f"blender_bridge_lod{level}_triangle_cap", DEFAULT_TRIANGLE_CAPS[level])
            for level in range(4)
        )
    )
    objects = resolve_export_scope(scene, context, scope)
    scoped_meshes = [obj for obj in objects if getattr(obj, "type", None) == "MESH"]
    if not scoped_meshes:
        raise ValueError("The selected export scope contains no static mesh objects.")
    lod_sets, excluded_ids, assigned_objects = _lod_sets(objects, context, caps)
    regular_meshes = [
        obj
        for obj in scoped_meshes
        if id(obj) not in excluded_ids
        and not _is_generated_lod_candidate(obj)
    ]
    mesh_objects = regular_meshes + assigned_objects
    if not mesh_objects:
        raise ValueError("The selected export scope contains no static mesh objects or assigned LOD levels.")
    unsupported = [
        obj
        for obj in mesh_objects
        if any(
            modifier.type in {"ARMATURE", "CURVE", "PARTICLE_SYSTEM"}
            for modifier in obj.modifiers
        )
        or (getattr(obj, "parent", None) is not None and obj.parent.type == "ARMATURE")
        or getattr(obj, "animation_data", None) is not None
        or getattr(getattr(obj, "data", None), "shape_keys", None) is not None
    ]
    if unsupported:
        names = ", ".join(obj.name for obj in unsupported)
        raise ValueError(
            f"Rigged, animated, shape-key, curve-modified, or particle meshes are outside the "
            f"Blender Bridge v1 bake scope: {names}. "
            "Export a static-mesh copy."
        )
    assets_folder = _unity_assets_folder(project_root)
    output_folder = _assets_output_folder(assets_folder, output_path)
    texture_folder = output_folder / "Textures"
    output_folder.mkdir(parents=True, exist_ok=True)
    texture_folder.mkdir(parents=True, exist_ok=True)
    if not file_name:
        file_name = getattr(scene, "blender_bridge_file_name", "")
    if not file_name and not regular_meshes and len(lod_sets) == 1:
        export_name = safe_asset_name(lod_sets[0]["sourceName"])
    else:
        export_name = _export_name(scene, context, mesh_objects, scope, file_name=file_name)
    fbx_path = output_folder / f"{export_name}.fbx"

    staged_images: list[tuple[Any, str]] = []
    used_names: dict[str, str] = {}
    staging_collection = None
    staged_lod_objects: list[Any] = []
    manifest_path = fbx_path.with_suffix(f"{fbx_path.suffix}.lod.json")
    try:
        for image in _material_images(mesh_objects):
            original_path = str(image.filepath)
            source_path = bpy.path.abspath(
                original_path,
                library=getattr(image, "library", None),
            ) if original_path else ""
            original_file = str(Path(source_path).resolve()) if source_path else f"packed:{image.name}"
            filename = Path(original_path).name or f"{safe_asset_name(image.name)}.png"
            previous_source = used_names.get(filename.casefold())
            if previous_source and previous_source != original_file:
                digest = hashlib.sha256(original_file.encode("utf-8")).hexdigest()[:8]
                source_name = Path(filename)
                filename = f"{source_name.stem}_{digest}{source_name.suffix}"
            used_names[filename.casefold()] = original_file
            staged_path, original_path = _stage_image(image, texture_folder, bpy, filename)
            image.filepath = str(staged_path)
            staged_images.append((image, original_path))

        if lod_sets:
            records_by_root = {
                id(root): validate_lod_set(
                    root,
                    caps,
                    context.evaluated_depsgraph_get(),
                )
                for root in (item["root"] for item in lod_sets)
            }
            for lod_set in lod_sets:
                lod_set["records"] = records_by_root[id(lod_set["root"])]
            _validate_staging_names(bpy, lod_sets)
            staging_collection, staged_lod_objects = _create_lod_staging(
                bpy, scene, lod_sets
            )

        selected_before = list(context.selected_objects)
        active_before = context.view_layer.objects.active
        regular_ids = {id(obj) for obj in regular_meshes}
        regular_export_objects = []
        for obj in objects:
            if id(obj) in regular_ids:
                current = obj
                while current is not None:
                    if current not in regular_export_objects:
                        regular_export_objects.append(current)
                    current = getattr(current, "parent", None)
        export_objects = regular_export_objects + staged_lod_objects
        try:
            for obj in context.scene.objects:
                obj.select_set(False)
            for obj in export_objects:
                obj.select_set(True)
            active_export_object = next(
                (obj for obj in export_objects if getattr(obj, "type", None) == "MESH"),
                None,
            )
            context.view_layer.objects.active = active_export_object
            if manifest_path.exists():
                manifest_path.unlink()
            result = bpy.ops.export_scene.fbx(
                filepath=str(fbx_path),
                use_selection=True,
                use_visible=False,
                use_active_collection=False,
                object_types={"MESH", "EMPTY"},
                use_mesh_modifiers=True,
                use_mesh_modifiers_render=True,
                mesh_smooth_type="OFF",
                colors_type="SRGB",
                prioritize_active_color=True,
                use_tspace=True,
                use_custom_props=True,
                use_triangles=False,
                apply_unit_scale=True,
                apply_scale_options="FBX_SCALE_NONE",
                global_scale=0.01,
                use_space_transform=True,
                axis_forward="-Z",
                axis_up="Y",
                path_mode="RELATIVE",
                embed_textures=False,
                bake_anim=False,
            )
            if "FINISHED" not in result or not fbx_path.is_file():
                raise RuntimeError("Blender's FBX exporter did not finish successfully.")
        finally:
            try:
                for obj in context.scene.objects:
                    obj.select_set(False)
                for obj in selected_before:
                    obj.select_set(True)
            finally:
                context.view_layer.objects.active = active_before
        if lod_sets:
            manifest = _lod_manifest_payload(fbx_path.name, lod_sets)
            manifest_path.write_text(
                json.dumps(manifest, indent=2, sort_keys=True) + "\n",
                encoding="utf-8",
            )
    finally:
        try:
            if staging_collection is not None:
                _remove_lod_staging(bpy, staging_collection, staged_lod_objects)
        finally:
            for image, original_path in staged_images:
                image.filepath = original_path
    log.info("Exported Blender bake to %s.", fbx_path)
    return fbx_path