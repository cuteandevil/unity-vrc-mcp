"""Mock Unity bridge test: fake channel file + fake WS server; verifies discovery,
envelope round-trip and error handling on the Python side. Run with the venv python."""

import asyncio
import json
import sys
import tempfile
import time
from pathlib import Path

import websockets

sys.path.insert(0, str(Path(__file__).parent))

from unity_bridge import UnityBridge, discover_channel  # noqa: E402


async def fake_unity(ws):
    async for raw in ws:
        env = json.loads(raw)
        if env.get("method") == "ping":
            reply = {"jsonrpc": "2.0", "id": env["id"], "result": '{"pong":true}'}
        elif env.get("method") == "fail":
            reply = {"jsonrpc": "2.0", "id": env["id"], "error": {"code": -32000, "message": "boom"}}
        else:
            reply = {"jsonrpc": "2.0", "id": env["id"], "result": "null"}
        await ws.send(json.dumps(reply))


async def main():
    import os
    tmp = Path(tempfile.mkdtemp())
    (tmp / ".unity-mcp").mkdir()
    os.environ["UNITY_MCP_PROJECT_DIR"] = str(tmp)

    # fake channel file for a live pid (us)
    info = {
        "channelName": "unity-mcp-mock",
        "port": 0,  # patched below
        "protocol": "ws",
        "projectPath": str(tmp),
        "projectName": "mock",
        "unityVersion": "6000.4.0a2",
        "transport": "mpe",
        "pid": __import__("os").getpid(),
        "startedAt": "2026-01-01T00:00:00Z",
    }

    import os

    channel_path = tmp / ".unity-mcp" / f"channel-{os.getpid()}.json"
    channel_path.write_text(json.dumps(info), encoding="utf-8")

    server = await websockets.serve(fake_unity, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    info["port"] = port
    channel_path.write_text(json.dumps(info), encoding="utf-8")

    discovered = discover_channel(project_dir=str(tmp))
    assert discovered["channelName"] == "unity-mcp-mock", discovered

    bridge = UnityBridge(request_timeout=5, connect_timeout=5)
    r = await bridge.request("ping")
    assert r["result"] == '{"pong":true}', r

    r2 = await bridge.request("get_scene_hierarchy")
    assert r2["result"] == "null", r2

    try:
        await bridge.request("fail")
        raise AssertionError("expected error envelope")
    except Exception as e:
        assert "boom" in str(e), e

    await bridge.close()
    server.close()
    print("ALL MOCK TESTS PASSED")


asyncio.run(main())