"""MCP server exposing Unity Editor operations to agent CLIs.

Run: uv run unity-mcp-server (stdio transport, the default MCP way).
Env: UNITY_MCP_PROJECT_DIR, UNITY_MCP_CHANNEL_FILE (see unity_bridge.py).
"""

import json
from typing import Any, Optional

from fastmcp import FastMCP

from unity_bridge import BridgeError, UnityBridge

mcp = FastMCP("unity-vrc-mcp")
bridge = UnityBridge()


def _camel(name: str) -> str:
    """snake_case -> camelCase so C# JsonUtility.FromJson parameter classes match."""
    first, *rest = name.split("_")
    return first + "".join(p[:1].upper() + p[1:] for p in rest)


def _result(env: dict) -> str:
    err = env.get("error") or {}
    if err.get("code", 0) != 0:
        raise BridgeError(json.dumps(err, ensure_ascii=False))
    raw = env.get("result")
    if raw is None:
        return "null"
    try:
        # Unity returns result as an escaped JSON string (JsonUtility quirk)
        return json.dumps(json.loads(raw), ensure_ascii=False)
    except json.JSONDecodeError:
        return raw


async def _call(method: str, **params) -> str:
    converted = {_camel(k): v for k, v in params.items() if v is not None}
    return _result(await bridge.request(method, converted))


@mcp.tool()
async def ping() -> str:
    """Check Unity bridge connectivity and editor state (pong + unity version)."""
    return await _call("ping")


@mcp.tool()
async def get_project_info() -> str:
    """Project identity, transport, compat status, VRCSDK presence and batch state."""
    return await _call("get_project_info")


@mcp.tool()
async def get_scene_hierarchy(max_depth: int = 8, max_nodes: int = 2000) -> str:
    """Serialized scene tree with instanceIds, components and transforms."""
    return await _call("get_scene_hierarchy", max_depth=max_depth, max_nodes=max_nodes)


@mcp.tool()
async def get_selection() -> str:
    """Currently selected objects in the editor (game objects + assets)."""
    return await _call("get_selection")


@mcp.tool()
async def begin_batch(name: Optional[str] = None) -> str:
    """Explicit Undo transaction: following tool calls collapse into ONE undo step."""
    return await _call("begin_batch", **({"name": name} if name else {}))


@mcp.tool()
async def end_batch() -> str:
    """Close the explicit transaction and collapse it into a single undo step."""
    return await _call("end_batch")


@mcp.tool()
async def get_batch_state() -> str:
    """Current batch state machine details (phase, idle seconds, close reason)."""
    return await _call("get_batch_state")


@mcp.tool()
async def get_console_logs(max: int = 50, level: str = "all", include_history: bool = False) -> str:
    """Recent Unity console messages (live public-API capture; optional best-effort history)."""
    return await _call(
        "get_console_logs",
        max=max,
        level=level,
        include_history=include_history,
    )


@mcp.tool()
async def undo() -> str:
    """Perform a single editor undo."""
    return await _call("undo")


@mcp.tool()
async def redo() -> str:
    """Perform a single editor redo."""
    return await _call("redo")


@mcp.tool()
async def get_object_details(instance_id: int, max_properties: int = 64) -> str:
    """Full detail for one object: transforms, components, serialized properties (edit prerequisite)."""
    return await _call("get_object_details", instance_id=instance_id, max_properties=max_properties)


@mcp.tool()
async def edit_transform(
    instance_id: int,
    position: list[float] | None = None,
    rotation: list[float] | None = None,
    scale: list[float] | None = None,
) -> str:
    """Set local position / rotation (euler degrees) / scale on a game object. At least one field required."""
    return await _call(
        "edit_transform", instance_id=instance_id, position=position, rotation=rotation, scale=scale
    )


@mcp.tool()
async def edit_set_active(instance_id: int, active: bool) -> str:
    """Set activeSelf on a game object."""
    return await _call("edit_set_active", instance_id=instance_id, active=active)


@mcp.tool()
async def edit_set_name(instance_id: int, name: str) -> str:
    """Rename a game object."""
    return await _call("edit_set_name", instance_id=instance_id, name=name)


@mcp.tool()
async def edit_add_component(instance_id: int, component_type: str) -> str:
    """Add a component by type name (e.g. BoxCollider, Rigidbody, or full type name)."""
    return await _call("edit_add_component", instance_id=instance_id, component_type=component_type)


@mcp.tool()
async def edit_remove_component(
    instance_id: int | None = None,
    component_instance_id: int | None = None,
    component_type: str | None = None,
) -> str:
    """Remove a component: by component_instance_id (preferred) or instance_id + component_type."""
    return await _call(
        "edit_remove_component",
        instance_id=instance_id or 0,
        component_instance_id=component_instance_id or 0,
        component_type=component_type,
    )


@mcp.tool()
async def edit_set_component_property(
    instance_id: int, component_type: str, property: str, value: Any
) -> str:
    """Set one serialized property by propertyPath (paths from get_object_details).
    value types: number, string, bool, [x,y,z] vector3, [r,g,b,a] color, asset path for object refs."""
    return await _call(
        "edit_set_component_property",
        instance_id=instance_id,
        component_type=component_type,
        property=property,
        value=json.dumps(value, ensure_ascii=False),
    )


@mcp.tool()
async def save_scene() -> str:
    """Save the active scene to disk."""
    return await _call("save_scene")


@mcp.tool()
async def edit_set_parent(
    instance_id: int, new_parent_instance_id: int = 0, world_position_stays: bool = True
) -> str:
    """Reparent a game object. new_parent_instance_id=0 unparents (scene root)."""
    return await _call(
        "edit_set_parent",
        instance_id=instance_id,
        new_parent_instance_id=new_parent_instance_id,
        world_position_stays=world_position_stays,
    )


@mcp.tool()
async def edit_set_sibling_index(instance_id: int, index: int = -1) -> str:
    """Reorder an object among its siblings (-1 = move to end)."""
    return await _call("edit_set_sibling_index", instance_id=instance_id, index=index)


@mcp.tool()
async def edit_create_object(
    name: str, parent_instance_id: int = 0, position: list[float] | None = None
) -> str:
    """Create an empty game object (parent_instance_id=0 = scene root)."""
    return await _call(
        "edit_create_object", name=name, parent_instance_id=parent_instance_id, position=position
    )


@mcp.tool()
async def edit_destroy_object(instance_id: int) -> str:
    """Destroy a game object (undoable)."""
    return await _call("edit_destroy_object", instance_id=instance_id)


@mcp.tool()
async def edit_duplicate_object(instance_id: int) -> str:
    """Duplicate a game object (undoable, exact clone incl. children)."""
    return await _call("edit_duplicate_object", instance_id=instance_id)


@mcp.tool()
async def prefab_create(instance_id: int, path: str) -> str:
    """Save a scene object as a Prefab asset (path must end .prefab under Assets/)."""
    return await _call("prefab_create", instance_id=instance_id, path=path)


@mcp.tool()
async def prefab_instantiate(
    path: str, parent_instance_id: int = 0, position: list[float] | None = None
) -> str:
    """Instantiate a prefab asset into the scene (parent_instance_id=0 = scene root)."""
    return await _call(
        "prefab_instantiate", path=path, parent_instance_id=parent_instance_id, position=position
    )


@mcp.tool()
async def asset_delete(path: str) -> str:
    """Delete an asset from the project (AssetDatabase.DeleteAsset). Use for test cleanup."""
    return await _call("asset_delete", path=path)


@mcp.tool()
async def asset_import_fbx(source_path: str, dest_dir: str = "Assets/Imports") -> str:
    """Copy a local model file (FBX/OBJ/GLB/GLTF/DAE/BLEND) into the project and import it via AssetDatabase."""
    return await _call("asset_import_fbx", source_path=source_path, dest_dir=dest_dir)


@mcp.tool()
async def import_avatar_from_zip(
    zip_path: str, dest_dir: str = "Assets/AvatarImports", do_import: bool = True
) -> str:
    """One-shot avatar import from a local zip: safe unzip, find first model file, import, instantiate in current scene."""
    return await _call("import_avatar_from_zip", zip_path=zip_path, dest_dir=dest_dir, **{"import": do_import})


@mcp.tool()
async def import_unitypackage(package_path: str) -> str:
    """Import a .unitypackage asset package via AssetDatabase.ImportPackage (non-interactive). Standard VRChat avatar distribution format - preserves prefab assembly, GUID references, controllers, expression menus, PhysBones, materials. Returns imported assets, detected prefabs/models, validation report."""
    return await _call("import_unitypackage", package_path=package_path)


@mcp.tool()
async def apply_shader_package_install(
    family: str, local_zip_path: str | None = None, remove: bool = False
) -> str:
    """Install (or remove) a whitelisted third-party shader package (currently: lilToon).
    Install resolves the latest version from the whitelisted VPM repo, validates the zip's
    package.json name, unpacks into Packages/ (zip-slip guarded) and refreshes.
    family: shader family (lilToon). local_zip_path: optional local zip (offline/no network).
    remove: uninstall instead of install. Not whitelisted families are rejected.
    Approval: mutates the project's Packages/ - apply_* permission applies."""
    return await _call(
        "apply_shader_package_install",
        family=family,
        local_zip_path=local_zip_path,
        remove=remove,
    )


@mcp.tool()
async def open_vrc_control_panel() -> str:
    """Open the VRChat SDK Build Control Panel window (menu VRChat SDK > Show Control Panel)."""
    return await _call("open_vrc_control_panel")


@mcp.tool()
async def sdk_repair_test_files() -> str:
    """Remove known broken official-SDK test files (VTPTests.cs / AssetBundleFooterTest.cs) from com.vrchat.base if present. Idempotent. Run after any VCC re-sync or when sdkHealth.status is broken."""
    return await _call("sdk_repair_test_files")


@mcp.tool()
async def create_animator_controller(asset_path: str, bind_instance_id: int = 0) -> str:
    """Create an AnimatorController asset (one layer, one Idle state), optionally bind to a scene GameObject's Animator. ASSET OPERATION - not undoable, do NOT call inside a batch."""
    return await _call("create_animator_controller", asset_path=asset_path, bind_instance_id=bind_instance_id)


@mcp.tool()
async def add_animator_state(
    asset_path: str, state_name: str, layer: int = 0, with_clip: bool = False, clip_path: str | None = None
) -> str:
    """Add a state to an AnimatorController layer, optionally creating a clip for its motion. ASSET OPERATION - not undoable, do NOT call inside a batch."""
    return await _call(
        "add_animator_state", asset_path=asset_path, state_name=state_name, layer=layer,
        with_clip=with_clip, clip_path=clip_path,
    )


@mcp.tool()
async def add_animator_transition(
    asset_path: str, from_state: str, to_state: str, layer: int = 0, duration: float = 0.25
) -> str:
    """Add a transition between two states in an AnimatorController layer. ASSET OPERATION - not undoable, do NOT call inside a batch."""
    return await _call(
        "add_animator_transition", asset_path=asset_path, **{"from": from_state}, **{"to": to_state},
        layer=layer, duration=duration,
    )


@mcp.tool()
async def get_animator_controller(asset_path: str) -> str:
    """Read-only: dump an AnimatorController asset (layers, states, transitions)."""
    return await _call("get_animator_controller", asset_path=asset_path)


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()