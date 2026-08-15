"""Multi-instance discovery strategy tests (mechanism layer only, per DESIGN).

Scope boundary (explicit): this validates the MECHANISM - channel file selection
under concurrent Unity editors, PID liveness, worker exclusion, ambiguity
refusal. The SEMANTIC layer (is "connect to the newest instance" the right UX?)
is deliberately out of scope and deferred to real-usage feedback.

All tests run against a fake project dir with hand-written channel files; no
running Unity needed. ctime ties cannot be manufactured naturally on NTFS, so
the exact-double-tie branch is verified by monkeypatching _read.
"""

import asyncio
import json
import os
import tempfile
import time
from pathlib import Path

import sys

sys.path.insert(0, str(Path(__file__).parent))

import unity_bridge  # noqa: E402
from unity_bridge import discover_channel, UnityBridge  # noqa: E402

FAILURES = []


def record(group, ok, detail=""):
    FAILURES.append((group, ok, detail))
    print(f"[{'ok' if ok else 'FAIL'}] {group}" + (f"  {detail}" if detail else ""))


def make_env(tmp: Path) -> Path:
    d = tmp / ".unity-mcp"
    d.mkdir()
    return d


def write_channel(channel_dir: Path, name: str, pid: int, mtime: float, project: str) -> Path:
    info = {
        "channelName": name,
        "port": 60000 + pid % 1000,
        "protocol": "ws",
        "projectPath": str(project),
        "projectName": "mock-" + name,
        "unityVersion": "2022.3.22f1c1",
        "transport": "mpe",
        "pid": pid,
        "startedAt": "2026-01-01T00:00:00Z",
    }
    p = channel_dir / f"channel-{name}.json"  # NB: must be unique per channel;
    # naming by pid would make two live channels of one editor overwrite each other
    # (previously masked three checks into false-green single-file passes).
    p.write_text(json.dumps(info), encoding="utf-8")
    os.utime(p, (mtime, mtime))
    return p


def check_pid_liveness():
    """Dead-pid channel files are skipped."""
    with tempfile.TemporaryDirectory() as td:
        cd = make_env(Path(td))
        me = os.getpid()
        write_channel(cd, "dead-instance", 99999999, time.time() - 10, td)
        write_channel(cd, "me", me, time.time(), td)
        try:
            info = discover_channel(project_dir=td)
            ok = info["channelName"] == "me"
            record("pid liveness (dead instance skipped)", ok, str(info.get("channelName")))
        except Exception as e:
            record("pid liveness (dead instance skipped)", False, str(e))


def check_newest_mtime_wins():
    """Newest mtime wins among live editors."""
    with tempfile.TemporaryDirectory() as td:
        cd = make_env(Path(td))
        me = os.getpid()
        write_channel(cd, "older", me, time.time() - 30, td)
        write_channel(cd, "newer", me, time.time(), td)
        try:
            info = discover_channel(project_dir=td)
            record("newest mtime wins", info["channelName"] == "newer", str(info.get("channelName")))
        except Exception as e:
            record("newest mtime wins", False, str(e))


def check_mtime_tie_ctime_break():
    """mtime tie broken by ctime (later write = later ctime on NTFS)."""
    with tempfile.TemporaryDirectory() as td:
        cd = make_env(Path(td))
        me = os.getpid()
        p1 = write_channel(cd, "first", me, time.time(), td)
        time.sleep(0.02)
        p2 = write_channel(cd, "second", me, time.time(), td)
        # force identical mtime; ctime differs (p2 written later)
        same = time.time()
        os.utime(p1, (same, same))
        os.utime(p2, (same, same))
        try:
            info = discover_channel(project_dir=td)
            record("mtime tie -> ctime breaks", info["channelName"] == "second", str(info.get("channelName")))
        except Exception as e:
            record("mtime tie -> ctime breaks", False, str(e))


def check_exact_double_tie_refuses():
    """Identical mtime AND ctime refuses with an ambiguity error (monkeypatched)."""
    with tempfile.TemporaryDirectory() as td:
        cd = make_env(Path(td))
        me = os.getpid()
        p1 = write_channel(cd, "a", me, time.time(), td)
        p2 = write_channel(cd, "b", me, time.time(), td)

        orig = unity_bridge._read

        def fake_read(path):
            info = orig(path)
            info["_mtime"] = 1111.0
            info["_ctime"] = 2222.0
            return info

        unity_bridge._read = fake_read
        try:
            try:
                discover_channel(project_dir=td)
                record("exact mtime+ctime tie -> BridgeError", False, "no error raised")
            except unity_bridge.BridgeError as e:
                ok = "ambiguous" in str(e)
                record("exact mtime+ctime tie -> BridgeError", ok, str(e))
        finally:
            unity_bridge._read = orig


def check_batch_worker_excluded():
    """Channel files owned by AssetImportWorker/batch processes are excluded."""
    with tempfile.TemporaryDirectory() as td:
        cd = make_env(Path(td))
        me = os.getpid()
        write_channel(cd, "worker", me, time.time() - 5, td)
        write_channel(cd, "editor", me, time.time(), td)
        orig = unity_bridge._is_batch_worker

        def fake_worker(info):
            return info["channelName"] == "worker"

        unity_bridge._is_batch_worker = fake_worker
        try:
            try:
                info = discover_channel(project_dir=td)
                record("batch worker channel excluded", info["channelName"] == "editor", str(info.get("channelName")))
            except Exception as e:
                record("batch worker channel excluded", False, str(e))
        finally:
            unity_bridge._is_batch_worker = orig


def check_explicit_pin():
    """UNITY_MCP_CHANNEL_FILE pins a specific file regardless of recency."""
    with tempfile.TemporaryDirectory() as td:
        cd = make_env(Path(td))
        me = os.getpid()
        write_channel(cd, "stale", me, time.time() - 60, td)
        pinned = write_channel(cd, "pinned", me, time.time() - 1, td)
        os.environ["UNITY_MCP_CHANNEL_FILE"] = str(pinned)
        try:
            info = discover_channel(project_dir=td)
            record("UNITY_MCP_CHANNEL_FILE pins", info["channelName"] == "pinned", str(info.get("channelName")))
        except Exception as e:
            record("UNITY_MCP_CHANNEL_FILE pins", False, str(e))
        finally:
            os.environ.pop("UNITY_MCP_CHANNEL_FILE", None)


def check_no_channel_dir():
    """Missing channel dir raises a clear BridgeError."""
    with tempfile.TemporaryDirectory() as td:
        try:
            discover_channel(project_dir=td)
            record("missing channel dir -> BridgeError", False, "no error raised")
        except unity_bridge.BridgeError as e:
            record("missing channel dir -> BridgeError", True)


def main():
    check_pid_liveness()
    check_newest_mtime_wins()
    check_mtime_tie_ctime_break()
    check_exact_double_tie_refuses()
    check_batch_worker_excluded()
    check_explicit_pin()
    check_no_channel_dir()

    failed = [f for f in FAILURES if not f[1]]
    print(f"\n{len(FAILURES) - len(failed)}/{len(FAILURES)} checks passed")
    if failed:
        for g, ok, d in failed:
            print(f"FAILED: {g}: {d}")
        return 1
    print("ALL MULTI-INSTANCE MECHANISM CHECKS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
