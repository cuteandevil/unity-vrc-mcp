# unity-vrc-mcp

Unity-LLM 桥：一个 MCP server，让任意 agent CLI 在**运行中的 Unity 编辑器**里编辑 VRChat 模型——场景层级、选中对象、undo 事务、Console、FBX 导入、Animator 编辑、VRCSDK 健康检查，以及一键 `import_avatar_from_zip`。

```
agent CLI ──stdio──► FastMCP server (Python) ──WebSocket──► Unity Editor 插件 (com.vrchat-mcp)
```

## 环境要求

- Unity 2022.3（在 **2022.3.22f1c1** 上测试；早期阶段也在 6000.4.0a2 上验证过），Windows
- Python >= 3.10，通过 [uv](https://docs.astral.sh/uv/) 管理

## 安装

1. **Unity 侧。** 把 `Packages/com.vrchat-mcp` 复制或软链到你的 Unity 项目 `Packages/` 目录。桥接自动启动；通过菜单 `Tools > VRChat MCP > Start/Stop Bridge` 开关。它会在 `<project>/.unity-mcp/channel-{pid}.json` 写入临时端口。传输自动选择：**优先 MPE**（反射绑定），**TcpWs 兜底**（手写 WebSocket；可用 EditorPref `VrcMcp.Transport=tcpws` 强制）。

2. **服务端。**

   ```
   cd server
   uv sync          # 创建 .venv（Python 3.12），安装 fastmcp/websockets/psutil
   uv run unity-mcp-server
   ```

3. **MCP 客户端。** 把 `opencode.example.json` 作为配置参考（任意支持 stdio MCP 的 agent CLI 均可），设置 `UNITY_MCP_PROJECT_DIR` 为你 Unity 项目的路径，重启 agent 会话。

   如果项目目录有歧义（多个 Unity 实例），用 `UNITY_MCP_CHANNEL_FILE` 钉死具体的 channel 文件。选择规则：最新的 channel 文件胜出；mtime 相同 → ctime；完全相同 → 拒绝并提示钉定。

## 工具（36 个，分组）

- **查询/读取**：`ping`、`get_project_info`、`get_scene_hierarchy`、`get_selection`、`get_object_details`、`get_console_logs`、`get_batch_state`
- **编辑**（可 undo）：`edit_create_object`、`edit_destroy_object`、`edit_duplicate_object`、`edit_set_name`、`edit_set_active`、`edit_set_parent`、`edit_set_sibling_index`、`edit_transform`、`edit_add_component`、`edit_remove_component`、`edit_set_component_property`
- **事务**：`begin_batch`、`end_batch`、`undo`、`redo`
- **资产**：`asset_import_fbx`、`asset_delete`、`prefab_create`、`prefab_instantiate`、`save_scene`
- **Animator**：`create_animator_controller`、`add_animator_state`、`add_animator_transition`、`get_animator_controller`
- **模型/SDK**：`import_avatar_from_zip`、`import_unitypackage`、`apply_shader_package_install`、`open_vrc_control_panel`、`sdk_repair_test_files`

破坏性工具使用权限前缀（`edit_*`、`apply_*`、`save_*`、`import_*`、`sdk_*` → 在 agent CLI 配置中请求审批）。新增工具/规则需要重启 agent 会话才会出现在工具列表里。

## 真实使用注意事项（试用期）

- **VRCSDK 健康**：官方 SDK 3.10.x 自带两个会导致编译失败的未防护测试文件；VCC 重新同步会恢复它们。每次 VCC 更新后，检查 `get_project_info.sdkHealth`（预期 `ok`），若为 `broken` 则运行 `sdk_repair_test_files`（DESIGN §26/§27）。
- 最后一个客户端断开时 batch 状态自动关闭（新连接上调用 `end_batch` 返回已关闭状态，不是错误）。
- 支持多客户端（广播 + 按 id 匹配回复），但典型用法是一个编辑器对应一个 agent 会话。
- **导入后洋红/粉色模型**：大多数 VRChat 模型包依赖第三方 shader（lilToon/Poiyomi），作者通常不打包（改用 VCC 安装）。如果项目缺少，材质会显示洋红且无 Console 报错。`import_unitypackage`/`import_avatar_from_zip` 现在会在 `needsAttention` 中报告（从序列化属性名推断家族，如 "lilToon shader missing on N material(s) ..."），导入流水线会对白名单家族（`lilToon`）从官方 VPM 源自动安装——成功记录到 `autoFixed`，失败保留在 `needsAttention` 并附带原因（DESIGN §31/§33）。`apply_shader_package_install` 可手动执行同样的安装（`family`；可选 `localZipPath` 用本地 zip 替代下载）。已安装的包绝不重装或替换。

## 测试

全部用 `server/.venv/Scripts/python.exe` 运行：

| 测试 | 需要 | 覆盖 |
|------|------|------|
| `run_regression.py` | 运行中的编辑器 | 11 组：工具、JSON 安全、单回复、断开、edit/undo、层级/prefab、batch 完整性、导入流水线、sdk 健康循环、animator、参数投递 |
| `test_mcp_stdio_smoke.py` | 无（自启 server） | 36 工具 stdio 冒烟 |
| `test_tcpws_transport.py` | 编辑器（`VrcMcp.Transport=tcpws`） | 13 项原始帧/断开/batch/多客户端/心跳检查 |
| `test_discovery_strategies.py` | 无 | 7 项多实例机制检查 |
| `test_bridge_mock.py` | 无 | Python 发现 + 信封往返 |
| `test_e2e.py` | 运行中的编辑器 | 主要工具手动冒烟 |

编译检查（批处理）：`D:\Unity\2022.3.22f1\Editor\Unity.exe -batchmode -quit -projectPath "<your project>" -logFile compile.log`，然后 grep `error CS`。

## 阶段

| 阶段 | 范围 | 状态 |
|------|------|------|
| 1 | 骨架：传输、信封、分发器、兼容注册表、channel 握手、batch 状态机、读取工具、Python 服务端 | 完成 |
| 2 | 编辑工具：transform/component/edit_* + 写校验层 | 完成 |
| 3 | 导入：FBX、动画、表情、PhysBone、菜单 | 完成 |
| 4 | run_vrchat_validation + validation_catalog.json + golden seeds | 完成 |
| 5 | import_avatar_from_zip + open_vrc_control_panel + SDK 健康 + 控制面板 | 完成——全阶段完成（36 工具，回归 11/11，TcpWs 13/13，多实例 7/7） |

## 许可证

[MIT](LICENSE)