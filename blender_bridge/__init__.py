"""Blender Bridge addon entry point and user interface."""

from __future__ import annotations

import logging

bl_info = {
    "name": "Blender Bridge",
    "author": "Blender Bridge contributors",
    "version": (0, 1, 0),
    "blender": (5, 0, 0),
    "location": "View3D > Sidebar > Blender Bridge",
    "description": "Stream live mesh and material previews to Unity and export assets",
    "category": "Import-Export",
}

log = logging.getLogger("blender_bridge")
_runtime = None
_last_error = ""

try:
    import bpy
except ImportError:
    bpy = None

if bpy is not None:
    from bpy.app.handlers import persistent


def _update_settings(scene: object, _context: object) -> None:
    global _last_error
    if _runtime is not None:
        try:
            _runtime.refresh_settings(scene)
        except (RuntimeError, ValueError, OSError) as exc:
            _runtime.last_error = str(exc)
            log.exception("Could not refresh Blender Bridge settings.")
    else:
        _last_error = ""


if bpy is not None:
    class BLENDERBRIDGE_OT_toggle_live_updates(bpy.types.Operator):
        bl_idname = "blender_bridge.toggle_live_updates"
        bl_label = "Toggle Live Updates"
        bl_description = "Start or stop streaming the current Blender scope to Unity"

        def execute(self, context: object) -> set[str]:
            global _last_error, _runtime
            if _runtime is not None:
                try:
                    _runtime.stop()
                except (RuntimeError, ValueError, OSError) as exc:
                    _last_error = str(exc)
                    log.exception("Could not stop Blender Bridge.")
                    self.report({"ERROR"}, _last_error)
                    return {"CANCELLED"}

                _runtime = None
                context.scene.blender_bridge_project_root = ""
                _last_error = ""
                self.report({"INFO"}, "Blender Bridge live updates stopped.")
                return {"FINISHED"}

            context.scene.blender_bridge_project_root = ""
            from .live_sync import BridgeRuntime

            runtime = BridgeRuntime(context.scene)
            try:
                runtime.start()
            except (
                AttributeError,
                OSError,
                OverflowError,
                ReferenceError,
                RuntimeError,
                TypeError,
                ValueError,
            ) as exc:
                _last_error = str(exc)
                log.exception("Could not start Blender Bridge.")
                try:
                    runtime.stop()
                except (RuntimeError, ValueError, OSError) as cleanup_error:
                    _last_error += f"; cleanup failed: {cleanup_error}"
                    log.exception("Could not clean up after Blender Bridge startup failed.")
                self.report({"ERROR"}, _last_error)
                return {"CANCELLED"}

            _runtime = runtime
            _last_error = ""
            self.report({"INFO"}, "Blender Bridge live updates started.")
            return {"FINISHED"}


    class BLENDERBRIDGE_OT_bake(bpy.types.Operator):
        bl_idname = "blender_bridge.bake"
        bl_label = "Bake to Unity"
        bl_description = "Export the selected static meshes to the connected Unity project"
        bl_options = {"REGISTER"}

        def execute(self, context: object) -> set[str]:
            from .exporter import export_fbx

            scene = context.scene
            if _runtime is None or _runtime.sender is None:
                self.report({"ERROR"}, "Start Live Updates and connect to Unity before baking.")
                return {"CANCELLED"}
            try:
                try:
                    project_root = _runtime.sender.request_project_root()
                except TimeoutError:
                    project_root = _runtime.sender.request_project_root()
                scene.blender_bridge_project_root = project_root
                path = export_fbx(
                    scene=scene,
                    context=context,
                    project_root=project_root,
                    output_path=scene.blender_bridge_output_path,
                    scope=scene.blender_bridge_scope,
                    file_name=scene.blender_bridge_file_name,
                )
            except (BufferError, RuntimeError, ValueError, OSError) as exc:
                self.report({"ERROR"}, str(exc))
                return {"CANCELLED"}

            folder_open_error = ""
            try:
                result = bpy.ops.wm.path_open(filepath=str(path.parent))
                if "FINISHED" not in result:
                    folder_open_error = f"Could not open {path.parent}."
            except (OSError, RuntimeError) as exc:
                folder_open_error = f"Could not open the output folder: {exc}"
            if folder_open_error:
                self.report(
                    {"WARNING"},
                    f"Exported {path.name}, but {folder_open_error}",
                )
            else:
                self.report(
                    {"INFO"},
                    f"Exported {path.name}. Unity must import it and run its explicit Prefab bake step.",
                )
            return {"FINISHED"}


    class BLENDERBRIDGE_PT_panel(bpy.types.Panel):
        bl_label = "Blender Bridge"
        bl_idname = "BLENDERBRIDGE_PT_panel"
        bl_space_type = "VIEW_3D"
        bl_region_type = "UI"
        bl_category = "Blender Bridge"

        def draw(self, context: object) -> None:
            layout = self.layout
            scene = context.scene
            column = layout.column(align=True)
            if _runtime is None or _runtime.sender is None:
                column.label(text="Disconnected", icon="UNLINKED")
                latency = None
                last_error = _last_error
            else:
                sender = _runtime.sender
                state = sender.connection_state.title()
                icon = "CHECKMARK" if state == "Connected" else "TIME"
                column.label(text=state, icon=icon)
                latency = sender.latency_ms
                last_error = _runtime.last_error or sender.last_error
            if latency is not None:
                column.label(text=f"Round trip: {latency:.1f} ms")
            if last_error:
                box = layout.box()
                box.alert = True
                box.label(text="Last error", icon="ERROR")
                box.label(text=last_error)

            layout.prop(scene, "blender_bridge_port")
            layout.prop(scene, "blender_bridge_scope")
            layout.prop(scene, "blender_bridge_unit_scale")
            layout.operator(
                BLENDERBRIDGE_OT_toggle_live_updates.bl_idname,
                text="Stop Live Updates" if _runtime is not None else "Start Live Updates",
                icon="PAUSE" if _runtime is not None else "PLAY",
            )

            layout.separator()
            layout.label(text="Bake track (static meshes only)")
            root_row = layout.row()
            root_row.enabled = False
            root_row.prop(scene, "blender_bridge_project_root")
            layout.prop(scene, "blender_bridge_output_path")
            layout.prop(scene, "blender_bridge_file_name")
            layout.operator(BLENDERBRIDGE_OT_bake.bl_idname, icon="EXPORT")
            layout.label(text="Unity must import the FBX and create the Prefab.")


    _CLASSES = (
        BLENDERBRIDGE_OT_toggle_live_updates,
        BLENDERBRIDGE_OT_bake,
        BLENDERBRIDGE_PT_panel,
    )


    @persistent
    def _on_load_post(_unused: object) -> None:
        if _runtime is not None:
            _runtime.scene = bpy.context.scene
            _runtime.scene.blender_bridge_project_root = ""
            _runtime.new_session()


    def _file_export_menu(self: object, _context: object) -> None:
        self.layout.operator(BLENDERBRIDGE_OT_bake.bl_idname, text="Bake to Unity")


def register() -> None:
    if bpy is None:
        raise RuntimeError("Blender Bridge can only be registered inside Blender.")
    for cls in _CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.blender_bridge_port = bpy.props.IntProperty(
        name="Target Port",
        description="Unity bridge server port on loopback",
        default=8191,
        min=1,
        max=65535,
        update=_update_settings,
    )
    bpy.types.Scene.blender_bridge_scope = bpy.props.EnumProperty(
        name="Export Scope",
        items=(
            ("SELECTED", "Selected objects", "Stream selected mesh objects and required ancestors"),
            ("COLLECTION", "Active collection", "Stream mesh objects in the active collection"),
            ("SCENE", "Whole scene", "Stream mesh objects in the active scene"),
        ),
        default="SELECTED",
        update=_update_settings,
    )
    bpy.types.Scene.blender_bridge_unit_scale = bpy.props.FloatProperty(
        name="Unit Scale Override",
        description="0 uses the Blender scene unit scale; a positive value overrides it",
        default=0.0,
        min=0.0,
        update=_update_settings,
    )
    bpy.types.Scene.blender_bridge_project_root = bpy.props.StringProperty(
        name="Unity Project",
        description="Project root received from the connected Unity Editor",
        subtype="DIR_PATH",
        default="",
    )
    bpy.types.Scene.blender_bridge_output_path = bpy.props.StringProperty(
        name="Output Path",
        description=(
            "Relative folder under the detected Unity project's Assets folder; "
            "keep it within the Unity Bridge Settings output folder for Prefab baking"
        ),
        default="Art/Blender/Environment",
    )
    bpy.types.Scene.blender_bridge_file_name = bpy.props.StringProperty(
        name="File Name",
        description=(
            "Custom FBX file name without extension; leave empty to auto-derive from "
            "object, collection, or scene name"
        ),
        default="",
    )
    if _on_load_post not in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.append(_on_load_post)
    bpy.types.TOPBAR_MT_file_export.append(_file_export_menu)


def unregister() -> None:
    global _runtime
    if bpy is None:
        return
    if _runtime is not None:
        _runtime.stop()
        _runtime = None
    if _on_load_post in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.remove(_on_load_post)
    bpy.types.TOPBAR_MT_file_export.remove(_file_export_menu)
    for property_name in (
        "blender_bridge_file_name",
        "blender_bridge_output_path",
        "blender_bridge_project_root",
        "blender_bridge_unit_scale",
        "blender_bridge_scope",
        "blender_bridge_port",
    ):
        if hasattr(bpy.types.Scene, property_name):
            delattr(bpy.types.Scene, property_name)
    for cls in reversed(_CLASSES):
        bpy.utils.unregister_class(cls)