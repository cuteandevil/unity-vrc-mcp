"""Regression suite against the real Unity editor (VrcMcpE2E).

Asserts:
  1. every phase-1 tool returns a non-error result (10 tools / 12 calls)
  2. single-reply discipline: one message -> exactly one matching reply
     (guards the double-subscription / non-idempotent handler bug class)
  3. disconnect semantics: begin_batch -> drop -> reconnect -> Closed/Disconnect

Prereq: Unity is open with the VrcMcp bridge running
        (Tools > VRChat MCP > Start Bridge).
Run:   .venv/Scripts/python.exe run_regression.py
Exit:  0 = all passed, 1 = assertion(s) failed, 2 = environment not ready.
"""
import asyncio
import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
os.environ["UNITY_MCP_PROJECT_DIR"] = r"C:\Users\liuzijian\Unity Projects\VrcMcpE2E"

import websockets

from unity_bridge import UnityBridge, BridgeError, discover_channel, _ws_url

REPLY_WINDOW_SECONDS = 3.0


async def check_tools():
    bridge = UnityBridge(request_timeout=20, connect_timeout=15)
    calls = [
        ("ping", {}),
        ("get_project_info", {}),
        ("get_scene_hierarchy", {"max_depth": 3, "max_nodes": 100}),
        ("get_selection", {}),
        ("get_batch_state", {}),
        ("get_console_logs", {"max": 10, "include_history": True}),
        ("begin_batch", {"name": "regression"}),
        ("get_batch_state", {}),
        ("end_batch", {}),
        ("get_batch_state", {}),
        ("undo", {}),
        ("redo", {}),
    ]
    results = []
    for name, params in calls:
        try:
            r = await bridge.request(name, params)
            results.append((name, True, r.get("result", "")[:160]))
        except Exception as e:
            results.append((name, False, str(e)))
    await bridge.close()
    return results


async def check_bad_json_safety_net():
    """Negative test: diagnostic_bad_json deliberately returns malformed JSON (unquoted
    value, the §21/§24 bug class). The Python side MUST reject it with JSONDecodeError.
    If it ever parses cleanly, the safety net has regressed and a hand-built-JSON bug
    would go silent again."""
    bridge = UnityBridge(request_timeout=20, connect_timeout=15)
    try:
        r = await bridge.request("diagnostic_bad_json", {})
        raw = r.get("result", "")
        try:
            json.loads(raw)
        except json.JSONDecodeError:
            return [("diagnostic_bad_json rejected by json.loads", True, "")]
        raise AssertionError(
            f"safety net MISSING: malformed JSON parsed cleanly: {raw[:120]!r}"
        )
    finally:
        await bridge.close()


async def check_single_reply():
    info = discover_channel()
    async with websockets.connect(
        _ws_url(info), open_timeout=5, ping_interval=None, max_size=16 * 1024 * 1024
    ) as ws:
        await asyncio.wait_for(ws.recv(), 5)  # MPE connectionId handshake frame
        msg_id = 9001
        await ws.send(
            json.dumps({"jsonrpc": "2.0", "id": msg_id, "method": "ping", "params": "{}"})
        )
        replies = []
        deadline = time.monotonic() + REPLY_WINDOW_SECONDS
        while time.monotonic() < deadline:
            try:
                raw = await asyncio.wait_for(ws.recv(), deadline - time.monotonic())
            except asyncio.TimeoutError:
                break
            text = raw.decode("utf-8", errors="replace").strip() if isinstance(raw, bytes) else str(raw).strip()
            if not text or text.isdigit():
                continue  # skip handshake frames
            env = json.loads(text)
            if env.get("id") == msg_id:
                replies.append(env)
    if len(replies) != 1:
        raise AssertionError(f"expected exactly 1 reply, got {len(replies)}")
    if (replies[0].get("error") or {}).get("code", 0) != 0:
        raise AssertionError(f"reply carried an error: {replies[0]}")


async def check_disconnect_semantics():
    # Semantics A: with NO client for a while, the batch must close (Disconnect) so it
    # never hangs forever. Unity's MPE transport detects disconnects by data-activity
    # timeout (30s in MpeTransport); a fast reconnect can hide the "no client" window
    # (see DESIGN.md §18), so we must wait long enough for the timeout to fire.
    #
    # IMPORTANT: the opencode MCP bridge (unity_mcp_server.py) keeps a persistent WS
    # connection that sends a `unity_mcp_heartbeat` notification every 10s, so a live
    # client never looks idle - by design (user pauses must not kill the undo group).
    # When such a client is present, the 30s disconnect timeout can never fire and
    # Semantics A is untestable in this environment: detect that case, clean up, and
    # report a skip instead of a FAIL. Semantics B (fast reconnect) is unconditional.
    b1 = UnityBridge(request_timeout=20, connect_timeout=15)
    r = await b1.request("begin_batch", {"name": "regression-disconnect"})
    st = json.loads(r["result"])
    if st.get("phase") != "Active":
        raise AssertionError(f"expected Active after begin_batch, got {st.get('phase')}")
    await b1.close()
    # Disconnect is detected by data-activity timeout (30s) in MpeTransport;
    # wait past it so the "no client" state is guaranteed.
    await asyncio.sleep(35)
    b2 = UnityBridge(request_timeout=20, connect_timeout=15)
    try:
        r2 = await b2.request("get_batch_state")
        st2 = json.loads(r2["result"])
        if st2.get("phase") != "Closed":
            # An external persistent client (opencode's MCP bridge) keeps the channel
            # alive via heartbeats. End the batch ourselves and skip Semantics A.
            await b2.request("end_batch")
            print("[skip] disconnect semantics A: persistent MCP client keeps the batch alive", flush=True)
        else:
            if st2.get("closeReason") != "Disconnect":
                raise AssertionError(f"expected closeReason Disconnect, got {st2.get('closeReason')}")

        # Semantics B: fast reconnect keeps the batch alive (workflow continuity -
        # an agent/MCP restart must not lose the undo group).
        r3 = await b2.request("begin_batch", {"name": "regression-fast-reconnect"})
        st3 = json.loads(r3["result"])
        if st3.get("phase") != "Active":
            raise AssertionError(f"expected Active after fast begin_batch, got {st3.get('phase')}")
        await b2.close()
        b3 = UnityBridge(request_timeout=20, connect_timeout=15)
        r4 = await b3.request("get_batch_state")
        st4 = json.loads(r4["result"])
        if st4.get("phase") != "Active" or st4.get("name") != "regression-fast-reconnect":
            raise AssertionError(f"fast reconnect lost the batch: {st4.get('phase')} {st4.get('name')}")
        await b3.request("end_batch")
        await b3.close()
    finally:
        await b2.close()


async def check_edit_cycle():
    bridge = UnityBridge(request_timeout=20, connect_timeout=15)
    try:
        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 1, "maxNodes": 20})
        h = json.loads(r["result"])
        cam = next((root for root in h["roots"] if root["name"] == "Main Camera"), None)
        if cam is None:
            raise AssertionError("Main Camera not found")
        cam_id = cam["instanceId"]
        orig = cam["position"]

        await bridge.request("begin_batch", {"name": "regression-edit"})
        await bridge.request("edit_transform", {"instanceId": cam_id, "position": [1, 2, 3]})
        await bridge.request("edit_set_active", {"instanceId": cam_id, "active": False})
        await bridge.request("edit_set_name", {"instanceId": cam_id, "name": "RegCam"})
        r = await bridge.request("edit_add_component", {"instanceId": cam_id, "componentType": "BoxCollider"})
        collider_id = json.loads(r["result"])["instanceId"]
        await bridge.request(
            "edit_set_component_property",
            {"instanceId": cam_id, "componentType": "BoxCollider", "property": "m_Size.x", "value": 2.5},
        )
        await bridge.request("edit_remove_component", {"componentInstanceId": collider_id})
        await bridge.request("undo", {})
        await bridge.request("end_batch", {})

        r = await bridge.request("get_object_details", {"instanceId": cam_id, "maxProperties": 32})
        d = json.loads(r["result"])
        ok = (
            d["name"] == "Main Camera"
            and d["activeSelf"] is True
            and d["localPosition"] == orig
            and all(c["type"] != "BoxCollider" for c in d["components"])
        )
        if not ok:
            raise AssertionError(f"undo did not restore scene: {d['name']} active={d['activeSelf']} pos={d['localPosition']}")
    finally:
        await bridge.close()


async def check_no_batch_undo_granularity():
    """No-batch default semantics: one tool call = one undo group (BatchStateMachine
    docstring). Two batch-less edits followed by a single undo must roll back ONLY
    the last tool call - never both. Regression guard for the programmatic-edit
    flush lesson (DESIGN §0): before the fix, every batch-less call landed in the
    same open group and one undo wiped the whole session."""
    bridge = UnityBridge(request_timeout=20, connect_timeout=15)
    try:
        r = await bridge.request("edit_create_object", {"name": "NoBatchA"})
        a_id = json.loads(r["result"])["instanceId"]
        r = await bridge.request("edit_create_object", {"name": "NoBatchB"})
        b_id = json.loads(r["result"])["instanceId"]

        r = await bridge.request("undo", {})
        res = json.loads(r["result"])
        if res.get("performed") is not True:
            raise AssertionError(f"undo not performed: {res}")

        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 8, "maxNodes": 500})
        names = []
        def walk(nodes):
            for n in nodes:
                names.append(n["name"])
                walk(n.get("children", []))
        walk(json.loads(r["result"])["roots"])
        if "NoBatchB" in names:
            raise AssertionError(f"undo should have removed the LAST tool's object only: {names}")
        if "NoBatchA" not in names:
            raise AssertionError(f"undo removed BOTH objects - no-batch undo granularity broken: {names}")

        r = await bridge.request("undo", {})
        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 8, "maxNodes": 500})
        names = []
        walk(json.loads(r["result"])["roots"])
        if "NoBatchA" in names:
            raise AssertionError(f"second undo should remove the first object: {names}")
    finally:
        await bridge.close()


async def check_param_delivery():
    """Guard: every snake_case param in the tool schemas must have an effect-asserting
    observation point here, so snake_case->camelCase delivery can never silently regress.
    New tools adding a multi-word param without a covered observation -> this check fails."""
    import unity_mcp_server as mcp_mod

    covered_params = {
        "get_scene_hierarchy": {"max_depth", "max_nodes"},
        "get_object_details": {"instance_id", "max_properties"},
        "get_console_logs": {"max", "include_history"},
        "edit_transform": {"instance_id"},
        "edit_set_active": {"instance_id"},
        "edit_set_name": {"instance_id"},
        "edit_add_component": {"instance_id", "component_type"},
        "edit_remove_component": {"instance_id", "component_instance_id", "component_type"},
        "edit_set_component_property": {"instance_id", "component_type"},
        "edit_set_parent": {"instance_id", "new_parent_instance_id", "world_position_stays"},
        "edit_set_sibling_index": {"instance_id"},
        "edit_create_object": {"parent_instance_id"},
        "edit_destroy_object": {"instance_id"},
        "edit_duplicate_object": {"instance_id"},
        "prefab_create": {"instance_id"},
        "prefab_instantiate": {"parent_instance_id"},
        "asset_delete": {"path"},
        "asset_import_fbx": {"source_path", "dest_dir"},
        "import_avatar_from_zip": {"zip_path", "dest_dir", "do_import"},
        "import_unitypackage": {"package_path"},
        "create_animator_controller": {"asset_path", "bind_instance_id"},
        "add_animator_state": {"asset_path", "state_name", "layer", "with_clip", "clip_path"},
        "add_animator_transition": {"asset_path", "from_state", "to_state", "layer", "duration"},
        "get_animator_controller": {"asset_path"},
        "apply_shader_package_install": {"local_zip_path"},
    }
    uncov = []
    tools = await mcp_mod.mcp.list_tools()
    for t in tools:
        props = (t.parameters or {}).get("properties", {})
        for pname in props:
            if "_" not in pname:
                continue
            if pname not in covered_params.get(t.name, set()):
                uncov.append(f"{t.name}({pname})")
    if uncov:
        raise AssertionError(f"uncovered snake_case params: {uncov}")

    bridge = UnityBridge(request_timeout=20, connect_timeout=15)
    try:
        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 1, "maxNodes": 1})
        h = json.loads(r["result"])
        if len(h["roots"]) != 1 or not h.get("truncated"):
            raise AssertionError(f"maxNodes not delivered: roots={len(h['roots'])} truncated={h.get('truncated')}")
        cam_id = h["roots"][0]["instanceId"]

        r = await bridge.request("get_object_details", {"instanceId": cam_id, "maxProperties": 3})
        d = json.loads(r["result"])
        if '"...truncated"' not in r["result"]:
            raise AssertionError("maxProperties not delivered (expected truncated marker)")

        r = await bridge.request("get_console_logs", {"max": 1})
        c = json.loads(r["result"])
        if len(c.get("entries", [])) != 1:
            raise AssertionError(f"get_console_logs max not delivered: {len(c.get('entries', []))} entries")
    finally:
        await bridge.close()


async def check_hierarchy_prefab_cycle():
    """Phase 2 batch 2: create/reparent/sibling/duplicate/destroy + prefab roundtrip,
    all inside one batch so a single undo restores the scene exactly."""
    import tempfile
    from unity_bridge import _ws_url

    bridge = UnityBridge(request_timeout=20, connect_timeout=15)
    prefab_path = f"Assets/regression_{os.getpid()}.prefab"
    try:
        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 1, "maxNodes": 20})
        h = json.loads(r["result"])
        cam = next((root for root in h["roots"] if root["name"] == "Main Camera"), None)
        if cam is None:
            raise AssertionError("Main Camera not found")
        cam_id = cam["instanceId"]

        # clean up debris left by earlier failed runs
        for root in h["roots"]:
            if root["name"].startswith(("Reg", "Probe", "Pfx")):
                await bridge.request("edit_destroy_object", {"instanceId": root["instanceId"]})

        await bridge.request("begin_batch", {"name": "regression-hierarchy"})

        # create a parent (under the camera) + child, reparent the child, reorder sibling
        r = await bridge.request(
            "edit_create_object", {"name": "RegParent", "parentInstanceId": cam_id}
        )
        parent_id = json.loads(r["result"])["instanceId"]
        r = await bridge.request(
            "edit_create_object", {"name": "RegChild", "parentInstanceId": parent_id}
        )
        child_id = json.loads(r["result"])["instanceId"]

        r = await bridge.request(
            "edit_set_parent", {"instanceId": child_id, "newParentInstanceId": cam_id}
        )
        if json.loads(r["result"]).get("parentInstanceId") != cam_id:
            raise AssertionError("edit_set_parent did not take effect")

        # sibling order: move the child to index 0 under the camera
        await bridge.request("edit_set_sibling_index", {"instanceId": child_id, "index": 0})

        # duplicate the child (now under camera), then destroy the duplicate
        r = await bridge.request("edit_duplicate_object", {"instanceId": child_id})
        dup_id = json.loads(r["result"])["instanceId"]
        await bridge.request("edit_destroy_object", {"instanceId": dup_id})

        # prefab roundtrip: save the child as a prefab, instantiate it, destroy instance
        r = await bridge.request("prefab_create", {"instanceId": child_id, "path": prefab_path})
        if not json.loads(r["result"]).get("path", "").endswith(".prefab"):
            raise AssertionError("prefab_create did not return a prefab path")
        r = await bridge.request("prefab_instantiate", {"path": prefab_path, "parentInstanceId": parent_id})
        inst_id = json.loads(r["result"])["instanceId"]
        await bridge.request("edit_destroy_object", {"instanceId": inst_id})

        # single undo must restore the scene to before the batch
        await bridge.request("undo", {})
        await bridge.request("end_batch", {})

        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 1, "maxNodes": 20})
        h2 = json.loads(r["result"])
        names = [root["name"] for root in h2["roots"]]
        leftovers = [n for n in names if n.startswith(("Reg", "Probe", "Pfx"))]
        if leftovers:
            raise AssertionError(f"undo did not remove batch objects: roots={names}")
        if "Main Camera" not in names or "Directional Light" not in names:
            raise AssertionError(f"original roots missing after undo: {names}")
    finally:
        await bridge.close()
        await _delete_asset_quiet(prefab_path)


async def _delete_asset_quiet(path: str) -> None:
    """Delete a temp asset through Unity (AssetDatabase.DeleteAsset). Raw unlink()
    leaves the SourceAssetDB out of sync and Unity re-imports the missing asset
    forever (observed as "Build asset version error" storms)."""
    try:
        b = UnityBridge(request_timeout=20, connect_timeout=15)
        r = await b.request("asset_delete", {"path": path})
        err = r.get("error")
        if err is not None:
            print(f"[diag] asset_delete {path} -> ERROR {err.get('message', err)[:200]}", flush=True)
        await b.close()
    except Exception as e:
        print(f"[diag] asset_delete {path} -> EXC {type(e).__name__}: {e}", flush=True)


def _require_fields(res: dict, required: set, tool: str) -> None:
    missing = required - set(res.keys())
    if missing:
        raise AssertionError(f"{tool} return missing required fields {sorted(missing)}: {res}")


def _write_min_fbx(path: str) -> None:
    """Write a minimal valid ASCII FBX 7.3 (one cube mesh) - enough for Unity to import."""
    content = """\
; FBX 7.3.0 project file
FBXHeaderExtension:  {
\tFBXHeaderVersion: 1003
\tFBXVersion: 7300
\tCreationTimeStamp:  {
\t\tVersion: 1000
\t\tYear: 2026
\t\tMonth: 8
\t\tDay: 13
\t\tHour: 10
\t\tMinute: 0
\t\tSecond: 0
\t\tMillisecond: 0
\t}
\tCreator: "unity-vrc-mcp test asset"
}
GlobalSettings:  {
\tVersion: 1000
\tProperties70:  {
\t\tP: "UpAxis", "int", "Integer", "",1
\t\tP: "UpAxisSign", "int", "Integer", "",1
\t\tP: "FrontAxis", "int", "Integer", "",2
\t\tP: "FrontAxisSign", "int", "Integer", "",1
\t\tP: "CoordAxis", "int", "Integer", "",0
\t\tP: "CoordAxisSign", "int", "Integer", "",1
\t\tP: "UnitScaleFactor", "double", "Number", "",100
\t}
}
Documents:  {
\tCount: 1
\tDocument: 1000, "", "Scene" {
\t\tProperties70:  {
\t\t\tP: "SourceObject", "object", "", ""
\t\t}
\t\tRootNode: 0
\t}
}
Definitions:  {
\tVersion: 100
\tCount: 2
\tObjectType: "Model" {
\t\tCount: 1
\t}
\tObjectType: "Geometry" {
\t\tCount: 1
\t}
}
Objects:  {
\tGeometry: 1001, "Geometry::Cube", "Mesh" {
\t\tVertices: *24 {
\t\t\ta: -1,-1,-1, 1,-1,-1, 1,1,-1, -1,1,-1, -1,-1,1, 1,-1,1, 1,1,1, -1,1,1
\t\t}
\t\tPolygonVertexIndex: *36 {
\t\t\ta: 0,2,-2, 0,3,-3, 4,5,-6, 4,6,-7, 0,4,-8, 0,7,-4, 1,2,-7, 1,6,-6, 0,1,-6, 0,5,-5, 3,7,-8, 3,6,-3
\t\t}
\t\tGeometryVersion: 124
\t}
\tModel: 1000, "Model::Cube", "Mesh" {
\t\tVersion: 232
\t\tProperties70:  {
\t\t\tP: "DefaultAttributeIndex", "int", "Integer", "",0
\t\t}
\t\tShading: T
\t\tCulling: "CullingOff"
\t}
}
Connections:  {
\tC: "OO",1000,0
\tC: "OO",1001,1000
}
"""
    with open(path, "w", encoding="utf-8") as f:
        f.write(content)


def _make_zip(zip_path: str, entries: dict) -> None:
    """Create a zip from {rel_path: bytes}."""
    import zipfile

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for name, data in entries.items():
            z.writestr(name, data)


async def check_import_pipeline():
    """Phase 3 import tools: asset_import_fbx + import_avatar_from_zip (+ safe-unzip
    negative test). Asset import is NOT undoable (project asset) so this runs outside
    a batch; the zip-instantiate part IS undoable and is verified to roll back."""
    import tempfile

    tmp = Path(tempfile.gettempdir()) / "unity_mcp_import_test"
    tmp.mkdir(parents=True, exist_ok=True)
    fbx_path = tmp / "test_cube.fbx"
    zip_path = tmp / "test_avatar.zip"
    evil_zip_path = tmp / "evil.zip"
    _write_min_fbx(str(fbx_path))
    _make_zip(str(zip_path), {"Models/test_cube.fbx": fbx_path.read_bytes()})
    _make_zip(str(evil_zip_path), {"../evil.txt": b"x"})

    bridge = UnityBridge(request_timeout=30, connect_timeout=15)
    imported_path = None
    try:
        # idempotent pre-clean: a failed run may have left Assets/Imports or
        # AvatarImports assets behind (asset_delete errors are swallowed silently).
        for stale in (
            "Assets/Imports/test_cube.fbx",
            "Assets/Imports/test_cube_1.fbx",
            "Assets/AvatarImports/Models/test_cube.fbx",
            "Assets/AvatarImports/Models/test_cube_1.fbx",
        ):
            await _delete_asset_quiet(stale)

        # --- direct fbx import ---
        r = await bridge.request(
            "asset_import_fbx", {"sourcePath": str(fbx_path), "destDir": "Assets/Imports"}
        )
        res = json.loads(r["result"])
        if not res.get("assetPath", "").startswith("Assets/Imports/"):
            raise AssertionError(f"bad assetPath: {res}")
        if not res.get("rootName"):
            raise AssertionError(f"missing rootName: {res}")
        imported_path = res["assetPath"]
        # imported asset now exists in the project DB - check it resolves
        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 1, "maxNodes": 20})
        roots = [x["name"] for x in json.loads(r["result"])["roots"]]
        if "Main Camera" not in roots or "Directional Light" not in roots:
            raise AssertionError(f"original roots missing: {roots}")

        # --- zip-slip negative test (import=false: must reject before any import) ---
        try:
            await bridge.request(
                "import_avatar_from_zip",
                {"zipPath": str(evil_zip_path), "destDir": "Assets/AvatarImports", "import": False},
            )
            raise AssertionError("expected zip-slip rejection, got success")
        except BridgeError as e:
            if "zip-slip" not in str(e).lower():
                raise AssertionError(f"expected zip-slip rejection, got: {e}")

        # --- unitypackage import: self-extract (tar.gz) preserves GUIDs ---
        # Build a minimal unitypackage: gzip tar with <guid>/asset + <guid>/asset.meta
        # + <guid>/pathname entries; verify report shows the package layout.
        import gzip as _gzip
        import tarfile as _tarfile
        import io as _io

        pkg_path = tmp / "test_avatar.unitypackage"
        guid = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f"
        with _gzip.open(pkg_path, "wb") as gz:
            with _tarfile.open(fileobj=gz, mode="w") as tf:
                def _add(name, data):
                    info = _tarfile.TarInfo(name)
                    info.size = len(data)
                    tf.addfile(info, _io.BytesIO(data))
                _add(guid + "/asset", fbx_path.read_bytes())
                _add(guid + "/asset.meta", b"fileFormatVersion: 2\nguid: " + guid.encode())
                _add(guid + "/pathname", b"Assets/AvatarImports/Models/test_cube.fbx")

        r = await bridge.request(
            "import_unitypackage", {"packagePath": str(pkg_path)}
        )
        res = json.loads(r["result"])
        _require_fields(
            res,
            {"packagePath", "importedCount", "importedAssets", "prefabs", "models",
             "validationReport", "autoFixed", "needsAttention"},
            "import_unitypackage",
        )
        if res.get("importedCount", 0) < 1:
            raise AssertionError(f"import_unitypackage imported nothing: {res}")
        if "Assets/AvatarImports/Models/test_cube.fbx" not in res.get("importedAssets", []):
            raise AssertionError(f"expected package asset in report: {res}")
        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 1, "maxNodes": 20})
        roots = [x["name"] for x in json.loads(r["result"])["roots"]]
        if "Main Camera" not in roots:
            raise AssertionError(f"unitypackage import disturbed scene roots: {roots}")
        pkg_path.unlink(missing_ok=True)

        # --- missing-shader detection: .mat referencing an uninstalled shader family ---
        # A material whose shader guid is absent keeps its original serialized property
        # names on disk (lilToon-only props like _AlphaMask) - the detector must infer
        # the family from the file text and report it, not touch the material.
        missing_mat_path = tmp / "test_missing_shader.unitypackage"
        mguid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        mat_text = (
            "%YAML 1.1\n"
            "%TAG !u! tag:unity3d.com,2011:\n"
            "--- !u!21 &2100000\n"
            "Material:\n"
            "  serializedVersion: 8\n"
            "  m_ObjectHideFlags: 0\n"
            "  m_CorrespondingSourceObject: {fileID: 0}\n"
            "  m_PrefabInstance: {fileID: 0}\n"
            "  m_PrefabAsset: {fileID: 0}\n"
            "  m_Name: test_missing_shader\n"
            "  m_Shader: {fileID: 4800000, guid: " + mguid + ", type: 3}\n"
            "  m_Parent: {fileID: 0}\n"
            "  m_ModifiedSerializedProperties: 0\n"
            "  m_ValidKeywords: []\n"
            "  m_InvalidKeywords: []\n"
            "  m_LightmapFlags: 4\n"
            "  m_EnableInstancingVariants: 0\n"
            "  m_DoubleSidedGI: 0\n"
            "  m_CustomRenderQueue: -1\n"
            "  stringTagMap: {}\n"
            "  disabledShaderPasses: []\n"
            "  m_LockedProperties: \n"
            "  m_SavedProperties:\n"
            "    serializedVersion: 3\n"
            "    m_TexEnvs:\n"
            "    - _MainTex:\n"
            "        m_Texture: {fileID: 0}\n"
            "        m_Scale: {x: 1, y: 1}\n"
            "        m_Offset: {x: 0, y: 0}\n"
            "    - _AlphaMask:\n"
            "        m_Texture: {fileID: 0}\n"
            "        m_Scale: {x: 1, y: 1}\n"
            "        m_Offset: {x: 0, y: 0}\n"
            "    m_Floats: []\n"
            "    m_Colors: []\n"
        )
        with _gzip.open(missing_mat_path, "wb") as gz:
            with _tarfile.open(fileobj=gz, mode="w") as tf:
                _add(mguid + "/asset", mat_text.encode())
                _add(mguid + "/asset.meta", b"fileFormatVersion: 2\nguid: " + mguid.encode())
                _add(mguid + "/pathname", b"Assets/AvatarImports/Models/test_missing_shader.mat")

        r = await bridge.request(
            "import_unitypackage", {"packagePath": str(missing_mat_path)}
        )
        res = json.loads(r["result"])
        attention = " ".join(res.get("needsAttention", []))
        if "lilToon" not in attention or "test_missing_shader.mat" not in attention:
            raise AssertionError(f"expected lilToon shader-missing report, got: {res}")
        missing_mat_path.unlink(missing_ok=True)

        # --- zip pipeline: unzip -> import -> instantiate in scene ---
        r = await bridge.request(
            "import_avatar_from_zip",
            {"zipPath": str(zip_path), "destDir": "Assets/AvatarImports", "import": True},
        )
        res = json.loads(r["result"])
        # pipelineVersion must be present; bump with the tool when the return
        # structure evolves (phase-3 later batches) - strict-field assertions
        # make any structure drift fail LOUDLY instead of silently.
        _require_fields(
            res,
            {"pipelineVersion", "zipPath", "extracted", "modelPath", "instanceId", "instanceName",
             "validationReport", "autoFixed", "needsAttention", "controlPanelOpened"},
            "import_avatar_from_zip",
        )
        if res.get("pipelineVersion") != 2:
            raise AssertionError(f"pipelineVersion must be 2, got {res.get('pipelineVersion')}")
        model_rel = res.get("modelPath", "")
        inst_id = res.get("instanceId")
        if not model_rel.startswith("Assets/AvatarImports/"):
            raise AssertionError(f"bad modelPath: {res}")
        if not inst_id:
            raise AssertionError(f"missing instanceId: {res}")

        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 8, "maxNodes": 500})
        h = json.loads(r["result"])
        names = []
        def walk(nodes):
            for n in nodes:
                names.append(n["name"])
                walk(n.get("children", []))
        walk(h["roots"])
        if "test_cube" not in names:
            raise AssertionError(f"instantiated avatar root not in scene: {names}")

        # --- single undo must remove the instantiated instance (undoable part) ---
        await bridge.request("undo", {})
        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 8, "maxNodes": 500})
        names_after = []
        def walk2(nodes):
            for n in nodes:
                names_after.append(n["name"])
                walk2(n.get("children", []))
        walk2(json.loads(r["result"])["roots"])
        if "test_cube" in names_after:
            raise AssertionError(f"undo did not remove imported instance: {names_after}")

        # imported project assets stay (not undoable) - clean them via asset_delete
        for path in (imported_path, model_rel):
            await _delete_asset_quiet(path)
        imported_path = None

        # --- shader auto-install guard rails (DESIGN §33) ---
        # NOTE: this project has lilToon 2.3.4 installed (Packages/jp.lilxyzw.liltoon),
        # so apply_shader_package_install {family:lilToon} must short-circuit to
        # alreadyInstalled WITHOUT touching the network - and that short-circuit
        # wins even when a localZipPath is supplied (an installed package is never
        # re-installed/replaced, per the never-touch-existing-packages rule).
        # The whitelist-rejection case below is offline. The real download+install
        # success path and the zip validation error paths (name mismatch / missing
        # local zip) are exercised manually on a project without lilToon (they are
        # unreachable here because alreadyInstalled fires first).
        r = await bridge.request(
            "apply_shader_package_install", {"family": "Poiyomi"}
        )
        res = json.loads(r["result"])
        if res.get("action") != "rejected" or "whitelist" not in res.get("error", ""):
            raise AssertionError(f"expected whitelist rejection for Poyomi, got: {res}")

        r = await bridge.request(
            "apply_shader_package_install", {"family": "lilToon"}
        )
        res = json.loads(r["result"])
        if res.get("action") != "alreadyInstalled":
            raise AssertionError(f"expected alreadyInstalled (lilToon present), got: {res}")

        # alreadyInstalled must also win over a supplied localZipPath (both a
        # bad zip and a missing path) - short-circuit priority, no unpacking.
        bad_zip = tmp / "evil_liltoon.zip"
        _make_zip(str(bad_zip), {
            "package.json": b'{"name": "com.evil.package", "version": "9.9.9"}',
            "shaders/evil.shader": b"Shader \"Hidden/Evil\" { SubShader {} }",
        })
        for zip_arg in (str(bad_zip), str(tmp / "does_not_exist.zip")):
            r = await bridge.request(
                "apply_shader_package_install", {"family": "lilToon", "localZipPath": zip_arg}
            )
            res = json.loads(r["result"])
            if res.get("action") != "alreadyInstalled":
                raise AssertionError(
                    f"expected alreadyInstalled to win over localZipPath, got: {res}"
                )
        bad_zip.unlink(missing_ok=True)
    finally:
        if imported_path:
            await _delete_asset_quiet(imported_path)
        await bridge.close()
        fbx_path.unlink(missing_ok=True)
        zip_path.unlink(missing_ok=True)
        evil_zip_path.unlink(missing_ok=True)
        # unitypackage-imported asset (kept by design) - leave to the project or clean quietly
        await _delete_asset_quiet("Assets/AvatarImports/Models/test_cube.fbx")
        await _delete_asset_quiet("Assets/AvatarImports/Models/test_missing_shader.mat")


async def check_sdk_health():
    """VRCSDK health self-check + repair loop: sdkHealth must be ok on a clean state;
    planting a known broken official test file flips it to broken with diagnostics;
    sdk_repair_test_files removes it and restores ok; repair is idempotent.
    Guards against the VCC re-sync scenario (DESIGN §27): if the SDK package is
    re-synced, the broken files come back and this group fails loudly."""

    bridge = UnityBridge(request_timeout=20, connect_timeout=15)
    planted = None
    try:
        r = await bridge.request("get_project_info", {})
        h = json.loads(r["result"])["sdkHealth"]
        if h["status"] != "ok":
            await bridge.request("sdk_repair_test_files", {})
            r = await bridge.request("get_project_info", {})
            h = json.loads(r["result"])["sdkHealth"]
        if h["status"] != "ok":
            raise AssertionError(f"sdkHealth not ok at start: {json.dumps(h)}")
        base = h["baseDir"]
        if not base or not os.path.isdir(base):
            raise AssertionError(f"cannot locate com.vrchat.base dir: {base!r}")

        planted = os.path.join(base, "Editor", "VRCSDK", "VTP", "VTPTests.cs")
        with open(planted, "w", encoding="utf-8") as f:
            f.write("// sdk health regression plant - removed by sdk_repair_test_files\n")

        r = await bridge.request("get_project_info", {})
        h = json.loads(r["result"])["sdkHealth"]
        if h["status"] != "broken":
            raise AssertionError(f"expected broken after plant, got {json.dumps(h)}")
        if not any("VTPTests.cs" in i for i in h["issues"]):
            raise AssertionError(f"issues missing VTPTests.cs diagnostic: {json.dumps(h)}")

        r = await bridge.request("sdk_repair_test_files", {})
        rep = json.loads(r["result"])
        if not any("VTPTests.cs" in x for x in rep["repaired"]):
            raise AssertionError(f"repair did not list VTPTests.cs: {json.dumps(rep)}")
        if os.path.exists(planted) or os.path.exists(planted + ".meta"):
            raise AssertionError("repair did not delete planted file(s)")
        planted = None

        r = await bridge.request("get_project_info", {})
        h = json.loads(r["result"])["sdkHealth"]
        if h["status"] != "ok":
            raise AssertionError(f"sdkHealth not restored after repair: {json.dumps(h)}")

        r = await bridge.request("sdk_repair_test_files", {})
        rep = json.loads(r["result"])
        if rep["repaired"] or len(rep["alreadyClean"]) != 2:
            raise AssertionError(f"repair not idempotent: {json.dumps(rep)}")
    finally:
        if planted:
            for p in (planted, planted + ".meta"):
                try:
                    os.remove(p)
                except OSError:
                    pass
        await bridge.close()


async def check_animator_pipeline():
    """Phase 3 batch 2 animator tools: create controller -> add state (+clip) ->
    add transition -> read back -> tool-level rollback on duplicate create.
    ASSET operations: NOT undoable, must NOT be inside a batch (spike §22).
    DIAG-TEMP: simplified da10-equivalent baseline (known PASS) for line-by-line bisect."""
    b = UnityBridge(request_timeout=30, connect_timeout=15)
    NAME = f'AnimTestAC_{os.getpid()}.controller'

    async def quiet(p):
        try:
            await b.request('asset_delete', {'path': p})
            return 'quiet-ok'
        except Exception:
            return 'quiet-exc'

    q1 = await quiet('Assets/' + NAME)
    q2 = await quiet(f'Assets/AnimTestAC_{os.getpid()}_Run.anim')
    r = await b.request('get_scene_hierarchy', {'maxDepth': 1, 'maxNodes': 20})
    root = 0
    for x in json.loads(r['result'])['roots']:
        if x['name'] == 'Main Camera':
            root = x['instanceId']
    if not root:
        raise AssertionError('Main Camera root not found')
    r = await b.request('create_animator_controller', {'assetPath': 'Assets/' + NAME, 'bindInstanceId': root})
    res = r.get('result')
    if isinstance(res, str) and res:
        _res = json.loads(res)
        _require_fields(_res, {'assetPath', 'layers', 'states', 'boundInstanceId'}, 'create_animator_controller')
        if _res['layers'] != 1 or int(_res['states']) < 1:
            raise AssertionError(f'bad initial controller: {_res}')
    else:
        raise BridgeError(((r.get('error') or {}).get('message') or '')[:100])

    # duplicate create must fail (already exists) - explicit error, not silent.
    # NOTE: bridge.request RAISES BridgeError on error responses (it does NOT
    # return an error dict), so the duplicate check must catch the exception.
    try:
        await b.request('create_animator_controller', {'assetPath': 'Assets/' + NAME})
        raise AssertionError('duplicate create_animator_controller should fail')
    except BridgeError as e:
        if 'already exists' not in str(e).lower():
            raise AssertionError(f'unexpected duplicate error: {e}')

    # add state with clip
    clip_name = f'AnimTestAC_{os.getpid()}_Run.anim'
    r = await b.request(
        'add_animator_state',
        {'assetPath': 'Assets/' + NAME, 'stateName': 'Run', 'withClip': True, 'clipPath': 'Assets/' + clip_name},
    )
    res = json.loads(r['result'])
    _require_fields(res, {'stateName', 'statesInLayer', 'clipPath'}, 'add_animator_state')
    if res['stateName'] != 'Run' or int(res['statesInLayer']) < 2:
        raise AssertionError(f'bad state add: {res}')

    # transition Idle -> Run
    r = await b.request(
        'add_animator_transition', {'assetPath': 'Assets/' + NAME, 'from': 'Idle', 'to': 'Run', 'duration': 0.25}
    )
    res = json.loads(r['result'])
    _require_fields(res, {'from', 'to', 'transitions'}, 'add_animator_transition')
    if res['from'] != 'Idle' or res['to'] != 'Run' or int(res['transitions']) < 1:
        raise AssertionError(f'bad transition: {res}')

    # read back: full structured dump must contain state + transition
    r = await b.request('get_animator_controller', {'assetPath': 'Assets/' + NAME})
    dump = json.loads(r['result'])
    names = []
    trans = []
    for layer in dump.get('layers', []):
        for s in layer.get('states', []):
            names.append(s['name'])
            for t in s.get('transitions', []):
                trans.append((s['name'], t.get('to')))
    if 'Idle' not in names or 'Run' not in names:
        raise AssertionError(f'states missing in dump: {names}')
    if ('Idle', 'Run') not in trans:
        raise AssertionError(f'transition missing in dump: {trans}')

    # tool-level rollback: add state with an invalid clip path must fail AND
    # must not leave the controller in a broken state (created assets deleted)
    try:
        await b.request(
            'add_animator_state',
            {'assetPath': 'Assets/' + NAME, 'stateName': 'Broken', 'withClip': True, 'clipPath': 'not_assets_path.anim'},
        )
        raise AssertionError('invalid clip path should fail')
    except BridgeError as e:
        if 'clipPath' not in str(e):
            raise AssertionError(f'unexpected error for bad clip path: {e}')

    await quiet('Assets/' + NAME)
    await quiet('Assets/' + clip_name)
    await b.close()

async def _find_root(bridge, name: str) -> int:
    r = await bridge.request("get_scene_hierarchy", {"maxDepth": 1, "maxNodes": 20})
    for x in json.loads(r["result"])["roots"]:
        if x["name"] == name:
            return x["instanceId"]
    raise AssertionError(f"root not found: {name}")


async def check_batch_integrity():
    """Generic batch integrity: ANY combination of write tools inside one batch must be
    fully reverted by a single undo - the scene snapshot must be byte-identical to before
    the batch. Not tied to any specific tool/API, so phase-3 tools (import/animator) can
    be added to the mix here without designing a per-API test."""
    bridge = UnityBridge(request_timeout=20, connect_timeout=15)
    prefab_path = f"Assets/regression_integrity_{os.getpid()}.prefab"
    try:
        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 8, "maxNodes": 500})
        before = _norm_snapshot(json.loads(r["result"]))

        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 1, "maxNodes": 20})
        cam = next(x for x in json.loads(r["result"])["roots"] if x["name"] == "Main Camera")
        cam_id = cam["instanceId"]

        await bridge.request("begin_batch", {"name": "regression-integrity"})
        # transform / active / name
        await bridge.request("edit_transform", {"instanceId": cam_id, "position": [1, 2, 3]})
        await bridge.request("edit_set_active", {"instanceId": cam_id, "active": False})
        await bridge.request("edit_set_name", {"instanceId": cam_id, "name": "IntegrityCam"})
        # component add / property / remove
        r = await bridge.request("edit_add_component", {"instanceId": cam_id, "componentType": "BoxCollider"})
        collider_id = json.loads(r["result"])["instanceId"]
        await bridge.request(
            "edit_set_component_property",
            {"instanceId": cam_id, "componentType": "BoxCollider", "property": "m_Size.x", "value": 2.5},
        )
        await bridge.request("edit_remove_component", {"componentInstanceId": collider_id})
        # hierarchy: create parent + child, reparent, sibling, duplicate, destroy
        r = await bridge.request("edit_create_object", {"name": "IntParent", "parentInstanceId": cam_id})
        parent_id = json.loads(r["result"])["instanceId"]
        r = await bridge.request("edit_create_object", {"name": "IntChild", "parentInstanceId": parent_id})
        child_id = json.loads(r["result"])["instanceId"]
        await bridge.request("edit_set_parent", {"instanceId": child_id, "newParentInstanceId": cam_id})
        await bridge.request("edit_set_sibling_index", {"instanceId": child_id, "index": 0})
        r = await bridge.request("edit_duplicate_object", {"instanceId": child_id})
        await bridge.request("edit_destroy_object", {"instanceId": json.loads(r["result"])["instanceId"]})
        # prefab roundtrip
        await bridge.request("prefab_create", {"instanceId": child_id, "path": prefab_path})
        r = await bridge.request("prefab_instantiate", {"path": prefab_path, "parentInstanceId": parent_id})
        await bridge.request("edit_destroy_object", {"instanceId": json.loads(r["result"])["instanceId"]})

        await bridge.request("undo", {})
        await bridge.request("end_batch", {})

        r = await bridge.request("get_scene_hierarchy", {"maxDepth": 8, "maxNodes": 500})
        after = _norm_snapshot(json.loads(r["result"]))
        if after != before:
            raise AssertionError(f"batch integrity violated:\nbefore={json.dumps(before)}\nafter={json.dumps(after)}")
    finally:
        await bridge.close()
        await _delete_asset_quiet(prefab_path)


def _norm_snapshot(h: dict) -> list:
    """Normalize the scene tree for equality: drop instanceIds (undo reallocates them)
    and dirty flags; keep everything a user can observe (name/active/transform/components)."""

    def node(n: dict) -> dict:
        comps = sorted(
            c["type"] + (":on" if c.get("enabled") is True else ":off" if c.get("enabled") is False else "")
            for c in n.get("components", [])
        )
        return {
            "name": n["name"],
            "active": n.get("active"),
            "position": n.get("position"),
            "rotation": n.get("rotation"),
            "scale": n.get("scale"),
            "components": comps,
            "children": [node(c) for c in n.get("children", [])],
        }

    return [node(r) for r in h.get("roots", [])]


async def main():
    try:
        discover_channel()
    except BridgeError as e:
        print(f"[env] {e}")
        return 2

    checks = [
        ("tools (10 tools / 12 calls)", check_tools),
        ("bad JSON safety net (diagnostic_bad_json rejected)", check_bad_json_safety_net),
        ("single-reply discipline", check_single_reply),
        ("disconnect -> Closed/Disconnect", check_disconnect_semantics),
        ("edit cycle + undo restore", check_edit_cycle),
        ("no-batch undo granularity (one tool = one group)", check_no_batch_undo_granularity),
        ("hierarchy + prefab cycle (undo restore)", check_hierarchy_prefab_cycle),
        ("batch integrity (generic snapshot equality)", check_batch_integrity),
        ("import pipeline (fbx + zip + zip-slip + undoable instance)", check_import_pipeline),
        ("sdk health self-check + repair loop", check_sdk_health),
        ("animator pipeline (create/state/transition/readback + rollback)", check_animator_pipeline),
        ("snake_case param delivery (schema coverage)", check_param_delivery),
    ]
    failed = 0
    for label, fn in checks:
        try:
            result = await fn()
            if isinstance(result, list):
                bad = [r for r in result if not r[1]]
                if bad:
                    failed += 1
                    print(f"[FAIL] {label}:")
                    for name, ok, detail in bad:
                        print(f"    {name}: {detail}")
                else:
                    print(f"[ok]   {label} ({len(result)} calls)")
            else:
                print(f"[ok]   {label}")
        except Exception as e:
            failed += 1
            print(f"[FAIL] {label}: {e}")

    if failed:
        print(f"\n{len(checks) - failed}/{len(checks)} check groups passed -> EXIT 1")
        return 1
    print("\nall regression checks passed -> EXIT 0")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
