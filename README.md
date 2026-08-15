# unity-vrc-mcp

Unity-LLM bridge: an MCP server that lets agent CLIs (opencode) edit VRChat
avatars inside a running Unity Editor — scene hierarchy, selection, undo
transactions, console, FBX import, Animator editing, VRCSDK health checks,
and one-click `import_avatar_from_zip`.

```
agent CLI (opencode) ──stdio──► FastMCP server (Python) ──WebSocket──► Unity Editor plugin (com.vrchat-mcp)
```

Full design: [docs/DESIGN.md](docs/DESIGN.md) (architecture, transport,
compat registry, batch/undo state machine, validation layer, import pipeline,
phases, risk register, §0 verification discipline). Start with the index at
the top of DESIGN.md to find any topic.

## Requirements

- Unity 2022.3 (tested on **2022.3.22f1c1**; early phases also verified on
  6000.4.0a2), Windows
- Python >= 3.10 via [uv](https://docs.astral.sh/uv/)

## Install

1. **Unity side.** Copy or symlink `Packages/com.vrchat-mcp` into your Unity
   project's `Packages/` folder. The bridge auto-starts; toggle via menu
   `Tools > VRChat MCP > Start/Stop Bridge`. It writes
   `<project>/.unity-mcp/channel-{pid}.json` with the ephemeral port.
   Transport is auto-selected: **MPE first** (reflection-bound), **TcpWs
   fallback** (hand-rolled WebSocket; force with EditorPref
   `VrcMcp.Transport=tcpws`).

2. **Server side.**

   ```
   cd server
   uv sync          # creates .venv (Python 3.12) with fastmcp/websockets/psutil
   uv run unity-mcp-server
   ```

3. **opencode.** Copy `opencode.example.json` → `opencode.json` (or merge the
   `mcp.unity-vrc` block) and set `UNITY_MCP_PROJECT_DIR` to your Unity
   project path. Restart opencode.

   If the project dir is ambiguous (multiple Unity instances), pin the exact
   channel file with `UNITY_MCP_CHANNEL_FILE`. Selection rule: newest channel
   file wins; mtime tie → ctime; identical tie → refuse with a pin hint.

## Tools (36, grouped)

- **Query/read**: `ping`, `get_project_info`, `get_scene_hierarchy`,
  `get_selection`, `get_object_details`, `get_console_logs`, `get_batch_state`
- **Edit** (undoable): `edit_create_object`, `edit_destroy_object`,
  `edit_duplicate_object`, `edit_set_name`, `edit_set_active`,
  `edit_set_parent`, `edit_set_sibling_index`, `edit_transform`,
  `edit_add_component`, `edit_remove_component`, `edit_set_component_property`
- **Transactions**: `begin_batch`, `end_batch`, `undo`, `redo`
- **Assets**: `asset_import_fbx`, `asset_delete`, `prefab_create`,
  `prefab_instantiate`, `save_scene`
- **Animator**: `create_animator_controller`, `add_animator_state`,
  `add_animator_transition`, `get_animator_controller`
- **Avatar/SDK**: `import_avatar_from_zip`, `import_unitypackage`,
  `apply_shader_package_install`, `open_vrc_control_panel`,
  `sdk_repair_test_files`

Destructive tools use permission prefixes (`edit_*`, `apply_*`, `save_*`,
`import_*`, `sdk_*` → ask in opencode.json). New tools/rules require an
opencode session restart to appear in the tool list.

## Notes for real usage (trial period)

- **VRCSDK health**: official SDK 3.10.x ships two unguarded test files that
  break compilation; a VCC re-sync restores them. After any VCC update, check
  `get_project_info.sdkHealth` (status `ok` expected) and run
  `sdk_repair_test_files` if `broken` (DESIGN §26/§27).
- Batch state auto-closes when the last client disconnects (`end_batch` on a
  fresh connection reports the closed state, not an error).
- Multi-client is supported (broadcast + id-matched replies), but the typical
  usage is one opencode session per editor.
- **Magenta/pink models after import**: most VRChat avatar packages depend on a
  third-party shader (lilToon/Poiyomi) that the author does not bundle (installed
  via VCC instead). If the project lacks it, materials render magenta with no
  console error. `import_unitypackage`/`import_avatar_from_zip` now report this
  in `needsAttention` (family inferred from serialized property names, e.g.
  "lilToon shader missing on N material(s) ..."), and the import pipeline
  auto-installs whitelisted families (`lilToon`) from the official VPM repo when
  missing - successes are recorded in `autoFixed`, failures stay in
  `needsAttention` with the reason appended (DESIGN §31/§33). `apply_shader_
  package_install` performs the same install manually (`family`; optional
  `localZipPath` to install from a zip instead of downloading). Already-installed
  packages are never re-installed or replaced.

## Tests

All run with `server/.venv/Scripts/python.exe`:

| Test | Needs | Covers |
|------|-------|--------|
| `run_regression.py` | running editor | 11 groups: tools, JSON safety, single-reply, disconnect, edit/undo, hierarchy/prefab, batch integrity, import pipeline, sdk health loop, animator, param delivery |
| `test_mcp_stdio_smoke.py` | none (spawns server) | 36-tool stdio smoke |
| `test_tcpws_transport.py` | editor with `VrcMcp.Transport=tcpws` | 13 raw-frame/disconnect/batch/multi-client/heartbeat checks |
| `test_discovery_strategies.py` | none | 7 multi-instance mechanism checks |
| `test_bridge_mock.py` | none | Python discovery + envelope round-trip |
| `test_e2e.py` | running editor | manual smoke of the main tools |

Compile check (batch): `D:\Unity\2022.3.22f1\Editor\Unity.exe -batchmode -quit
-projectPath "<your project>" -logFile compile.log` — then grep for `error CS`.

## Phases

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | Skeleton: transports, envelope, dispatcher, compat registry, channel handshake, batch state machine, read tools, Python server | done |
| 2 | Edit tools: transform/component/edit_* + write-validation layer | done |
| 3 | Import: FBX, animations, expressions, PhysBone, menu | done |
| 4 | run_vrchat_validation + validation_catalog.json + golden seeds | done |
| 5 | import_avatar_from_zip + open_vrc_control_panel + SDK health + control panel | done — all phases complete (36 tools, regression 11/11, TcpWs 13/13, multi-instance 7/7) |
