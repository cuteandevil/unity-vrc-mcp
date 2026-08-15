"""Avatar upload-readiness check against the running Unity editor.

Programmatic alternative to eyeballing the Inspector: pulls the actual
serialized values via get_object_details and asserts expected VRChat SDK
configuration. Reusable for any imported avatar - pass the root name.

Usage:
    python check_avatar_readiness.py [avatar_root_name]
"""
import asyncio
import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
os.environ["UNITY_MCP_PROJECT_DIR"] = r"C:\Users\liuzijian\Unity Projects\VrcMcpE2E"

from unity_bridge import UnityBridge  # noqa: E402

EXPECTED_LAYERS = ["Base", "Additive", "Gesture", "Action", "FX"]


def _ref(props, key):
    v = props.get(key)
    if isinstance(v, dict):
        return v.get("name")
    return v


async def check_avatar(bridge, root_name):
    results = []
    issues = []

    def ok(name, detail):
        results.append(("ok", name, detail))

    def warn(name, detail):
        results.append(("warn", name, detail))
        issues.append(f"{name}: {detail}")

    h = await bridge.request("get_scene_hierarchy", {"max_depth": 1})
    roots = json.loads(h["result"])["roots"]
    av = next((r for r in roots if r["name"] == root_name), None)
    if av is None:
        print(f"ERROR: scene root '{root_name}' not found; roots = {[r['name'] for r in roots]}")
        return 1
    print(f"found root: {av['name']} instanceId={av['instanceId']}")
    ok("scene root", f"'{root_name}' (instanceId {av['instanceId']})")

    det = await bridge.request("get_object_details",
                               {"instanceId": int(av["instanceId"]), "maxProperties": 900})
    comps = json.loads(det["result"]).get("components", [])

    desc = next((c for c in comps if c["type"] == "VRCAvatarDescriptor"), None)
    if desc is None:
        print("ERROR: VRCAvatarDescriptor not found on root")
        return 1
    ok("VRCAvatarDescriptor", "present")
    dp = desc["properties"]

    pip = next((c for c in comps if c["type"] == "PipelineManager"), None)
    if pip is None:
        warn("PipelineManager", "missing")
    else:
        bp = pip["properties"].get("blueprintId")
        if isinstance(bp, str) and bp:
            ok("PipelineManager.blueprintId", f"{bp} (update path)")
        else:
            ok("PipelineManager.blueprintId", "empty (first upload, SDK assigns new ID)")

    anim = next((c for c in comps if c["type"] == "Animator"), None)
    if anim is None:
        warn("Animator", "missing")
    else:
        ap = anim["properties"]
        avatar = _ref(ap, "m_Avatar")
        ok("Animator.m_Avatar", str(avatar) if avatar else "unset")
        ctrl = _ref(ap, "m_Controller")
        if ctrl is None:
            ok("Animator.m_Controller", "unset (correct for VRChat playable layers)")
        else:
            ok("Animator.m_Controller", str(ctrl))

    vp = dp.get("ViewPosition")
    if vp and isinstance(vp, list) and len(vp) == 3:
        if 0.4 <= vp[1] <= 2.2:
            ok("ViewPosition", f"y={vp[1]:.3f} (within 0.4-2.2)")
        else:
            warn("ViewPosition", f"y={vp[1]:.3f} outside plausible head height")
    else:
        warn("ViewPosition", "missing")

    ls = _ref(dp, "lipSync")
    if ls == "VisemeBlendShape":
        ok("lipSync", "VisemeBlendShape")
    else:
        warn("lipSync", f"expected VisemeBlendShape, got {ls}")
    vmesh = _ref(dp, "VisemeSkinnedMesh")
    if vmesh:
        ok("VisemeSkinnedMesh", vmesh)
    else:
        warn("VisemeSkinnedMesh", "unset")
    mouth = dp.get("MouthOpenBlendShapeName")
    if isinstance(mouth, str) and mouth:
        ok("MouthOpenBlendShapeName", mouth)
    else:
        warn("MouthOpenBlendShapeName", "unset")

    for i, expected in enumerate(EXPECTED_LAYERS):
        p = f"baseAnimationLayers.Array.data[{i}]"
        ctrl = _ref(dp, p + ".animatorController")
        if ctrl:
            ok(f"anim layer[{i}]", f"{expected} -> {ctrl}")
        else:
            warn(f"anim layer[{i}]", f"{expected} has no controller")

    custom = dp.get("customExpressions")
    menu = _ref(dp, "expressionsMenu")
    params = _ref(dp, "expressionParameters")
    if custom:
        if menu and params:
            ok("expressions", f"menu={menu}, params={params}")
        else:
            warn("expressions", f"customExpressions=True but menu={menu} params={params}")
    else:
        ok("expressions", "customExpressions=False")

    print(f"\n=== readiness for '{root_name}' ===")
    for status, name, detail in results:
        print(f"[{status:4}] {name}: {detail}")
    if issues:
        print(f"\n{len(issues)} WARNINGS:")
        for i in issues:
            print("  -", i)
    else:
        print("\nno warnings - avatar is upload-ready")
    return 1 if issues else 0


async def main():
    root_name = sys.argv[1] if len(sys.argv) > 1 else "Hikarun"
    bridge = UnityBridge(request_timeout=30, connect_timeout=15)
    try:
        code = await check_avatar(bridge, root_name)
    finally:
        await bridge.close()
    return code


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))