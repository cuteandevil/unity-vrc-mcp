"""Smoke test: connect to the FastMCP server the same way opencode does (stdio MCP client).

Verifies protocol handshake, tool discovery (25 tools), real round-trips, and safe
edit cycles (begin_batch -> edits -> undo restores the scene), incl. hierarchy +
prefab roundtrip.
Run: .venv/Scripts/python.exe test_mcp_stdio_smoke.py
Prereq: Unity open with the VrcMcp bridge running (Tools > VRChat MCP > Start Bridge).
"""
import asyncio
import json
import os
import sys
from pathlib import Path

from fastmcp import Client
from fastmcp.client.transports import StdioTransport

SERVER_DIR = Path(__file__).parent
PY = SERVER_DIR / ".venv" / "Scripts" / "python.exe"
SERVER = SERVER_DIR / "unity_mcp_server.py"

EXPECTED_TOOLS = 36
PROJECT_DIR = r"C:\Users\liuzijian\Unity Projects\VrcMcpE2E"
PREFAB_PATH = f"Assets/smoke_{os.getpid()}.prefab"


def text(result) -> str:
    blocks = result.content if hasattr(result, "content") else result
    return " ".join(getattr(b, "text", str(b)) for b in blocks)


def require_tool(names, name):
    if name not in names:
        raise SystemExit(f"expected tool '{name}' not in list: {names}")


async def main():
    transport = StdioTransport(
        command=str(PY),
        args=[str(SERVER)],
        env={"UNITY_MCP_PROJECT_DIR": PROJECT_DIR},
        cwd=str(SERVER_DIR),
    )
    async with Client(transport) as client:
        tools = await client.list_tools()
        names = sorted(t.name for t in tools)
        print(f"[tools] {len(names)}: {names}")
        if len(names) != EXPECTED_TOOLS:
            raise SystemExit(f"expected {EXPECTED_TOOLS} tools, got {len(names)}")
        require_tool(names, "import_unitypackage")

        r = await client.call_tool("ping", {})
        print("[ping]", text(r)[:120])

        r = await client.call_tool("get_scene_hierarchy", {"max_depth": 1, "max_nodes": 20})
        h = json.loads(text(r))
        cam = next((root for root in h["roots"] if root["name"] == "Main Camera"), None)
        if cam is None:
            raise SystemExit("Main Camera not found in scene hierarchy")
        cam_id = cam["instanceId"]
        print(f"[get_scene_hierarchy] camera instanceId={cam_id}")

        r = await client.call_tool("get_object_details", {"instance_id": cam_id})
        d = json.loads(text(r))
        print(f"[get_object_details] name={d['name']} components={[c['type'] for c in d['components']]}")

        r = await client.call_tool("begin_batch", {"name": "mcp-smoke-edit"})
        print("[begin_batch]", text(r)[:90])

        r = await client.call_tool("edit_transform", {"instance_id": cam_id, "position": [1, 2, 3]})
        print("[edit_transform]", text(r)[:120])

        r = await client.call_tool("edit_set_active", {"instance_id": cam_id, "active": False})
        print("[edit_set_active]", text(r)[:120])

        r = await client.call_tool("edit_set_name", {"instance_id": cam_id, "name": "SmokeCam"})
        print("[edit_set_name]", text(r)[:120])

        r = await client.call_tool("edit_add_component", {"instance_id": cam_id, "component_type": "BoxCollider"})
        print("[edit_add_component]", text(r)[:120])
        collider_id = json.loads(text(r))["instanceId"]

        r = await client.call_tool(
            "edit_set_component_property",
            {"instance_id": cam_id, "component_type": "BoxCollider", "property": "m_Size.x", "value": 2.5},
        )
        print("[edit_set_component_property]", text(r)[:120])

        r = await client.call_tool(
            "edit_remove_component", {"component_instance_id": collider_id}
        )
        print("[edit_remove_component]", text(r)[:120])

        r = await client.call_tool("undo", {})
        print("[undo] batch-rolled-back:", text(r)[:60])

        r = await client.call_tool("get_object_details", {"instance_id": cam_id})
        d = json.loads(text(r))
        ok = (
            d["name"] == "Main Camera"
            and d["activeSelf"] is True
            and d["localPosition"] == [0, 1, -10]
            and all(c["type"] != "BoxCollider" for c in d["components"])
        )
        print("[verify-undo] camera restored:", ok)
        if not ok:
            raise SystemExit("undo did not restore the scene")

        r = await client.call_tool("end_batch", {})
        print("[end_batch]", text(r)[:90])

        # hierarchy + prefab roundtrip in one batch (single undo restores scene)
        # pre-clean debris left by earlier aborted runs (no batch: each destroy = its own undo)
        r = await client.call_tool("get_scene_hierarchy", {"max_depth": 1, "max_nodes": 20})
        for root in json.loads(text(r))["roots"]:
            if root["name"].startswith("Smoke"):
                await client.call_tool("edit_destroy_object", {"instance_id": root["instanceId"]})

        r = await client.call_tool("begin_batch", {"name": "mcp-smoke-hierarchy"})
        r = await client.call_tool(
            "edit_create_object", {"name": "SmokeParent", "parent_instance_id": cam_id}
        )
        parent_id = json.loads(text(r))["instanceId"]
        print("[edit_create_object] parent_id=", parent_id)

        r = await client.call_tool(
            "edit_create_object", {"name": "SmokeChild", "parent_instance_id": parent_id}
        )
        child_id = json.loads(text(r))["instanceId"]
        print("[edit_create_object] child_id=", child_id)

        r = await client.call_tool(
            "edit_set_parent", {"instance_id": child_id, "new_parent_instance_id": cam_id}
        )
        print("[edit_set_parent]", text(r)[:110])

        r = await client.call_tool("edit_set_sibling_index", {"instance_id": child_id, "index": 0})
        print("[edit_set_sibling_index]", text(r)[:110])

        r = await client.call_tool("edit_duplicate_object", {"instance_id": child_id})
        dup_id = json.loads(text(r))["instanceId"]
        print("[edit_duplicate_object] dup_id=", dup_id)

        r = await client.call_tool("edit_destroy_object", {"instance_id": dup_id})
        print("[edit_destroy_object]", text(r)[:110])

        r = await client.call_tool("prefab_create", {"instance_id": child_id, "path": PREFAB_PATH})
        print("[prefab_create]", text(r)[:110])

        r = await client.call_tool(
            "prefab_instantiate", {"path": PREFAB_PATH, "parent_instance_id": parent_id}
        )
        inst_id = json.loads(text(r))["instanceId"]
        print("[prefab_instantiate] inst_id=", inst_id)

        r = await client.call_tool("edit_destroy_object", {"instance_id": inst_id})
        r = await client.call_tool("undo", {})
        print("[undo] hierarchy-rolled-back:", text(r)[:60])

        r = await client.call_tool("get_scene_hierarchy", {"max_depth": 1, "max_nodes": 20})
        h2 = json.loads(text(r))
        roots = [root["name"] for root in h2["roots"]]
        leftovers = [n for n in roots if n.startswith("Smoke")]
        print("[verify-undo] hierarchy restored:", not leftovers, roots)
        if leftovers:
            raise SystemExit("undo did not restore hierarchy")

        r = await client.call_tool("end_batch", {})
        print("[end_batch]", text(r)[:90])

        r = await client.call_tool("asset_delete", {"path": PREFAB_PATH})
        print("[asset_delete cleanup]", text(r)[:90])

        print("all mcp stdio smoke checks passed")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except SystemExit:
        raise
    except Exception as e:
        print("[smoke] failed:", e)
        # best-effort: delete the temp prefab via a fresh direct bridge (server
        # session may already be gone); raw unlink() would desync SourceAssetDB.
        import os

        os.environ.setdefault("UNITY_MCP_PROJECT_DIR", PROJECT_DIR)
        from unity_bridge import UnityBridge

        async def _clean():
            try:
                b = UnityBridge(request_timeout=20, connect_timeout=15)
                await b.request("asset_delete", {"path": PREFAB_PATH})
                await b.close()
            except Exception:
                pass

        asyncio.run(_clean())
        raise