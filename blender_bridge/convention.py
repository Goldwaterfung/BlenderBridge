"""Coordinate, unit, identity, and export-scope conventions for the bridge."""

from __future__ import annotations

import math
import uuid
from collections.abc import Iterable, Sequence
from typing import Any


UUID_PROPERTY = "bridge_uuid"
SCOPE_SELECTED = "SELECTED"
SCOPE_COLLECTION = "COLLECTION"
SCOPE_SCENE = "SCENE"
VALID_SCOPES = (SCOPE_SELECTED, SCOPE_COLLECTION, SCOPE_SCENE)
_UUID_CACHE_LIMIT = 100_000
_UUID_CACHE: dict[int, tuple[str, uuid.UUID]] = {}


def _components(value: Any, count: int) -> tuple[float, ...]:
    if hasattr(value, "x"):
        values = (value.x, value.y, value.z, getattr(value, "w", None))
        values = values[:count]
    else:
        values = tuple(value)
    if len(values) != count:
        raise ValueError(f"Expected {count} components, received {len(values)}.")
    result = tuple(float(component) for component in values)
    if not all(math.isfinite(component) for component in result):
        raise ValueError("Bridge values must be finite.")
    return result


def to_unity_pos(value: Any, unit_scale: float = 1.0) -> tuple[float, float, float]:
    """Convert a Blender-space position to Unity space and metres."""
    x, y, z = _components(value, 3)
    scale = validate_unit_scale(unit_scale)
    return -x * scale, z * scale, -y * scale


def to_unity_rot(value: Any) -> tuple[float, float, float, float]:
    """Convert Blender (w, x, y, z) quaternion to Unity-space (w, x, y, z)."""
    if hasattr(value, "w"):
        w, x, y, z = (float(value.w), float(value.x), float(value.y), float(value.z))
    else:
        components = tuple(value)
        if len(components) != 4:
            raise ValueError("A quaternion must have four components.")
        w, x, y, z = (float(component) for component in components)
    if not all(math.isfinite(component) for component in (w, x, y, z)):
        raise ValueError("Bridge values must be finite.")
    return w, x, -z, y


def to_unity_dir(value: Any) -> tuple[float, float, float]:
    """Convert a Blender-space direction, normal, or tangent to Unity space."""
    x, y, z = _components(value, 3)
    return -x, z, -y


def validate_unit_scale(value: float) -> float:
    scale = float(value)
    if not math.isfinite(scale) or scale <= 0.0:
        raise ValueError("The effective unit scale must be finite and positive.")
    return scale


def effective_unit_scale(scene: Any, override: float = 0.0) -> float:
    """Return the explicit override or a safe positive Blender scene scale."""
    override = float(override)
    if override < 0.0 or not math.isfinite(override):
        raise ValueError("Unit-scale override must be zero (automatic) or positive.")
    if override > 0.0:
        return validate_unit_scale(override)
    scene_scale = float(scene.unit_settings.scale_length)
    return scene_scale if math.isfinite(scene_scale) and scene_scale > 0.0 else 1.0


def get_bridge_uuid(obj: Any) -> uuid.UUID:
    """Return an object's persistent bridge UUID, assigning one when necessary."""
    try:
        value = obj.get(UUID_PROPERTY)
    except AttributeError as exc:
        raise TypeError("Expected a Blender object with custom-property access.") from exc

    raw_value = str(value) if value else ""
    as_pointer = getattr(obj, "as_pointer", None)
    cache_key = int(as_pointer()) if callable(as_pointer) else id(obj)
    cached = _UUID_CACHE.get(cache_key)
    if cached is not None and cached[0] == raw_value:
        return cached[1]

    canonical = None
    if value:
        try:
            parsed = uuid.UUID(raw_value)
            canonical = str(parsed)
        except (ValueError, AttributeError, TypeError):
            parsed = uuid.uuid4()
    else:
        parsed = uuid.uuid4()

    # Avoid rewriting an unchanged custom property on every identity lookup.
    if canonical is None or raw_value != canonical:
        canonical = str(parsed)
        obj[UUID_PROPERTY] = canonical

    if len(_UUID_CACHE) >= _UUID_CACHE_LIMIT:
        _UUID_CACHE.clear()
    _UUID_CACHE[cache_key] = (canonical, parsed)
    return parsed


def ensure_unique_uuids(objects: Iterable[Any]) -> dict[uuid.UUID, Any]:
    """Repair missing/duplicate identities and return the UUID-to-object inventory."""
    inventory: dict[uuid.UUID, Any] = {}
    for obj in objects:
        object_uuid = get_bridge_uuid(obj)
        if object_uuid in inventory:
            object_uuid = uuid.uuid4()
            obj[UUID_PROPERTY] = str(object_uuid)
        inventory[object_uuid] = obj
    return inventory


def object_uuid(obj: Any) -> uuid.UUID:
    return get_bridge_uuid(obj)


def resolve_export_scope(
    scene: Any,
    context: Any,
    scope: str,
) -> list[Any]:
    """Resolve mesh objects for a scope and include ancestors needed for transforms."""
    if scope not in VALID_SCOPES:
        raise ValueError(f"Unknown export scope: {scope!r}.")
    scene_objects = list(scene.objects)
    if scope == SCOPE_SELECTED:
        candidates = list(getattr(context, "selected_objects", ()))
    elif scope == SCOPE_COLLECTION:
        view_layer = getattr(context, "view_layer", None)
        layer_collection = getattr(view_layer, "active_layer_collection", None)
        if layer_collection is None:
            layer_collection = getattr(context, "active_layer_collection", None)
        collection = getattr(layer_collection, "collection", None)
        if collection is None:
            raise ValueError("There is no active collection to export.")
        candidates = list(collection.all_objects)
    else:
        candidates = list(scene.objects)

    meshes = [obj for obj in candidates if getattr(obj, "type", None) == "MESH"]
    if not meshes:
        return []
    included: dict[int, Any] = {}
    for obj in meshes:
        current = obj
        while current is not None:
            included[id(current)] = current
            current = getattr(current, "parent", None)

    scene_object_ids = {id(obj) for obj in scene_objects}
    outside_scene = [obj for obj in included.values() if id(obj) not in scene_object_ids]
    if outside_scene:
        raise ValueError("The export scope includes objects outside the active scene.")
    result = [obj for obj in scene_objects if id(obj) in included]
    ensure_unique_uuids(result)
    result.sort(key=lambda obj: getattr(obj, "name_full", obj.name))
    return result


def is_mesh_object(obj: Any) -> bool:
    return getattr(obj, "type", None) == "MESH"


def validate_no_shear(matrix: Any, tolerance: float = 1e-5) -> None:
    """Reject a local matrix whose basis cannot be represented as TRS."""
    if tolerance < 0.0 or not math.isfinite(tolerance):
        raise ValueError("Shear tolerance must be finite and non-negative.")
    basis = matrix.to_3x3()
    columns: Sequence[Any] = (basis.col[0], basis.col[1], basis.col[2])
    normalized = []
    for column in columns:
        length = float(column.length)
        if not math.isfinite(length):
            raise ValueError("A non-finite scale cannot be streamed.")
        normalized.append(column / length if length > 1e-12 else None)
    for left in range(3):
        for right in range(left + 1, 3):
            if normalized[left] is None or normalized[right] is None:
                continue
            if abs(float(normalized[left].dot(normalized[right]))) > tolerance:
                raise ValueError("Local transform contains shear and cannot be streamed as TRS.")


def local_trs(obj: Any, tolerance: float = 1e-5) -> tuple[Any, Any, Any]:
    """Validate and decompose an object's local transform."""
    matrix = obj.matrix_local
    validate_no_shear(matrix, tolerance)
    return matrix.decompose()