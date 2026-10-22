"""Depsgraph dirty tracking and trailing-flush collection on Blender's main thread."""

from __future__ import annotations

import logging
import time
from collections.abc import Iterable
from dataclasses import dataclass
from typing import Any, Callable

from .convention import ensure_unique_uuids, is_mesh_object, object_uuid

log = logging.getLogger("blender_bridge")


@dataclass(frozen=True)
class ChangeBatch:
    objects: dict[Any, Any]
    transforms: frozenset[Any]
    meshes: frozenset[Any]
    materials: frozenset[Any]
    hierarchy: frozenset[Any]
    visibility: frozenset[Any]
    deleted: tuple[tuple[Any, str], ...]
    full_snapshot: bool = False


class ChangeCollector:
    """Tracks IDs cheaply in the depsgraph handler and snapshots on a Blender timer."""

    def __init__(
        self,
        scope_provider: Callable[[], list[Any]],
        on_changes: Callable[[ChangeBatch], None],
        *,
        dispatch_hz: float = 30.0,
        reconcile_interval: float = 0.25,
    ) -> None:
        if dispatch_hz <= 0.0 or reconcile_interval <= 0.0:
            raise ValueError("Collector intervals must be positive.")
        self.scope_provider = scope_provider
        self.on_changes = on_changes
        self.dispatch_interval = 1.0 / min(dispatch_hz, 30.0)
        self.reconcile_interval = reconcile_interval
        self.last_dispatch = 0.0
        self.last_reconcile = 0.0
        self._objects: dict[Any, Any] = {}
        self._object_ids: dict[int, Any] = {}
        self._by_mesh_data: dict[int, set[Any]] = {}
        self._by_material: dict[int, set[Any]] = {}
        self._by_node_tree: dict[int, set[Any]] = {}
        self._by_image: dict[int, set[Any]] = {}
        self._sibling_indices: dict[int, int] = {}
        self._states: dict[Any, tuple[Any, int, bool]] = {}
        self._names: dict[Any, str] = {}
        self._dirty_transforms: set[Any] = set()
        self._dirty_meshes: set[Any] = set()
        self._dirty_materials: set[Any] = set()
        self._dirty_hierarchy: set[Any] = set()
        self._dirty_visibility: set[Any] = set()
        self._deleted: dict[Any, str] = {}
        self._full_snapshot = False
        self._registered = False
        self._persistent_handler: Callable[..., None] | None = None

    @staticmethod
    def _is_visible(obj: Any) -> bool:
        try:
            return not bool(obj.hide_get())
        except (AttributeError, RuntimeError):
            return not bool(getattr(obj, "hide_viewport", False))

    @staticmethod
    def _sibling_index(obj: Any) -> int:
        parent = getattr(obj, "parent", None)
        try:
            siblings = parent.children if parent is not None else obj.users_scene[0].objects
            return list(siblings).index(obj)
        except (AttributeError, IndexError, ValueError):
            return 0

    def _refresh_sibling_indices(self, objects: Iterable[Any]) -> None:
        """Build all sibling indices in one pass instead of scanning per object."""
        objects = list(objects)
        indices: dict[int, int] = {}
        first_scenes: dict[int, int] = {}
        seen_parents: set[int] = set()
        seen_scenes: set[int] = set()
        for obj in objects:
            parent = getattr(obj, "parent", None)
            if parent is not None:
                continue
            scenes = getattr(obj, "users_scene", ())
            if scenes:
                first_scenes[self._id_key(obj)] = self._id_key(scenes[0])

        for obj in objects:
            parent = getattr(obj, "parent", None)
            if parent is None:
                scenes = getattr(obj, "users_scene", ())
                if not scenes:
                    continue
                scene = scenes[0]
                scene_key = self._id_key(scene)
                if scene_key in seen_scenes:
                    continue
                seen_scenes.add(scene_key)
                for index, sibling in enumerate(getattr(scene, "objects", ())):
                    sibling_key = self._id_key(sibling)
                    if first_scenes.get(sibling_key) == scene_key:
                        indices[sibling_key] = index
            else:
                parent_key = self._id_key(parent)
                if parent_key in seen_parents:
                    continue
                seen_parents.add(parent_key)
                for index, sibling in enumerate(getattr(parent, "children", ())):
                    indices[self._id_key(sibling)] = index

        self._sibling_indices = indices

    def sibling_index(self, obj: Any) -> int:
        """Return a cached sibling index, falling back to a live lookup."""
        key = self._id_key(obj)
        if key in self._sibling_indices:
            return self._sibling_indices[key]
        return self._sibling_index(obj)

    def _state(self, obj: Any) -> tuple[Any, int, bool]:
        parent = getattr(obj, "parent", None)
        parent_id = object_uuid(parent) if parent is not None else None
        return (parent_id, self.sibling_index(obj), self._is_visible(obj))

    @staticmethod
    def _id_key(value: Any) -> int:
        as_pointer = getattr(value, "as_pointer", None)
        return int(as_pointer()) if callable(as_pointer) else id(value)

    def _reconcile(self) -> None:
        scope = self.scope_provider()
        inventory = ensure_unique_uuids(scope)
        current = {object_uuid(obj): obj for obj in inventory.values()}
        current_ids = set(current)
        previous_ids = set(self._objects)

        for removed_id in previous_ids - current_ids:
            self._deleted[removed_id] = self._names.pop(removed_id, str(removed_id))
            self._states.pop(removed_id, None)
        for added_id in current_ids - previous_ids:
            obj = current[added_id]
            self._dirty_transforms.add(added_id)
            self._dirty_hierarchy.add(added_id)
            self._dirty_visibility.add(added_id)
            self._dirty_materials.add(added_id)
            if is_mesh_object(obj):
                self._dirty_meshes.add(added_id)

        self._by_mesh_data.clear()
        self._by_material.clear()
        self._by_node_tree.clear()
        self._by_image.clear()
        self._refresh_sibling_indices(current.values())
        for object_id, obj in current.items():
            if is_mesh_object(obj):
                self._by_mesh_data.setdefault(self._id_key(obj.data), set()).add(object_id)
                for slot in getattr(obj, "material_slots", ()):
                    material = getattr(slot, "material", None)
                    if material is None:
                        continue
                    self._by_material.setdefault(self._id_key(material), set()).add(object_id)
                    tree = getattr(material, "node_tree", None)
                    if tree is not None:
                        self._by_node_tree.setdefault(self._id_key(tree), set()).add(object_id)
                    for node in getattr(tree, "nodes", ()):
                        image = getattr(node, "image", None)
                        if getattr(node, "type", "") == "TEX_IMAGE" and image is not None:
                            self._by_image.setdefault(self._id_key(image), set()).add(object_id)
            state = self._state(obj)
            self._names[object_id] = obj.name
            if object_id in self._states:
                previous_parent, previous_sibling, previous_visibility = self._states[object_id]
                if previous_parent != state[0] or previous_sibling != state[1]:
                    self._dirty_hierarchy.add(object_id)
                if previous_visibility != state[2]:
                    self._dirty_visibility.add(object_id)
            self._states[object_id] = state

        self._objects = current
        self._object_ids = {self._id_key(obj): object_id for object_id, obj in current.items()}

    def _on_depsgraph_update(self, _scene: Any, depsgraph: Any) -> None:
        for update in depsgraph.updates:
            updated_id = update.id
            original_id = getattr(updated_id, "original", None)
            if original_id is not None:
                updated_id = original_id

            if hasattr(updated_id, "type") and hasattr(updated_id, "data"):
                object_id = self._object_ids.get(self._id_key(updated_id))
                if object_id is None:
                    continue
                if update.is_updated_transform:
                    self._dirty_transforms.add(object_id)
                if is_mesh_object(updated_id) and update.is_updated_geometry:
                    self._dirty_meshes.add(object_id)
                if update.is_updated_shading:
                    self._dirty_materials.add(object_id)
            elif hasattr(updated_id, "vertices"):
                for object_id in self._by_mesh_data.get(self._id_key(updated_id), ()):
                    self._dirty_meshes.add(object_id)
            else:
                object_id = self._id_key(updated_id)
                self._dirty_materials.update(self._by_material.get(object_id, ()))
                self._dirty_materials.update(self._by_node_tree.get(object_id, ()))
                self._dirty_materials.update(self._by_image.get(object_id, ()))

    def mark_full_snapshot(self) -> None:
        self._reconcile()
        self._dirty_transforms.update(self._objects)
        self._dirty_hierarchy.update(self._objects)
        self._dirty_visibility.update(self._objects)
        self._dirty_materials.update(self._objects)
        self._dirty_meshes.update(
            object_id for object_id, obj in self._objects.items() if is_mesh_object(obj)
        )
        self._full_snapshot = True

    def register(self) -> None:
        if self._registered:
            return
        try:
            import bpy
        except ImportError as exc:
            raise RuntimeError("The Blender addon must be registered inside Blender.") from exc
        self._persistent_handler = self._on_depsgraph_update
        if self._persistent_handler not in bpy.app.handlers.depsgraph_update_post:
            bpy.app.handlers.depsgraph_update_post.append(self._persistent_handler)
        self._registered = True
        self._reconcile()
        bpy.app.timers.register(self._timer_callback, first_interval=self.dispatch_interval)

    def unregister(self) -> None:
        if not self._registered:
            return
        import bpy

        if self._persistent_handler in bpy.app.handlers.depsgraph_update_post:
            bpy.app.handlers.depsgraph_update_post.remove(self._persistent_handler)
        if bpy.app.timers.is_registered(self._timer_callback):
            bpy.app.timers.unregister(self._timer_callback)
        self._registered = False
        self._persistent_handler = None

    def _timer_callback(self) -> float | None:
        if not self._registered:
            return None
        now = time.monotonic()
        try:
            if now - self.last_reconcile >= self.reconcile_interval:
                self._reconcile()
                self.last_reconcile = now
            if (
                self._dirty_transforms
                or self._dirty_meshes
                or self._dirty_materials
                or self._dirty_hierarchy
                or self._dirty_visibility
                or self._deleted
                or self._full_snapshot
            ) and now - self.last_dispatch >= self.dispatch_interval:
                batch = ChangeBatch(
                    objects=dict(self._objects),
                    transforms=frozenset(self._dirty_transforms),
                    meshes=frozenset(self._dirty_meshes),
                    materials=frozenset(self._dirty_materials),
                    hierarchy=frozenset(self._dirty_hierarchy),
                    visibility=frozenset(self._dirty_visibility),
                    deleted=tuple(self._deleted.items()),
                    full_snapshot=self._full_snapshot,
                )
                self.on_changes(batch)
                self._dirty_transforms.clear()
                self._dirty_meshes.clear()
                self._dirty_materials.clear()
                self._dirty_hierarchy.clear()
                self._dirty_visibility.clear()
                self._deleted.clear()
                self._full_snapshot = False
                self.last_dispatch = time.monotonic()
        except Exception:
            # A single unexpected object must not unregister Blender's timer.
            # Pending dirty sets are retained and retried on the next tick.
            log.exception("Bridge change collection failed; pending changes were retained.")
        return self.dispatch_interval