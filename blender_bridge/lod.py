"""LOD assignment validation and persistent candidate generation."""

from __future__ import annotations

import uuid
from typing import Any

from .convention import get_bridge_uuid


LOD_PROPERTY_NAMES = tuple(f"blender_bridge_lod{level}" for level in range(4))
GENERATED_SOURCE_PROPERTY = "blender_bridge_lod_source_uuid"
GENERATED_LEVEL_PROPERTY = "blender_bridge_lod_level"
GENERATED_MARKER_PROPERTY = "blender_bridge_generated_lod"
DEFAULT_TRIANGLE_CAPS = (100_000, 30_000, 10_000, 5_000)
MAX_DECIMATE_EVALUATIONS = 16
RATIO_INTERVAL_EPSILON = 1e-4


def validate_triangle_caps(caps: Any) -> tuple[int, int, int, int]:
    values = tuple(caps)
    if len(values) != 4:
        raise ValueError("Exactly four LOD triangle caps are required.")
    for level, value in enumerate(values):
        if isinstance(value, bool) or int(value) != value or int(value) <= 0:
            raise ValueError(f"LOD{level} triangle cap must be a positive integer.")
    normalized = tuple(int(value) for value in values)
    if not all(normalized[index] > normalized[index + 1] for index in range(3)):
        raise ValueError("LOD triangle caps must be strictly descending: LOD0 > LOD1 > LOD2 > LOD3.")
    return normalized


def count_evaluated_mesh(obj: Any, depsgraph: Any) -> tuple[int, int]:
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    if mesh is None:
        raise RuntimeError(f"Could not evaluate mesh object {obj.name!r}.")
    try:
        mesh.calc_loop_triangles()
        return len(mesh.loop_triangles), len(mesh.vertices)
    finally:
        evaluated.to_mesh_clear()


def assigned_lod_objects(root: Any) -> tuple[Any | None, ...]:
    return tuple(getattr(root, name, None) for name in LOD_PROPERTY_NAMES)


def candidate_levels_for_baseline(baseline: int, caps: Any) -> list[int]:
    limits = validate_triangle_caps(caps)
    if baseline <= 0:
        raise ValueError("The evaluated source mesh must contain at least one triangle.")
    return [level for level, cap in enumerate(limits) if cap < baseline]


def validate_lod_set(root: Any, caps: Any, depsgraph: Any) -> list[dict[str, Any]]:
    limits = validate_triangle_caps(caps)
    if getattr(root, "type", None) != "MESH":
        raise ValueError("An LOD set source/root must be a mesh object.")

    assigned = assigned_lod_objects(root)
    if not any(obj is not None for obj in assigned):
        raise ValueError(f"LOD set {root.name!r} has no assigned levels.")

    records: list[dict[str, Any]] = []
    seen: set[int] = set()
    previous_triangles: int | None = None
    for level, obj in enumerate(assigned):
        if obj is None:
            continue
        if getattr(obj, "type", None) != "MESH":
            raise ValueError(
                f"LOD set {root.name!r} assigns {obj.name!r} to LOD{level}, but it is not a mesh."
            )
        identity = int(obj.as_pointer()) if callable(getattr(obj, "as_pointer", None)) else id(obj)
        if identity in seen:
            raise ValueError(f"Object {obj.name!r} is assigned to more than one level in LOD set {root.name!r}.")
        seen.add(identity)
        try:
            triangles, vertices = count_evaluated_mesh(obj, depsgraph)
        except (AttributeError, RuntimeError, TypeError, ValueError) as exc:
            raise RuntimeError(
                f"Could not validate {obj.name!r} for LOD{level} in LOD set {root.name!r}: {exc}"
            ) from exc
        cap = limits[level]
        if triangles <= 0 or vertices <= 0:
            raise ValueError(
                f"LOD set {root.name!r} assigned object {obj.name!r} for LOD{level} is empty "
                f"(triangles={triangles}, vertices={vertices})."
            )
        if triangles > cap:
            raise ValueError(
                f"LOD set {root.name!r}, assigned object {obj.name!r} for LOD{level} has "
                f"{triangles} triangles; "
                f"the cap is {cap}. Assign a lower-density mesh."
            )
        if previous_triangles is not None and triangles >= previous_triangles:
            raise ValueError(
                f"LOD set {root.name!r}, LOD{level} object {obj.name!r} has {triangles} "
                "triangles, which is not lower "
                "than the preceding assigned level. Assign strictly decreasing triangle counts."
            )
        previous_triangles = triangles
        records.append(
            {
                "level": level,
                "object": obj,
                "triangles": triangles,
                "vertices": vertices,
                "cap": cap,
            }
        )
    return records


def _link_candidate(candidate: Any, root: Any, scene: Any) -> None:
    collections = tuple(getattr(root, "users_collection", ()))
    if collections:
        for collection in collections:
            collection.objects.link(candidate)
    else:
        scene.collection.objects.link(candidate)


def _remove_object(obj: Any, bpy: Any) -> None:
    mesh = getattr(obj, "data", None)
    bpy.data.objects.remove(obj, do_unlink=True)
    if mesh is not None and getattr(mesh, "users", 1) == 0:
        bpy.data.meshes.remove(mesh)


def _is_assigned(obj: Any, bpy: Any) -> bool:
    return any(
        assigned is obj
        for owner in bpy.data.objects
        for assigned in assigned_lod_objects(owner)
    )


def _apply_evaluated_modifiers(candidate: Any, depsgraph: Any, bpy: Any) -> None:
    evaluated = candidate.evaluated_get(depsgraph)
    evaluated_mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    if evaluated_mesh is None:
        raise RuntimeError(f"Could not bake the evaluated modifiers on {candidate.name!r}.")
    try:
        baked_mesh = evaluated_mesh.copy()
    finally:
        evaluated.to_mesh_clear()

    if baked_mesh is None:
        raise RuntimeError(f"Could not copy the evaluated mesh for {candidate.name!r}.")

    baked_mesh.name = f"{candidate.name}_Mesh"
    original_mesh = candidate.data
    try:
        for modifier in reversed(tuple(candidate.modifiers)):
            candidate.modifiers.remove(modifier)
        candidate.data = baked_mesh
    finally:
        if getattr(baked_mesh, "users", 1) == 0:
            bpy.data.meshes.remove(baked_mesh)

    if original_mesh is not baked_mesh and getattr(original_mesh, "users", 1) == 0:
        bpy.data.meshes.remove(original_mesh)
    bpy.context.view_layer.update()


def generate_lod_candidates(root: Any, scene: Any, caps: Any, depsgraph: Any) -> list[Any]:
    """Create an atomic set of persistent candidates without changing assignments."""
    import bpy

    limits = validate_triangle_caps(caps)
    if getattr(root, "type", None) != "MESH":
        raise ValueError("Select a source/root mesh before generating LOD candidates.")
    baseline, _ = count_evaluated_mesh(root, depsgraph)
    if baseline <= 0:
        raise ValueError(f"Source mesh {root.name!r} evaluates to an empty mesh.")

    levels = candidate_levels_for_baseline(baseline, limits)
    source_uuid = str(get_bridge_uuid(root))
    previous_candidates = [
        obj
        for obj in bpy.data.objects
        if obj.get(GENERATED_MARKER_PROPERTY, False)
        and obj.get(GENERATED_SOURCE_PROPERTY) == source_uuid
        and not _is_assigned(obj, bpy)
    ]
    created: list[Any] = []
    try:
        for level in levels:
            candidate = root.copy()
            created.append(candidate)
            candidate.data = root.data.copy()
            candidate.name = f"{root.name}_LOD{level}_Auto"
            candidate["bridge_uuid"] = str(uuid.uuid4())
            for property_name in LOD_PROPERTY_NAMES:
                if hasattr(candidate, property_name):
                    setattr(candidate, property_name, None)
            _link_candidate(candidate, root, scene)
            modifier = candidate.modifiers.new(name="Blender Bridge LOD Decimate", type="DECIMATE")
            modifier.decimate_type = "COLLAPSE"
            cap = limits[level]
            low = 0.0
            high = 1.0
            ratio = min(1.0, max(0.0, cap / baseline))
            best_ratio = None
            for _ in range(MAX_DECIMATE_EVALUATIONS):
                modifier.ratio = ratio
                bpy.context.view_layer.update()
                measured, _ = count_evaluated_mesh(candidate, depsgraph)
                if 0 < measured <= cap:
                    best_ratio = ratio
                    low = ratio
                else:
                    high = ratio
                if high - low <= RATIO_INTERVAL_EPSILON:
                    break
                next_ratio = (low + high) * 0.5
                if next_ratio <= 0.0 or next_ratio >= 1.0:
                    break
                ratio = next_ratio
            if best_ratio is None:
                raise RuntimeError(
                    f"Could not generate a non-empty LOD{level} candidate for {root.name!r} "
                    f"within {cap} triangles (last measured count: {measured})."
                )
            modifier.ratio = best_ratio
            bpy.context.view_layer.update()
            final_count, _ = count_evaluated_mesh(candidate, depsgraph)
            if final_count <= 0 or final_count > cap:
                raise RuntimeError(
                    f"LOD{level} candidate for {root.name!r} changed during final validation: "
                    f"{final_count} triangles; cap {cap}."
                )
            _apply_evaluated_modifiers(candidate, depsgraph, bpy)
            baked_count, _ = count_evaluated_mesh(candidate, depsgraph)
            if baked_count != final_count:
                raise RuntimeError(
                    f"Applying the evaluated modifier stack changed LOD{level} for {root.name!r}: "
                    f"{final_count} triangles before baking, {baked_count} after."
                )
            candidate[GENERATED_MARKER_PROPERTY] = True
            candidate[GENERATED_SOURCE_PROPERTY] = source_uuid
            candidate[GENERATED_LEVEL_PROPERTY] = level
            candidate["blender_bridge_lod_triangle_count"] = baked_count
            candidate["blender_bridge_lod_decimate_ratio"] = best_ratio

        for candidate in previous_candidates:
            _remove_object(candidate, bpy)
        for candidate in created:
            level = candidate.get(GENERATED_LEVEL_PROPERTY)
            candidate.name = f"{root.name}_LOD{level}_Auto"
        return created
    except Exception:
        for candidate in reversed(created):
            if candidate.name in bpy.data.objects:
                _remove_object(candidate, bpy)
        raise
