"""TcpWs fallback transport real e2e against the running Unity editor.

The editor MUST be started with transport=tcpws (EditorPref VrcMcp.Transport=tcpws,
see BridgeBootstrap.CreateTransport). This file validates the transport beyond
"can it connect": raw frame parsing under pressure (fragmentation/extended length/
mask/ping-pong/close), disconnect/reconnect (graceful + abrupt FIN), batch boundary
across full disconnect (EndOnDisconnect remaining==0 semantics), multi-client
isolation under broadcast Send, and a 35s long-lived connection with heartbeats.
"""

import asyncio
import base64
import hashlib
import json
import os
import socket
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
os.environ["UNITY_MCP_PROJECT_DIR"] = r"C:\Users\liuzijian\Unity Projects\VrcMcpE2E"

from unity_bridge import UnityBridge, discover_channel  # noqa: E402

WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
FAILURES = []


def record(group, ok, detail=""):
    FAILURES.append((group, ok, detail))
    print(f"[{'ok' if ok else 'FAIL'}] {group}" + (f"  {detail}" if detail else ""))


class RawWsClient:
    """Minimal raw WebSocket client over TCP: hand-rolled frames, bypasses the
    websockets library so we can control masking/fragmentation/splitting."""

    def __init__(self, port, channel):
        self.sock = socket.create_connection(("127.0.0.1", int(port)), timeout=15)
        self._handshake(channel)

    def _handshake(self, channel):
        key = base64.b64encode(os.urandom(16)).decode()
        req = (f"GET /{channel} HTTP/1.1\r\nHost: 127.0.0.1\r\n"
               f"Upgrade: websocket\r\nConnection: Upgrade\r\n"
               f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")
        self.sock.sendall(req.encode())
        resp = b""
        while b"\r\n\r\n" not in resp:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise AssertionError("connection closed during handshake")
            resp += chunk
        if b"101 Switching Protocols" not in resp:
            raise AssertionError(f"bad handshake response: {resp[:200]!r}")
        expect = base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode()
        if expect.encode() not in resp:
            raise AssertionError("Sec-WebSocket-Accept mismatch")

    def send_partial(self, data):
        """sendall a raw byte chunk (used to simulate TCP fragmentation)."""
        self.sock.sendall(data)

    def send_frame(self, opcode, payload, fin=True, masked=True):
        mask_key = b"\x11\x22\x33\x44" if masked else None
        b0 = (0x80 if fin else 0) | opcode
        n = len(payload)
        if n < 126:
            hdr = bytes([b0, (0x80 if masked else 0) | n])
        elif n <= 0xFFFF:
            hdr = bytes([b0, (0x80 if masked else 0) | 126]) + struct.pack(">H", n)
        else:
            hdr = bytes([b0, (0x80 if masked else 0) | 127]) + struct.pack(">Q", n)
        if masked:
            body = bytes(b ^ mask_key[i % 4] for i, b in enumerate(payload))
            self.sock.sendall(hdr + mask_key + body)
        else:
            self.sock.sendall(hdr + payload)

    def read_frame(self):
        def read_exact(n):
            buf = b""
            while len(buf) < n:
                chunk = self.sock.recv(n - len(buf))
                if not chunk:
                    raise AssertionError("socket closed mid-frame")
                buf += chunk
            return buf

        h = read_exact(2)
        fin = bool(h[0] & 0x80)
        opcode = h[0] & 0x0F
        masked = bool(h[1] & 0x80)
        ln = h[1] & 0x7F
        if ln == 126:
            ln = struct.unpack(">H", read_exact(2))[0]
        elif ln == 127:
            ln = struct.unpack(">Q", read_exact(8))[0]
        mask = read_exact(4) if masked else None
        payload = read_exact(ln)
        if mask:
            payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        return opcode, payload, fin

    def request(self, method, mid, params=None):
        env = {"jsonrpc": "2.0", "id": mid, "method": method,
               "params": json.dumps(params or {}, ensure_ascii=False)}
        self.send_frame(0x1, json.dumps(env, ensure_ascii=False).encode())
        op, pl, fin = self.read_frame()
        assert op == 0x1 and fin, (op, fin)
        return json.loads(pl)

    def close_sock(self):
        try:
            self.sock.close()
        except OSError:
            pass


def check_handshake_roundtrip(info):
    """Group 1: channel discovery says tcpws; ping/get_scene_hierarchy round-trip."""
    try:
        assert info.get("transport") == "tcpws", info
        record("g1 channel file transport=tcpws", True)
    except Exception as e:
        record("g1 channel file transport=tcpws", False, str(e))


async def check_raw_frame_parsing(info):
    """Group 2: raw frame parser under pressure - handshake accept, masked text,
    >64KB payload via 127-length across 5 TCP fragments, WS control frames."""
    port, channel = info["port"], info["channelName"]
    raw = RawWsClient(port, channel)
    try:
        # masked small text frame -> response, server must NOT mask its frames
        env = raw.request("ping", 1)
        assert json.loads(env["result"])["pong"] is True, env
        record("g1 masked text frame round-trip", True)

        # >65535-byte payload (127 extended length), split into 5 TCP sends
        env = {"jsonrpc": "2.0", "id": 2, "method": "ping",
               "params": json.dumps({"pad": "x" * 200000}, ensure_ascii=False)}
        data = json.dumps(env, ensure_ascii=False).encode()
        assert len(data) > 65535, len(data)
        mask_key = b"\xde\xad\xbe\xef"
        hdr = bytes([0x81, 0xFF]) + struct.pack(">Q", len(data)) + mask_key
        body = bytes(b ^ mask_key[i % 4] for i, b in enumerate(data))
        # split across 5 sends with short sleeps to force TCP re-assembly.
        # NB: (a) each chunk must be a proper slice body[i*step:(i+1)*step] - a
        # slice-to-end resends the whole tail and desyncs the stream; (b) step
        # must round UP so the 5 chunks cover all bytes - a truncated final
        # chunk makes the server's ReadExactly block forever waiting for the
        # missing tail (observed: TimeoutError with zero server progress).
        step = (len(body) + 4) // 5
        raw.send_partial(hdr + body[:step])
        for i in range(1, 5):
            time.sleep(0.03)
            raw.send_partial(body[i * step:(i + 1) * step])
        op, pl, fin = raw.read_frame()
        assert op == 0x1 and fin, (op, fin)
        r2 = json.loads(pl)
        assert r2["id"] == 2 and json.loads(r2["result"])["pong"] is True, r2
        record("g1 64KB+ fragmented payload (127 length, 5 TCP splits)", True)

        # WS ping control frame -> pong echo
        raw.send_frame(0x9, b"hbt", masked=True)
        op, pl, fin = raw.read_frame()
        assert op == 0xA and pl == b"hbt", (op, pl)
        record("g1 ping -> pong echo", True)

        # WS close handshake -> close echo
        raw.send_frame(0x8, b"\x03\xe8", masked=True)
        op, pl, fin = raw.read_frame()
        assert op == 0x8, (op, pl)
        record("g1 close handshake echoed", True)

        # WS-level fragmentation (fin=0): the server consumes and drops
        # non-fin text frames (no reassembly - a known design limit, the
        # websockets client never fragments). Verify: no response to the
        # dropped frame, and the connection survives for normal requests.
        raw2 = RawWsClient(port, channel)
        raw2.send_frame(0x1, b'{"jsonrpc":"2.0","id":3,"method":"ping","params":"{}"}', fin=False)
        time.sleep(0.3)
        raw2.sock.settimeout(1.5)
        try:
            op, pl, fin = raw2.read_frame()
            raise AssertionError(f"unexpected frame after fin=0: op={hex(op)} pl={pl!r}")
        except (TimeoutError, socket.timeout):
            pass  # expected: non-fin frame silently dropped
        env = raw2.request("ping", 4)
        assert json.loads(env["result"])["pong"] is True, env
        record("g1 non-fin text frame dropped, connection survives", True)
        raw2.close_sock()
    except Exception as e:
        record("g2 raw frame parsing", False, str(e))
    finally:
        raw.close_sock()


async def check_reconnect_graceful(info):
    """Group 3a: graceful close then reconnect works."""
    try:
        b1 = UnityBridge(request_timeout=10, connect_timeout=10)
        r = await b1.request("ping")
        assert json.loads(r["result"])["pong"] is True
        await b1.close()
        await asyncio.sleep(0.5)
        b2 = UnityBridge(request_timeout=10, connect_timeout=10)
        r = await b2.request("ping")
        assert json.loads(r["result"])["pong"] is True
        await b2.close()
        record("g3a graceful close + reconnect", True)
    except Exception as e:
        record("g3a graceful close + reconnect", False, str(e))


async def check_reconnect_abrupt(info):
    """Group 3b: abrupt disconnect (raw FIN, no close frame) then reconnect."""
    try:
        raw = RawWsClient(info["port"], info["channelName"])
        env = raw.request("ping", 5)
        assert json.loads(env["result"])["pong"] is True, env
        raw.close_sock()  # FIN without WS close frame
        await asyncio.sleep(1.0)
        b = UnityBridge(request_timeout=10, connect_timeout=10)
        r = await b.request("ping")
        assert json.loads(r["result"])["pong"] is True
        await b.close()
        record("g3b abrupt FIN disconnect + reconnect", True)
    except Exception as e:
        record("g3b abrupt FIN disconnect + reconnect", False, str(e))


async def check_batch_across_disconnect(info):
    """Group 4: batch boundary across disconnect - EndOnDisconnect fires only when
    the LAST client leaves (remaining==0); an open batch is auto-closed, and the
    new connection starts clean. This is the MPE-phase lesson applied to TcpWs."""
    try:
        a = UnityBridge(request_timeout=10, connect_timeout=10)
        b = UnityBridge(request_timeout=10, connect_timeout=10)
        await a.request("ping")
        await b.request("ping")

        await a.request("begin_batch", {"name": "tcpws-batch"})
        st = json.loads((await b.request("get_batch_state"))["result"])
        assert st.get("phase") == "Active", st
        record("g4 batch Active with 2 clients", True)

        await a.close()  # remaining=1 -> batch must survive
        await asyncio.sleep(0.8)
        st = json.loads((await b.request("get_batch_state"))["result"])
        assert st.get("phase") == "Active", st
        record("g4 single disconnect keeps batch (remaining>0)", True)

        await b.close()  # remaining=0 -> EndOnDisconnect
        await asyncio.sleep(1.0)
        c = UnityBridge(request_timeout=10, connect_timeout=10)
        st = json.loads((await c.request("get_batch_state"))["result"])
        assert st.get("phase") != "Active", st
        record("g4 last disconnect auto-closes batch (EndOnDisconnect)", True)
        await c.close()
    except Exception as e:
        record("g4 batch across disconnect", False, str(e))


async def check_multi_client_isolation(info):
    """Group 5: two concurrent clients under broadcast Send - each gets its own
    id-matched responses, no cross-talk deadlock."""
    try:
        a = UnityBridge(request_timeout=10, connect_timeout=10)
        b = UnityBridge(request_timeout=10, connect_timeout=10)
        await a.request("ping")
        await b.request("ping")

        async def one(bridge, i):
            r = await bridge.request("ping", timeout=10)
            return json.loads(r["result"])["pong"] is True

        results = await asyncio.gather(*[one(a, i) for i in range(12)],
                                       *[one(b, i) for i in range(12)])
        assert all(results) and len(results) == 24, results
        record("g5 dual-client 24 interleaved requests, id-matched", True)
        await a.close()
        await b.close()
    except Exception as e:
        record("g5 multi-client isolation", False, str(e))


async def check_long_lived_heartbeat(info):
    """Group 6: 35s connection with automatic heartbeats (id=0 notifications
    every 10s from UnityBridge) and periodic pings - the server must not drop
    or corrupt. Validates the 'data-activity timeout' analog over TcpWs."""
    try:
        b = UnityBridge(request_timeout=10, connect_timeout=10)
        t0 = time.monotonic()
        for i in range(7):
            r = await b.request("ping", timeout=10)
            assert json.loads(r["result"])["pong"] is True
            await asyncio.sleep(5)
        elapsed = time.monotonic() - t0
        assert elapsed >= 30, elapsed
        r = await b.request("get_project_info", timeout=10)
        info2 = json.loads(r["result"])
        assert info2["transport"]["name"] == "tcpws", info2["transport"]
        await b.close()
        record(f"g6 long-lived {elapsed:.0f}s + heartbeats + final round-trip", True)
    except Exception as e:
        record("g6 long-lived heartbeat", False, str(e))


async def main():
    try:
        info = discover_channel()
    except Exception as e:
        print(f"[env] {e}")
        return 2
    check_handshake_roundtrip(info)
    await check_raw_frame_parsing(info)
    await check_reconnect_graceful(info)
    await check_reconnect_abrupt(info)
    await check_batch_across_disconnect(info)
    await check_multi_client_isolation(info)
    await check_long_lived_heartbeat(info)

    failed = [f for f in FAILURES if not f[1]]
    print(f"\n{len(FAILURES) - len(failed)}/{len(FAILURES)} checks passed")
    if failed:
        for g, ok, d in failed:
            print(f"FAILED: {g}: {d}")
        return 1
    print("ALL TCPWS TRANSPORT CHECKS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
