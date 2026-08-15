"""Real e2e against the running Unity editor (VrcMcpE2E)."""
import asyncio
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
os.environ["UNITY_MCP_PROJECT_DIR"] = r"C:\Users\liuzijian\Unity Projects\VrcMcpE2E"

from unity_bridge import UnityBridge  # noqa: E402


async def main():
    bridge = UnityBridge(request_timeout=20, connect_timeout=15)
    calls = [
        ("ping", {}),
        ("get_project_info", {}),
        ("get_scene_hierarchy", {"max_depth": 3, "max_nodes": 100}),
        ("get_batch_state", {}),
        ("get_console_logs", {"max": 10, "include_history": True}),
        ("begin_batch", {"name": "e2e-test"}),
        ("get_batch_state", {}),
        ("end_batch", {}),
        ("get_batch_state", {}),
        ("undo", {}),
        ("redo", {}),
    ]
    for name, params in calls:
        try:
            r = await bridge.request(name, params)
            print(f"[{name}] -> {r.get('result', r.get('error'))[:300]}")
        except Exception as e:
            print(f"[{name}] FAILED: {e}")
    await bridge.close()


asyncio.run(main())