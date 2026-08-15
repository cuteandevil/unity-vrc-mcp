"""Unity bridge client: channel-file discovery -> WebSocket connection to the Unity plugin.

Design (see docs/DESIGN.md):
- The plugin writes <project>/.unity-mcp/channel-{pid}.json containing the ephemeral
  OS-assigned port and the channel name (the last part of the WebSocket URL).
- Pid liveness is verified with psutil; stale files are skipped.
- Selection: newest mtime wins; on mtime ties, newest ctime wins; on double ties the
  bridge refuses and asks for UNITY_MCP_CHANNEL_FILE.
- UNITY_MCP_PROJECT_DIR overrides the project dir; UNITY_MCP_CHANNEL_FILE pins a file.
- The connection is persistent; a closed socket is re-established lazily on the next
  request (2s poll while connecting).
"""

import asyncio
import json
import os
import time
from pathlib import Path
from typing import Any, Optional

import psutil
import websockets

ENV_CHANNEL_FILE = "UNITY_MCP_CHANNEL_FILE"
ENV_PROJECT_DIR = "UNITY_MCP_PROJECT_DIR"
POLL_INTERVAL = 2.0
# The Unity side detects a dead client by data-activity timeout (30s in
# MpeTransport): the bridge sends this heartbeat notification every 10s so a live
# client never looks idle. Unity ignores the unknown method (notification).
HEARTBEAT_INTERVAL = 10.0
HEARTBEAT_METHOD = "unity_mcp_heartbeat"


class BridgeError(RuntimeError):
    pass


def _channel_dir(project_dir: Optional[str] = None) -> Path:
    project_dir = project_dir or os.environ.get(ENV_PROJECT_DIR)
    if project_dir:
        return Path(project_dir) / ".unity-mcp"
    return Path.cwd() / ".unity-mcp"


def _read(path: Path) -> dict:
    st = path.stat()
    # utf-8-sig tolerates the BOM that .NET Encoding.UTF8 writes
    info = json.loads(path.read_text(encoding="utf-8-sig"))
    info["_mtime"] = st.st_mtime
    info["_ctime"] = st.st_ctime
    return info


def _is_batch_worker(info: dict) -> bool:
    """True if the channel file's pid belongs to an AssetImportWorker/batch Unity process.
    Import workers also initialize the plugin and write channel files, but their MPE
    channel never dispatches replies; discovery must never pick them."""
    pid = info.get("pid", -1)
    try:
        args = psutil.Process(pid).cmdline()
    except (psutil.NoSuchProcess, psutil.AccessDenied, ValueError):
        return False
    low = " ".join(args).lower()
    return "batchmode" in low or "assetimportworker" in low


def discover_channel(project_dir: Optional[str] = None, explicit: Optional[str] = None) -> dict:
    """Finds the live channel file for a running Unity editor. Raises BridgeError."""
    if explicit:
        p = Path(explicit)
        if not p.exists():
            raise BridgeError(f"UNITY_MCP_CHANNEL_FILE {p} does not exist")
        return _read(p)

    d = _channel_dir(project_dir)
    if not d.exists():
        raise BridgeError(
            f"no channel directory {d}. Is Unity open with the VrcMcp bridge running? "
            "(Tools > VRChat MCP > Start Bridge)"
        )
    files = sorted(d.glob("channel-*.json"))
    if not files:
        raise BridgeError(f"no channel files in {d}. Start the bridge in Unity first.")

    live = []
    for f in files:
        try:
            info = _read(f)
        except (json.JSONDecodeError, OSError):
            continue
        if not psutil.pid_exists(info.get("pid", -1)):
            continue
        if _is_batch_worker(info):
            continue
        live.append(info)
    if not live:
        raise BridgeError(
            f"channel files found but no live Unity process ({d}). "
            "Restart the bridge in Unity (Tools > VRChat MCP > Start Bridge)."
        )

    live.sort(key=lambda c: (c["_mtime"], c["_ctime"]), reverse=True)
    if len(live) >= 2 and live[0]["_mtime"] == live[1]["_mtime"] and live[0]["_ctime"] == live[1]["_ctime"]:
        raise BridgeError(
            "ambiguous channels (identical mtime and ctime). "
            f"Pin one explicitly with {ENV_CHANNEL_FILE}."
        )
    return live[0]


def _ws_url(info: dict) -> str:
    port = int(info["port"])
    channel = info.get("channelName", "")
    # MPE: the channel name is the last part of the URL (docs: 127.0.0.1:9090/<channelName>).
    return f"ws://127.0.0.1:{port}/{channel}"


class UnityBridge:
    """Persistent WebSocket client to the Unity plugin. Thread-safe for one caller at a time."""

    def __init__(self, request_timeout: float = 60.0, connect_timeout: float = 12.0):
        self.request_timeout = request_timeout
        self.connect_timeout = connect_timeout
        self._ws = None
        self._info: Optional[dict] = None
        self._receiver_task: Optional[asyncio.Task] = None
        self._heartbeat_task: Optional[asyncio.Task] = None
        self._next_id = 0
        self._pending: dict[int, asyncio.Future] = {}

    @property
    def connected(self) -> bool:
        return self._ws is not None and getattr(self._ws, "state", None) is not None \
            and self._ws.state.name == "OPEN"

    @property
    def info(self) -> Optional[dict]:
        return self._info

    async def close(self) -> None:
        if self._heartbeat_task:
            self._heartbeat_task.cancel()
            self._heartbeat_task = None
        if self._receiver_task:
            self._receiver_task.cancel()
            self._receiver_task = None
        if self._ws:
            await self._ws.close()
            self._ws = None

    async def request(self, method: str, params: Optional[dict] = None,
                      timeout: Optional[float] = None) -> dict:
        """Sends an envelope request and awaits the matching response envelope."""
        await self._ensure_connected()
        self._next_id += 1
        msg_id = self._next_id
        envelope = {
            "jsonrpc": "2.0",
            "id": msg_id,
            "method": method,
            "params": json.dumps(params or {}, ensure_ascii=False),
        }
        fut: asyncio.Future = asyncio.get_running_loop().create_future()
        self._pending[msg_id] = fut
        try:
            await self._ws.send(json.dumps(envelope, ensure_ascii=False))
            env = await asyncio.wait_for(fut, timeout or self.request_timeout)
            err = env.get("error") or {}
            if err.get("code", 0) != 0:
                raise BridgeError(json.dumps(err, ensure_ascii=False))
            return env
        finally:
            self._pending.pop(msg_id, None)

    async def _ensure_connected(self) -> dict:
        deadline = time.monotonic() + self.connect_timeout
        last_error: Optional[Exception] = None
        while True:
            if self.connected:
                return self._info
            try:
                info = discover_channel()
                ws = await websockets.connect(
                    _ws_url(info),
                    open_timeout=5,
                    ping_interval=None,
                    max_size=16 * 1024 * 1024,
                )
                self._info = info
                self._ws = ws
                self._receiver_task = asyncio.create_task(self._receive_loop())
                if self._heartbeat_task is None or self._heartbeat_task.done():
                    self._heartbeat_task = asyncio.create_task(self._heartbeat_loop())
                return info
            except BridgeError as e:
                last_error = e
            except Exception as e:  # noqa: BLE001 - surface any connection error
                last_error = e
            if time.monotonic() >= deadline:
                break
            await asyncio.sleep(POLL_INTERVAL)
        raise BridgeError(f"cannot connect to Unity editor: {last_error}")

    async def _heartbeat_loop(self) -> None:
        try:
            while True:
                await asyncio.sleep(HEARTBEAT_INTERVAL)
                if not self.connected:
                    return
                try:
                    await self._ws.send(json.dumps(
                        {"jsonrpc": "2.0", "id": 0, "method": HEARTBEAT_METHOD, "params": "{}"},
                        ensure_ascii=False))
                except Exception:
                    return
        except asyncio.CancelledError:
            pass

    async def _receive_loop(self) -> None:
        ws = self._ws
        try:
            async for raw in ws:
                if isinstance(raw, bytes):
                    text = raw.decode("utf-8", errors="replace")
                else:
                    text = raw
                text = text.strip()
                if not text:
                    continue
                if text.isdigit():
                    # MPE handshake frame: the server announces the connection id
                    continue
                try:
                    env = json.loads(text)
                except json.JSONDecodeError:
                    continue
                if not isinstance(env, dict):
                    continue
                msg_id = env.get("id", 0)
                if msg_id == 0:
                    continue  # notification; not used in phase 1
                fut = self._pending.get(msg_id)
                if fut is not None and not fut.done():
                    fut.set_result(env)
        except websockets.ConnectionClosed:
            pass
        finally:
            for fut in self._pending.values():
                if not fut.done():
                    fut.set_exception(BridgeError("connection to Unity closed"))
            self._pending.clear()
            self._ws = None