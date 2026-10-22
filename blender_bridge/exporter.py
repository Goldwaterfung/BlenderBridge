"""FBX bake exporter for the Blender-to-Unity static-mesh workflow."""

from __future__ import annotations

import hashlib
import logging
import re
from pathlib import Path, PureWindowsPath
from typing import Any

from .convention import resolve_export_scope

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

    objects = resolve_export_scope(scene, context, scope)
    mesh_objects = [obj for obj in objects if getattr(obj, "type", None) == "MESH"]
    if not mesh_objects:
        raise ValueError("The selected export scope contains no static mesh objects.")
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
    export_name = _export_name(scene, context, mesh_objects, scope, file_name=file_name)
    fbx_path = output_folder / f"{export_name}.fbx"

    staged: list[tuple[Any, str]] = []
    used_names: dict[str, str] = {}
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
            staged.append((image, original_path))

        selected_before = list(context.selected_objects)
        active_before = context.view_layer.objects.active
        try:
            for obj in context.scene.objects:
                obj.select_set(False)
            for obj in objects:
                obj.select_set(True)
            context.view_layer.objects.active = mesh_objects[0]
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
            for obj in context.scene.objects:
                obj.select_set(False)
            for obj in selected_before:
                obj.select_set(True)
            context.view_layer.objects.active = active_before
    finally:
        for image, original_path in staged:
            image.filepath = original_path
    log.info("Exported Blender bake to %s.", fbx_path)
    return fbx_path