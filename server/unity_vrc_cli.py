"""Standalone CLI for the Unity bridge - no MCP protocol involved.

Directly talks to the Unity editor plugin over WebSocket (via UnityBridge)
and prints raw JSON results. Works without any LLM / MCP client.

Usage:
    python unity_vrc_cli.py <method> [key=value ...]

    python unity_vrc_cli.py ping
    python unity_vrc_cli.py get_project_info
    python unity_vrc_cli.py get_scene_hierarchy max_depth=4
    python unity_vrc_cli.py edit_set_name instance_id=123 name=foo
    python unity_vrc_cli.py import_avatar_from_zip zip_path=C:/path/avatar.zip

Value parsing: int / float / bool / null / JSON (list, dict, quoted strings)
are auto-detected. Plain words stay strings.

Env: UNITY_MCP_PROJECT_DIR, UNITY_MCP_CHANNEL_FILE (same as the MCP server).
Exit code 0 on success, 1 on bridge/argument errors.
"""

import argparse
import asyncio
import json
import sys

from unity_bridge import BridgeError, UnityBridge


def _camel(name: str) -> str:
    first, *rest = name.split("_")
    return first + "".join(p[:1].upper() + p[1:] for p in rest)


def _parse_value(raw: str):
    if raw.startswith("[") or raw.startswith("{"):
        return json.loads(raw)
    low = raw.lower()
    if low == "true":
        return True
    if low == "false":
        return False
    if low == "null":
        return None
    if low.startswith('"') and low.endswith('"'):
        return json.loads(raw)
    try:
        return int(raw)
    except ValueError:
        pass
    try:
        return float(raw)
    except ValueError:
        pass
    return raw


def _parse_params(pairs: list[str]) -> dict:
    params = {}
    for pair in pairs:
        if "=" not in pair:
            raise argparse.ArgumentTypeError(f"expected key=value, got: {pair}")
        key, _, raw = pair.partition("=")
        params[key] = _parse_value(raw)
    return params


async def _run(method: str, params: dict) -> str:
    bridge = UnityBridge()
    try:
        env = await bridge.request(method, {_camel(k): v for k, v in params.items()})
        err = env.get("error") or {}
        if err.get("code", 0) != 0:
            raise BridgeError(json.dumps(err, ensure_ascii=False))
        raw = env.get("result")
        if raw is None:
            return "null"
        try:
            return json.dumps(json.loads(raw), ensure_ascii=False, indent=2)
        except json.JSONDecodeError:
            return raw
    finally:
        await bridge.close()


def main() -> int:
    parser = argparse.ArgumentParser(prog="unity-vrc-cli", description=__doc__)
    parser.add_argument("method", help="tool method name, e.g. get_project_info")
    parser.add_argument("params", nargs="*", metavar="key=value", help="tool parameters")
    args = parser.parse_args()

    try:
        params = _parse_params(args.params)
    except argparse.ArgumentTypeError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1

    try:
        print(asyncio.run(_run(args.method, params)))
        return 0
    except BridgeError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    sys.exit(main())