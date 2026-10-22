"""Main-thread extraction of evaluated Blender meshes into wire-ready data."""

from __future__ import annotations

import logging
import math
from collections import defaultdict
from typing import Any

from .convention import to_unity_dir, to_unity_pos
from .protocol import MeshPayload, Submesh

log = logging.getLogger("blender_bridge")


def _color_attribute_index(attribute: Any, loop: Any, loop_index: int) -> int:
    domain = getattr(attribute, "domain", "CORNER")
    if domain == "CORNER":
        return loop_index
    if domain == "POINT":
        return loop.vertex_index
    raise ValueError(f"Unsupported vertex colour domain {domain!r}; expected CORNER or POINT.")


def _normalized(values: tuple[float, float, float], label: str) -> tuple[float, float, float]:
    length = math.sqrt(sum(value * value for value in values))
    if not math.isfinite(length) or length <= 1e-12:
        raise ValueError(f"{label} contains a zero or invalid direction.")
    return tuple(value / length for value in values)


def extract_mesh(obj: Any, depsgraph: Any, unit_scale: float) -> MeshPayload:
    """Extract the evaluated, triangulated mesh and always release its temporary data."""
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    if mesh is None:
        raise RuntimeError(f"Blender returned no evaluated mesh for {obj.name!r}.")
    try:
        mesh.calc_loop_triangles()
        render_uv_layer = next(
            (layer for layer in mesh.uv_layers if getattr(layer, "active_render", False)),
            None,
        )
        uv_layer = (
            render_uv_layer
            if render_uv_layer is not None
            else mesh.uv_layers.active
        )
        color_attribute = mesh.color_attributes.active_color
        if uv_layer is not None:
            uvmap = uv_layer.name
            try:
                mesh.calc_tangents(uvmap=uvmap)
            except RuntimeError as exc:
                raise RuntimeError(
                    f"Could not calculate tangents for {obj.name!r} using UV map {uvmap!r}."
                ) from exc
            uv_layer = mesh.uv_layers.get(uvmap)

        positions: list[tuple[float, float, float]] = []
        normals: list[tuple[float, float, float]] = []
        tangents: list[tuple[float, float, float, float]] = []
        uvs: list[tuple[float, float]] = []
        colors: list[tuple[float, float, float, float]] = []
        by_material: dict[int, list[int]] = defaultdict(list)

        for triangle in mesh.loop_triangles:
            for loop_index in (
                triangle.loops[0],
                triangle.loops[2],
                triangle.loops[1],
            ):
                loop = mesh.loops[loop_index]
                output_index = len(positions)
                positions.append(to_unity_pos(mesh.vertices[loop.vertex_index].co, unit_scale))
                normal = _normalized(to_unity_dir(loop.normal), "Split normal")
                normals.append(normal)
                if uv_layer is not None:
                    uv = uv_layer.data[loop_index].uv
                    uvs.append((float(uv.x), float(uv.y)))
                    tangent = _normalized(to_unity_dir(loop.tangent), "Tangent")
                    tangents.append((*tangent, -float(loop.bitangent_sign)))
                if color_attribute is not None:
                    color_value = color_attribute.data[
                        _color_attribute_index(color_attribute, loop, loop_index)
                    ]
                    rgba = color_value.color_srgb
                    colors.append(tuple(float(component) for component in rgba))
                by_material[int(triangle.material_index)].append(output_index)

        indices: list[int] = []
        submeshes: list[Submesh] = []
        for material_index in sorted(by_material):
            material_indices = by_material[material_index]
            submeshes.append(Submesh(len(indices), len(material_indices), material_index))
            indices.extend(material_indices)

        if positions:
            minimum = tuple(min(point[axis] for point in positions) for axis in range(3))
            maximum = tuple(max(point[axis] for point in positions) for axis in range(3))
            bounds_center = tuple((minimum[axis] + maximum[axis]) * 0.5 for axis in range(3))
            bounds_extents = tuple((maximum[axis] - minimum[axis]) * 0.5 for axis in range(3))
        else:
            bounds_center = (0.0, 0.0, 0.0)
            bounds_extents = (0.0, 0.0, 0.0)

        return MeshPayload(
            object_uuid=obj["bridge_uuid"],
            name=obj.name,
            positions=positions,
            indices=indices,
            submeshes=submeshes,
            bounds_center=bounds_center,
            bounds_extents=bounds_extents,
            normals=normals,
            uvs=uvs,
            tangents=tangents,
            colors=colors,
        )
    finally:
        evaluated.to_mesh_clear()
