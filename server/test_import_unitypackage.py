"""Verify import_unitypackage: tool discovery + real package import into the project.

Run: .venv/Scripts/python.exe test_import_unitypackage.py <packagePath>
Prereq: Unity open with bridge running (editor restarted after the ImportTools.cs change).
"""
import asyncio
import json
import sys
from pathlib import Path

from fastmcp import Client
from fastmcp.client.transports import StdioTransport

SERVER_DIR = Path(__file__).parent
PY = SERVER_DIR / ".venv" / "Scripts" / "python.exe"
SERVER = SERVER_DIR / "unity_mcp_server.py"
PROJECT_DIR = r"C:\Users\liuzijian\Unity Projects\VrcMcpE2E"

PACKAGE = sys.argv[1] if len(sys.argv) > 1 else r"D:\BaiduNetdiskDownload\Hikarun3D_v1.0.unitypackage"


def text(result) -> str:
    blocks = result.content if hasattr(result, "content") else result
    return " ".join(getattr(b, "text", str(b)) for b in blocks)


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
        print(f"[tools] {len(names)}")
        if "import_unitypackage" not in names:
            raise SystemExit(f"import_unitypackage missing from {names}")

        r = await client.call_tool("import_unitypackage", {"package_path": PACKAGE})
        out = json.loads(text(r))
        print("[import_unitypackage]")
        print(json.dumps(out, indent=2, ensure_ascii=False))

        if out.get("importedCount", 0) == 0:
            raise SystemExit("nothing imported")
        prefabs = out.get("prefabs", [])
        if not prefabs:
            print("WARN: no prefabs imported (models:", out.get("models"), ")")
        else:
            print(f"[prefab] primary: {out.get('validationReport', {}).get('primaryPrefab')}")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except SystemExit:
        raise
    except Exception as e:
        print("[import_unitypackage test] failed:", e)
        raise