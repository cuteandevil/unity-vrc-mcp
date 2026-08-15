# Unity-LLM Bridge MCP — 设计规格（v1.0）

> 本文档是 `unity-vrc-mcp` 的唯一权威规格。所有阶段交付必须与本文档一致；不一致以本文档为准并更新历史。

## 索引（2026-08-15 一次性重排完成：§12 归位、补 §30-§35；§13/§14/§15 内容原样保留）

- §0 验证操作纪律（强制）：6 条——manifest 变更/包内源码变更需重启、报错后确认干净、
  discovered N 核对、VCC 同步复位查 sdkHealth、测试可信度"先验证会红再验证会绿"
- §1 目标与范围
- §2 总体架构（三层：确定性核心/LLM 动态层/性能缓存 + 关键约束）
- §3 传输层：ITransport / TcpWs（§3.2 设计为主，实测见 §28）/ MPE（实验）/ Channel
  文件握手与生命周期（修订 6 定案，选择规则 mtime→ctime→拒绝）
- §4 Internal API 兼容台账（一等风险统一管理）
- §5 工具面：§5.1 阶段 1 已交付 / §5.2 后续阶段目录 / §5.3 权限约定（edit/apply/
  save/import/sdk 前缀 → ask）
- §6 事务与 Undo：默认语义 / Batch 状态机 / 事务回滚
- §7 写入校验层（修订 3 定案）
- §8 run_vrchat_validation（数据驱动 + 透传兜底）
- §9 import_avatar_from_zip（六条修订补丁；pipelineVersion 2 见 §26）
- §10 阶段计划（五阶段）
- §11 风险台账（TcpWs 已实测移出未验证，见 §28）
- §12 安全（已归位）
- §13 阶段 1 e2e 实测（6000.4 + MPE）
- §14 opencode stdio 接入实测（FastMCP 3.4.7）
- §15 opencode 内真实调用验证
- §16 阶段 2 编辑工具 e2e 实测
- §17 opencode 内 edit_* 审批验证
- §18 生命周期/参数解析 4 连修（回归 5/5）
- §19 阶段 2 第二批：层级 + Prefab（25 工具，回归 6/6）
- §20 通用 batch 完整性断言 + 资产清理（26 工具，7/7）
- §21 阶段 3 第一批：导入管线（28 工具，8/8）
- §22 资产侧 Undo 语义 spike（Animator 批前哨）
- §23 接口稳定性策略（演进/兼容/废弃纪律）
- §24 阶段 3 第二批：Animator 组回归修复（9/9；含 `r["error"]` 断言 ghost 教训）
- §25 JSON 构建迁移 Newtonsoft + 负测试（DLL 陈旧教训）
- §26 VRCSDK 编译阻塞根因定论 + import v2 / open_vrc_control_panel（官方包测试文件、
  删除修复、35 工具）
- §27 sdkHealth 自检 + sdk_repair_test_files（对 §26 修复方式的 VCC 重同步设防，36 工具）
- §28 TcpWs 兜底传输实测（13 项）+ 多实例机制层验证（7 项）；含服务端已知行为与
  "转正必须重新评估"触发条件
- §29 审批流完整验证（4 路径）+ 阶段 5 封顶
- §30 import_unitypackage（Hikarun 首次实装，2026-08-15）
- §31 Missing Shader 检测（lilToon 等，2026-08-15）
- §32 首次上传尝试 + SDK 控制面板提示（Hikarun，2026-08-15）
- §33 Shader 自动安装（白名单驱动；含短路优先/手动验证策略，2026-08-15）
- §34 Undo 根因（程序化编辑永不触发 mouse-up flush；flush + 组分割修复，
  2026-08-15）
- §35 asset_fs_probe 诊断工具移除（围绕已证伪理论写的历史包袱，无保留支撑；
  工具数 38→37，2026-08-15）

## 0. 验证操作纪律（强制，先于一切验证执行）

回归脚本测的是 **MCP 协议层往返**（JSON-RPC envelope → 工具响应 → Python `json.loads`）。
它**无从判断 C# 那头跑的是新编译的代码还是旧 DLL**。以下场景的"回归全绿 / smoke 全过"
**不构成验证**——必须按纪律先确认编辑器状态，否则验证结果是假的：

1. **manifest.json / package 依赖变更**（新增/升级 UPM 包）：运行中编辑器**不会自动
   re-resolve**——PackageCache 保持为空，编辑器带编译错误继续跑旧 DLL，回归依然全绿
   （§25 实测：Newtonsoft 迁移批 1/批 2 的"通过"全部是旧代码）。**必须重启编辑器**，
   确认 `Library/PackageCache/` 出现对应包 + Editor.log 无 `error CS`，然后才跑回归。
2. **Unity 报过编译错误**（Editor.log 出现 `## Script Compilation Error`）：报错期间
   编辑器继续运行**上一次成功编译的程序集**。即使后续源码已修好，也要确认控制台干净
   再验证；保守做法同样是重启。
3. 验证前检查（每次都要）：Editor.log 无 `error CS` / `Script Compilation Error`；
   若日志中有 `[VrcMcp] discovered N tools`，用 N 与预期工具数核对（新代码的标志）。
4. **包内源码变更（新增/修改/删除 .cs）后必须重启编辑器**：§25 实测 6000.4 对 file: 引用包
   源码变更会自动重编译，但本机 2022.3.22f1 **不触发**（DLL 时间戳停留旧值，编辑器继续
   跑旧代码，回归/直连全部"通过"于旧 DLL）。重启后核对 `discovered N tools` 计数变化。
5. **VCC 重新同步 / SDK 版本升级可能把官方包内已知坏文件覆盖回来**（§27）：验证 VRCSDK
   相关功能前必须查 `get_project_info.sdkHealth.status`，非 `ok` 时先
   `sdk_repair_test_files` 再继续（这是检测，不是靠人记住"哪两个文件"）。
6. **测试基础设施自身可信度**（两次独立案例：§24 的 `r["error"]` 断言误用 ghost、
   §28 通道文件 pid 命名假绿——共同点是测试代码的 bug 让测试提前"以为通过"或
   "跳过该测的东西"，回归绿了反而停止怀疑）：**任何新回归组/新断言，第一次运行
   前先故意让被测对象处于"应该失败"的状态，确认测试真的会红，再拿去测正常路径**
   （"先验证会红，再验证会绿"）。绿色结果不是可信度的证明，测试能红才是。

这是结构盲区，不是流程瑕疵：第 4 次踩中（MPE 幂等 → Undo batch 语义 → DLL 陈旧）
后总结为纪律，第 5、6 次起按纪律执行（第 5 次：file: 包新增 .cs 未重启；第 6 次：
VCC 同步复位坏文件风险）。违反本条的任何"验证通过"记录一律视为无效。

## 1. 目标与范围

让 agent CLI（opencode 等）通过 MCP 对 Unity Editor 进行 **VRChat 模型编辑**：场景/GameObject/组件/资产编辑、FBX 导入配置、Animator/Expressions/PhysBone、`import_avatar_from_zip` 一键导入流水线。终点为"可手动上传状态"。

**不包含**：VRChat 账户自动化上传（官方不支持）、远程文件上传（MCP server 与 Unity 同机，只接受本地路径）。

## 2. 总体架构

```
opencode (agent CLI)
   │  MCP stdio (JSON-RPC 2.0)
   ▼
Python FastMCP server (server/)
   │  WebSocket (自定义 JSON envelope {jsonrpc,id,method,params,result,error})
   ▼
Unity Editor (com.vrchat-mcp Editor 包)
   ├─ ITransport        ← TcpWsTransport(手写 WS，主) / MpeTransport(反射，实验)
   ├─ MainThreadDispatcher  ← EditorApplication.update 泵命令队列
   ├─ McpToolRegistry   ← [McpTool] 特性 + 反射自动发现
   ├─ InternalApiRegistry  ← internal API 反射统一台账
   ├─ BatchStateMachine ← Undo 批量事务状态机
   └─ 工具适配器
```

### 关键约束

- **所有 Unity API 只允许在主线程**。传输层事件一律经 `MainThreadDispatcher.Enqueue` 上抛，工具执行在 update 泵内完成。
- **envelope 与传输解耦**：`McpToolRegistry`/`MainThreadDispatcher`/`BatchStateMachine` 只依赖 `ITransport` 与 `JsonRpcEnvelope`，换传输 = 换一行注册代码。
- 对象寻址：场景内用 `instanceId`，资产用 `assetPath/GUID`。

## 3. 传输层

### 3.1 ITRANSPORT 接口

```csharp
public interface ITransport : IDisposable {
    event Action<string> MessageReceived;  // 客户端原始 JSON（上抛前已 marshal 到主线程）
    event Action ClientDisconnected;
    void Start(); void Stop();
    bool IsRunning { get; }
    void Send(string json);
    string Name { get; }
}
```

### 3.2 TcpWsTransport（主，版本免疫）

`TcpListener(IPAddress.Loopback, 0)`（OS 分配端口）+ 手写最小 WebSocket 服务端（握手/掩码/分帧/close/ping-pong），~250 行，零第三方依赖，不碰 MPE 实验性 API。**默认传输**。

### 3.3 MpeTransport（实验，反射绑定）

`UnityEditor.MPE.ChannelService` 经反射绑定（本机无 Unity，签名按源码记忆实现，见 §11 待验证项）。绑定策略：候选方法名列表逐个尝试，成功记录绑定路径，全部失败则 `unavailable` 并给出原因。**仅当显式配置 `mpe` 或 `auto` 且绑定成功时启用**。

### 3.4 Channel 文件握手与生命周期（修订 6 定案）

- Unity 端启动后写 `<project>/.unity-mcp/channel-{unityPid}.json`：
  ```json
  { "channelName": "unity-mcp-<sanitizedProject>", "port": 8080, "protocol": "ws",
    "projectPath": "...", "unityVersion": "...", "transport": "tcpws",
    "compatStatus": {...}, "pid": 12345, "startedAt": "..." }
  ```
- channel 名：`unity-mcp-{projectName}`，过滤为 `[A-Za-z0-9_-]`，截断 48 字符。
- **清理**：`EditorApplication.quitting` + `AssemblyReloadEvents.beforeAssemblyReload` 删除自身文件；启动时顺手清理 pid 已死的残留文件。
- **server 端**：读文件先做 `psutil.pid_exists` 存活校验，不存活忽略该文件继续找下一个；多存活实例 mtime 打平用 ctime tie-break，仍打平则报歧义并提示 `UNITY_MCP_CHANNEL_FILE` 显式指定。轮询（2s）监听文件变化 → 重连。

## 4. internal API 兼容台账（一等风险，统一管理）

所有反射访问点集中到 `Editor/Compat/InternalApiRegistry`，启动自检：失败 → warning + `get_project_info.compat[]` 标记 `available:false` + 原因。**优先公共 API，反射仅做增强**。

| API | 用途 | 访问方式 | 版本敏感点 | 回退 |
|---|---|---|---|---|
| `UnityEditor.LogEntries` | 历史 Console | 反射 GetType+缓存 MethodInfo | 方法签名 2019→2022→6.x 多次变动 | 公共 API `Application.logMessageReceived` 实时捕获为主 |
| `UnityEditor.MPE.ChannelService` | 实验传输 | 反射候选绑定 | 签名/事件名跨版本漂移 | TcpWsTransport（默认） |
| VRC SDK 校验入口 | run_vrchat_validation | 反射（阶段 4） | SDK 版本间路径变动 | 菜单项 + Console 捕获 |

兼容矩阵沉淀于 `docs/compat-matrix.md`（阶段 4 前建立）。

## 5. 工具面

### 5.1 阶段 1（已交付）

| 工具 | 说明 |
|---|---|
| `ping` | 连通性 + editor 状态 |
| `get_project_info` | 项目信息 + transport + compat 状态 + VRCSDK 安装状态 + batch 状态 |
| `get_scene_hierarchy` | 场景树（instanceId/组件/Transform），maxDepth 默认 8，上限 2000 节点 |
| `get_selection` | 当前选中对象 |
| `begin_batch` / `end_batch` | 显式 Undo 事务（见 §6） |
| `get_batch_state` | batch 状态机详情 |
| `get_console_logs` | 实时环形缓冲 + 历史（compat 尽力而为） |
| `undo` / `redo` | 编辑器级撤销/重做 |

### 5.2 后续阶段目录（阶段 2-4 交付，顺序见 §10）

读：`get_fbx_import_settings`、`get_avatar_descriptor`、`get_expression_parameters`、`get_expression_menu`、`get_physbones`、`get_animator_controller`、`get_materials`、`enumerate_blendshapes`、`inspect_component`。

写：`create_gameobject`、`delete_gameobject`、`duplicate`、`rename`、`reparent`、`set_transform`、`add_component`、`remove_component`、`set_component_property`、`set_active`、`instantiate_prefab`、`create_prefab`、`apply_prefab_instance`、`reimport_asset`、`create_material`、`set_material_property`、`set_model_import_settings`、`set_blendshape_value`、`create_animator_controller`、`add_parameter`、`create_state`、`add_transition`、`set_state_motion`、`configure_avatar_descriptor`、`add_expression_parameter`、`add_expression_menu_item`、`add_physbone`、`run_vrchat_validation`、`collect_validation_patterns`、`open_vrc_control_panel`、`import_avatar_from_zip`、`play_mode`、`screenshot_scene_view`、`execute_menu_item`。

### 5.3 权限约定

写工具一律 `edit_*/apply_*/save_*/import_*` 前缀，供 agent CLI 权限规则按前缀强制审批；场景保存/资产导入/Play 模式走非阻塞审批（§6.3）。

## 6. 事务与 Undo

### 6.1 默认语义

一次工具调用 = 一个 Undo group（`Undo.IncrementCurrentGroup` + atomic group）。连续 5 次 `set_transform` 想合并，必须显式 `begin_batch`/`end_batch`。

### 6.2 Batch 状态机（三维拆解，修订定案）

```
batch: open ──┬─> active ──────────────┬─> closed (end_batch / 断连 / 超时)
              │     ▲                  │
              │     └─ 每完成一个工具调用重置 idle 计时器
              └─> awaiting_approval ───┘
                    (idle 计时器暂停)
```

- **断连**：`ITransport.ClientDisconnected` → 立即 `end_batch`（不等超时）。
- **卡顿**：idle 计时器 10 分钟（常量 `IdleTimeoutSeconds`），每次工具调用完成时重置，仅覆盖"连接在但 agent 忘了 end_batch"。
- **审批暂停**：审批是非阻塞自定义流程（非 Unity 模态框）。工具进 `awaiting_approval` 即暂停计时器，返回 `{status:"awaiting_approval"}`；用户决定后恢复。
- 超时触发：自动 close + 发送通知 `notifications/batch_auto_closed {reason:"idle_timeout"}`。
- `end_batch` 语义：`Undo.CollapseUndoOperations(groupIndex)`。

### 6.3 事务回滚（import_avatar_from_zip 等创建类操作）

双层回滚：**Undo group 管场景状态**（`RegisterCreatedObjectUndo`/`RecordObject`），**显式 `AssetDatabase.DeleteAsset` 管磁盘上新建的资产文件**（Undo 不删已写盘文件）。两层缺一不可。

## 7. 写入校验层（修订 3 定案）

`set_component_property` 等通用写工具，写入前校验：

- 约束三路合并：SerializedProperty 自带 min/max → 字段反射 `RangeAttribute` → 内置规则表（Transform scale 上限、layer 0–31、Animator layer 索引 < layerCount、NaN/Infinity 全局拦截、枚举白名单）。
- 校验失败不执行，返回 `{applied:false, reason, property, expected}` + Console 错误摘录。
- `inspect_component` 返回每条 property path 的 `{type, value, constraints, enumOptions}`，让 LLM 先读约束再写。

## 8. run_vrchat_validation（数据驱动 + 透传兜底）

输出：`{passed, errors:[{severity, code?, message, relatedAssets[], hint?, raw}], unknownPatterns:[...], catalogVersion, sdkVersion}`。

- 映射表 `validation_catalog.json`（regex → code/severity/hint）：随包分发、项目可覆盖、按 VRC SDK 版本归档。
- **未匹配消息不丢弃**：进 `unknownPatterns` 原样透传，LLM 仍可解读。
- 自愈回路：`collect_validation_patterns` 快照导出未知消息，供人工回填 catalog。
- 种子策略：阶段 4 golden 测试（5 个损坏模型：PhysBone 超限、菜单深度超限、未授权表达式参数、姿态动画缺失、BlendShape 映射错误）的报错消息直接落为 catalog 种子条目；交付标准：种子条目 ≥5 且回归通过、`unknownPatterns == 0`。

## 9. import_avatar_from_zip（含六条修订补丁）

**入参**：`zip_path`（本地绝对路径）、`avatar_name`（默认取 zip 文件名）、`options`（`rig_type` 默认 humanoid、`skip_expressions` 默认 false、`overwrite` 默认 false）。

全量流水线（模型+动画+口型场景全开）：

1. **安全解压** → `Assets/Avatars/<name>/`
   - zip-slip 路径穿越防护 + 文件名校验
   - **zip 炸弹防护（补丁 1）**：解压时按条目累加实际写入字节数，>3GB 立即中止并清理已写入部分；条目数 >5000 直接拒绝
   - **命名冲突（补丁 5）**：`Assets/Avatars/<name>/` 已存在 → 默认拒绝，须显式 `overwrite: true` 或换名，避免新旧残留混入导致回滚边界不清
2. **FBX 导入**：Humanoid 自动映射（失败返回缺失骨骼清单，不静默降级 Generic）、Import BlendShapes 开启、clips 按 take 拆分、材质 remap
3. **装配**：生成 `<name>.prefab` + 场景实例化
4. **VRCAvatarDescriptor**：ViewPosition=头部、LipSync=Viseme + 口型映射（补丁 3：**命名映射走 `viseme_naming_catalog.json` 数据驱动**，不写死 if-else；检测不到标准集 → needsAttention + 项目级可覆盖）、Avatar Scale
5. **Animator**：base+FX 层、拆分 clips 落成状态机
6. **材质/Shader**：lilToon/Poiyomi 优先，否则 VRChat/Mobile/Toon Lit；贴图命名启发式绑定（补丁 4：**绑不上 → needsAttention 记录，不猜**）
7. **Expressions**（补丁 2：**生成前预算计算**）：VRC Expression Parameters 总 bit 预算（bool=1 / int=8 / float=8，总数上限以安装的 VRC SDK 文档为准，实现时核验）。生成前算好每个 toggle 的 bit 成本，超预算只生成前 N 个，其余进 needsAttention——**不在第 8 步才靠校验回路发现**（参数超预算不是确定性可修项，会卡死在修复循环或掉进 unknownPatterns）
8. **校验回路**：`run_vrchat_validation` ≤3 轮，确定性可修项（blendshape 缺 v_sil、shader 不兼容）自动修
9. **出口**：校验通过 → `{prefabPath, scenePath, validationReport, autoFixed[], needsAttention[]}` + `open_vrc_control_panel` 打开 VRC SDK 控制面板（终点=手动上传前一步）
10. **失败事务**：任一步失败 → 删资产回滚（§6.3 双层）+ 返回错误与上下文
11. **进度语义（补丁 6）**：`AssetDatabase.ImportAsset` 是同步主线程阻塞，只能做**阶段边界间**的进度通知（导入前一条/完成后一条），文档与工具描述明确此预期，避免被误判为卡死

**前置检测**：VRCSDK（`com.vrchat.avatars`）未安装 → `get_project_info.avatarImportAvailable:false` + 安装指引，工具不盲跑。

## 10. 阶段计划

1. **骨架**（本次交付）：双 transport + envelope + dispatcher + compat 自检 + channel 握手 + batch 状态机 + 读工具集 + Python server + opencode 接入
2. **核心编辑**：对象/组件/Transform/资产 + 写入校验层 + prefab 工具
3. **模型链路**：ModelImporter + BlendShape + 材质 + 完整 Console
4. **VRChat 专项**：AvatarDescriptor/Expressions/PhysBone/Animator + validation_catalog（种子+golden）+ `import_avatar_from_zip` 端到端
5. **收尾**：审批流完善、多实例策略验证、compat-matrix.md 全量、文档

## 11. 风险台账

| 级别 | 风险 | 处置 |
|---|---|---|
| **高** | Editor internal API 兼容面（MPE/LogEntries/VRC 校验） | Compat 模块 + 启动自检 + 矩阵文档 + 公共 API 优先 |
| 中 | 通用写入误操作半径 | 校验层 + 约束注入 + 权限前缀 |
| 中 | Undo 语义漂移 | 显式事务 + 每调用一组 + 三维状态机 |
| 中 | 校验输出结构化脆弱 | 数据驱动 catalog + unknown 透传 + 快照自愈 |
| 低 | 多实例/端口 | 临时端口 + channel 文件握手 + pid 存活校验 |

## 12. 安全

- 仅监听 loopback；channel 文件含 pid/port 属本机信息
- zip 解压：zip-slip 防护、字节预算、条目数上限、不执行 zip 内任何代码
- 工具描述/注解视为不可信输入（MCP 规范），写工具按前缀走 agent CLI 审批
- 所有写操作带 Undo + 双层回滚，可审计

**阶段 1 验证结果**（6000.4.0a2，verify/api-dump2.txt 实测）：
- `UnityEditor.MPE.ChannelService`：**全部为静态方法**。`Start()/Stop()/IsRunning()/GetPort()/GetAddress()`；`GetOrCreateChannel(string, Action<int,byte[]>)` 返回 `Action`（退订委托）；handler 第一参是 **connectionId**（非 channelId），回包用 `Send(int connectionId, string)`，无连接记录时兜底 `Broadcast(int channelId, string)`（channelId 由 `ChannelNameToId(name)` 取）。客户端 URL 格式官方文档确认：**channel 名 = URL 最后一段**（`ws://127.0.0.1:<port>/<channelName>`）。已在 MpeTransport.cs 实现。
- `UnityEditor.LogEntries`：`StartGettingEntries()→int`、`GetCount()→int`、`GetEntryInternal(int row, LogEntry)`（LogEntry 无 `stackTrace` 字段，全文本在 `message`，`callstackTextStartUTF16` 为栈起始偏移）、`EndGettingEntries()`、`Clear()`。`LogEntry.mode` 为位掩码 `1 << (int)LogType`（0=Error 1=Assert 2=Warning 3=Log 4=Exception）。已在 InternalApiRegistry.cs 实现（Invoke 数组槽位回写兼容 ref/值传两种形态）。
- TcpWsTransport 握手/帧解析在目标 Mono 上的行为：**未实测**（无 license 无法长时间跑 batch，见下）。

**阻塞项**：本机 Unity 无激活 license（batch 长跑报 "No valid Unity Editor license found"；`-quit` 短任务可跑）。MPE/TcpWs 端到端、TcpWs 帧解析实测需在用户已激活的编辑器中进行（Tools > VRChat MCP > Start Bridge + `server/test_bridge_mock.py` 同款 Python 客户端）。

## 13. 阶段 1 e2e 实测记录（2026-08-13，Unity 6000.4.0a2 + MPE 传输）

全部通过（server/test_e2e.py + probe2.py 探针，连 `ws://127.0.0.1:<port>/<channelName>`）：

1. **协议帧**：连接后 MPE 服务器先发**十进制 connectionId 文本帧**（如 `'5'`），随后即我们的 JSON-RPC envelope 明文。Python 接收循环需跳过纯数字帧。
2. **JsonUtility 怪癖**：`JsonUtility.ToJson` 把 null 的 `error` 字段序列化为 `"error":{}`（→ 反序列化侧看到 `{"code":0,...}` 空错误对象），且 `result` 字符串字段内容被**二次转义**。Python 侧容错：`error.code != 0` 才算错；`result` 先 `json.loads` 再输出。
3. **双重订阅 bug**：BridgeBootstrap 手动订阅 `MessageReceived` 的同时 `BindTransport` 也订阅 → 每条消息执行两次、回包×2。修复：删除手动订阅。回包重复会连带 batch 状态被二次 Begin 覆盖（groupIndex 漂移、closeReason 泄漏）。
4. **MPE handler 非幂等**：`GetOrCreateChannel` 每次调用追加 handler；`Start()` 前先 `CloseChannel(name)` 幂等化。
5. **.NET UTF8 BOM**：`Encoding.UTF8` 写文件带 BOM，Python `json.loads` 拒绝 → C# 改 `new UTF8Encoding(false)`，Python 用 `utf-8-sig` 双保险。
6. **断连语义**：断开 websocket → `ClientDisconnected` → batch 自动 `Active → Closed (closeReason=Disconnect)` ✓。
7. **已修复的输出 bug**：`get_scene_hierarchy` tag 前缺逗号；`get_batch_state` Closed 时 idleSeconds 天文数字（DateTimeOffset 默认值）→ 显示 0。

**未验证**：TcpWs 传输帧解析（兜底传输，主路径 MPE 已全通）；opencode MCP 全链路接入（配置就绪，待跑）。

## 14. opencode stdio 接入实测（2026-08-13，FastMCP 3.4.7）

- 拓扑确认：opencode 运行于 **Windows 原生**（`WSL_DISTRO_NAME` 为空、`wsl --list` 仅 docker-desktop/Stopped、进程 `AppData\Roaming\npm\node_modules\opencode-ai\bin\opencode.exe`）→ 无跨边界，全链路 Windows 内部。
- MCP 工具权限 key 格式：`<mcp-server-name>_<tool>`（如 `unity-vrc_begin_batch`），支持通配符 `unity-vrc_edit_*`（docs/opencode permissions + mcp-servers）。
- 冒烟（server/test_mcp_stdio_smoke.py，fastmcp `Client`+`StdioTransport` 与 opencode 同款接入）：10 工具发现、ping/get_scene_hierarchy/begin_batch/get_batch_state/end_batch 真实往返全过。
- **发现并修复的 bug**：`_call(name, **params)` 形参名 `name` 与 `begin_batch` 的 MCP 参数 `name` 冲突 → `TypeError: _call() got multiple values for argument 'name'`（直连 e2e 绕过 FastMCP 无法暴露，必须 stdio 层测试）。修复：形参改名 `method`。**教训：Python helper 形参名不得与任何工具 MCP 参数名冲突。**
- opencode 配置落位 `C:\Users\liuzijian\opencode.json`（用户主目录=cwd 项目级）：mcp.unity-vrc（local，`.venv` python + cwd=server + `UNITY_MCP_PROJECT_DIR`）+ permission 预写 `unity-vrc_{edit,apply,save,import}_* → ask`（阶段 2 写工具落地时验证命中）。
- 待用户验证：重启 opencode 后工具列表、真实调用、无前缀工具（begin_batch/undo/redo）默认审批行为（默认应为 allow，确认后决定是否显式 allow）。

## 15. opencode 内真实调用验证（2026-08-13）

在 opencode 内通过 `unity-vrc_*` MCP 工具直接完成：ping、get_scene_hierarchy、begin_batch/get_batch_state/end_batch（groupIndex 一致）、undo/redo 全过。
**无前缀工具默认审批行为 = allow**（begin_batch/end_batch/undo/redo 直接执行，无 ask 拦截）。结论：高频无副作用工具无需显式 allow 规则；阶段 2 起 `edit_*` 等前缀工具按预写规则 ask，届时验证命中。

## 16. 阶段 2 编辑工具 e2e 实测（2026-08-13）

新增 8 工具（共 18）：`get_object_details`（读）+ `edit_transform` / `edit_set_active` / `edit_set_name` / `edit_add_component` / `edit_remove_component` / `edit_set_component_property` / `save_scene`。C# 侧 `EditTools.cs` + 写校验层 `EditValidation`（instanceId 解析用 `EditorUtility.InstanceIDToObject`、类型解析带缓存、值解析按 `SerializedPropertyType` 分支）；每调用一 Undo group + `Undo.SetCurrentGroupName`。

实测通过：
- stdio 冒烟（test_mcp_stdio_smoke.py）：18 工具发现；begin_batch 包裹的编辑往返（transform→active→name→add BoxCollider→property m_Size.x=2.5→remove）→ **单次 undo 全量回滚**（name/active/position/collider 全部恢复）→ end_batch。
- run_regression.py 新增第 4 组 `edit cycle + undo restore`，与单回复/断连断言一起全绿。
- **MCP 层 bug（_call 冲突）已修**（见 §14）；`_call` 现做 snake_case→camelCase 参数转换，顺带修复了 `get_scene_hierarchy` 的 `maxDepth/maxNodes` 参数从未命中 C# 的问题（Python 传 max_depth，C# 找 maxDepth）。

**Unity 6000.4 序列化坑（实测确认）**：`SerializedProperty.NextVisible(true)` 对部分内置组件（AudioListener 实测）**返回空**，但 `Next(true)` raw 遍历正常。原因未深究（batch 实验 PropDump.cs 证实 visible=空、raw=13 个属性含 m_Enabled）。**修复：WriteSerializedProperties 改用 `Next(true)` + 过滤内部字段**（m_ObjectHideFlags/m_GameObject/m_CorrespondingSourceObject/m_PrefabInstance/m_PrefabAsset/m_Script/m_EditorClassIdentifier 及其子路径）。

待验证：opencode 内 `edit_*` 审批命中（重启 opencode 拉取新工具清单）。

## 17. opencode 内 edit_* 审批验证（2026-08-13）

重启 opencode 后 18 工具全部拉取；opencode 内真实调用 `get_object_details`（AudioListener `m_Enabled` 正确输出，NextVisible 修复生效）与 `edit_transform`——**权限规则 `unity-vrc_edit_* → ask` 命中，UI 弹出审批框**（once/always/reject），用户批准后执行。权限前缀约定与 opencode 审批规则在真实环境验证闭合。阶段 2 第一批（读侧 + 6 编辑工具 + save_scene + 写校验层）完成。

## 18. 生命周期/参数解析 4 连修（2026-08-13，回归 5/5 绿）

回归脚本扩到 5 组断言后立即抓到 4 个真实问题（证明"守护测试"价值）：

1. **AssetImportWorker 污染通道文件**：`-batchMode -noUpm` 的导入 worker 也会初始化插件并写 channel 文件 + 开 MPE 通道，但其消息调度不工作。discover 选最新 mtime → 连到 worker 端口 → 全部超时（曾出现 3 个 Unity 进程、3 个通道文件）。修复：C# 侧 `Environment.CommandLine` 含 `-batchMode` 时静态构造跳过启动（根治，下次导入不再写）；Python 侧 discover 过滤 `cmdline` 含 `batchMode/AssetImportWorker` 的 pid（防御存量文件）。之后通道目录只有主编辑器一个文件。
2. **MPE 断开检测竞态（GetChannelClientList 残留）**：原实现按 `GetChannelClientList` 计数检测"0 客户端"，但多次连接/断开后 Unity 侧残留死连接条目（且客户端禁 ping），poll 永远看不到 0 → 断开永不触发（表现为 fast-reconnect 抢窗口）。修复：改为**数据活动超时**——Python 客户端每 10s 发 `unity_mcp_heartbeat` notification（未知方法，Unity 静默忽略、不回包）；Unity 侧 `OnChannelData` 刷新活动时间戳，`PollClients` 30s 无活动判定断开。双语义回归断言：断开不重连 → Closed/Disconnect；快重连 → batch 保活（agent/MCP 重启不丢 undo 组）。
3. **JsonArg 空白解析 bug**：Python `json.dumps` 输出 `"maxNodes": 1`（冒号后空格），C# 从冒号后直接取数字不跳空白 → `int.Parse(" ")` 抛异常 → 回退默认值。**`maxDepth/maxNodes` 自始未生效**（返回完整树一直未被注意）。修复：跳空格后再解析。ConsoleTools 的 `max` 解析同 bug 同修。
4. **场景保存后实例 ID 失效**：测试硬编码 `instanceId=-1302`（内存场景 ID），保存场景为 `Assets/E2E.unity` 后 ID 重新分配（camera 变正数 20838）。修复：回归内动态查找 Main Camera。

另外：`EditorApplication.isBatchMode` 在 6000.4 不存在，用命令行检测。验证载体：`run_regression.py`（5 组）+ `test_mcp_stdio_smoke.py`（18 工具 stdio 冒烟）均全绿。

## 19. 阶段 2 第二批：层级 + Prefab（2026-08-13，25 工具，回归 6/6 + 冒烟全绿）

新增 7 工具：`edit_set_parent`（环检测：newParent.IsChildOf(go) 即自身/后代才算环）、`edit_set_sibling_index`（-1=末尾）、`edit_create_object`、`edit_destroy_object`、`edit_duplicate_object`（prefab 实例走 InstantiatePrefab，否则 Object.Instantiate 回退）、`prefab_create`（SaveAsPrefabAsset/AndConnect）、`prefab_instantiate`（RegisterCreatedObjectUndo）。

验证抓到 3 个真实问题：
1. **循环检测写反**：`go.IsChildOf(newParent)` 在 newParent 为祖先时也 true（合法上移），真环是 `newParent.IsChildOf(go)`。回归的层级批次直接命中。
2. **PrefabUtility 内部开独立 undo 组**：`SaveAsPrefabAsset`/`InstantiatePrefab` 内部记录 undo，`PerformUndo` 只弹最新组 → 批次内 mix prefab 操作后单次 undo 部分失效（对象残留且父级被重置）。修复：prefab 工具内 `Undo.GetCurrentGroup()` 捕获 + 操作后 `Undo.CollapseUndoOperations(group)` 折叠回当前组链，批次单 undo 完整覆盖。
3. **测试断言失准**：场景天然 2 根（Camera + Directional Light）；批次 undo 后断言"仅 1 根"错误。改为断言无 `Reg/Probe/Pfx/Smoke` 残留 + 原始根仍在。

回归新增第 6 组 `hierarchy + prefab cycle`：create→reparent→sibling→duplicate→destroy→prefab 往返→单 undo 恢复；预清理上次失败残骸。冒烟同步扩展（EXPECTED_TOOLS=25）。covered_params 补齐 7 新工具。

## 20. 通用 batch 完整性断言 + 资产清理（2026-08-13，26 工具，回归 7/7 + 冒烟全绿）

用户建议：不针对特定 API，做**通用 batch 完整性断言**——任意写工具组合在一个 batch 内，单 undo 后场景快照必须逐字段回到 batch 前。

1. **通用断言（check_batch_integrity）**：batch 前 `get_scene_hierarchy(maxDepth=8)` 取完整树 → `_norm_snapshot` 规范化（丢弃 instanceId——undo 会重分配、脏标记；保留 name/active/transform/组件类型+enabled/子级递归）；batch 内覆盖全部写工具类别（transform/active/name/component add+property+remove/hierarchy create+reparent+sibling+duplicate+destroy/prefab 往返），单 undo → 快照必须字节级一致。数据驱动：阶段 3 新工具（import/animator）只需往 mix 里加，无需逐 API 设计测试。
2. **测试清理 bug（SourceAssetDB 失同步风暴）**：回归/冒烟直接 `unlink()` 临时 prefab 文件 → SourceAssetDB 记录与磁盘不符 → Unity 无限重导入报 "Build asset version error"（Worker0 风暴刷屏）→ **主线程被占，MPE 活动超时轮询饿死，disconnect 检查 35s 窗口内未触发 30s 超时**（表现为回归偶发红）。修复：新增 C# `asset_delete` 工具（AssetDatabase.DeleteAsset，路径须在 Assets/ 下），测试清理一律走它；手工清掉残留 .prefab.meta。Unity 重启时 AssetDatabase 重验，风暴消失。此 bug 同时解释了上次 disconnect 失败的直接原因。
3. 顺手清理：opencode 会话中发现场景残骸（RegChild/RegParent/SmokeChild×2 挂在 Main Camera 下——回归预清理只扫 root 层，子级残骸漏网），通过 edit_destroy_object 清掉；回归预清理是另一个覆盖缺口（递归清理留给后续）。

## 21. 阶段 3 第一批：导入管线（2026-08-13，28 工具，回归 8/8 + 冒烟全绿）

新增 2 工具（`ImportTools.cs`，属性自动发现）：

1. **asset_import_fbx**：拷贝本地模型文件（.fbx/.obj/.glb/.gltf/.dae/.blend）进 Assets → `AssetDatabase.ImportAsset(ForceUpdate)` → 返回 assetPath/hasAvatar/rootName。destDir 须在 Assets/ 下（自动建目录）；同名冲突自动后缀 `_1`。**项目资产导入不可 Undo**，工具文档明确要求 batch 外使用。
2. **import_avatar_from_zip**：一键管线——安全解压（zip-slip 防护 + 2GiB 字节预算 + 8192 条目上限 + 嵌套目录创建）→ 找第一个模型文件 → 导入 → `PrefabUtility.InstantiatePrefab` 实例化进当前场景（含 `Undo.GetCurrentGroup/CollapseUndoOperations` 折叠，实例化部分可 Undo）。`import=false` 时只解压不导入（用于预览/检查）。

验证抓到 3 个真实问题（回归第 7 组 `import pipeline`，含 zip-slip 负测试）：

1. **JSON 手拼缺引号**：`"rootName":test_cube_1`（值没包引号）→ Python json 解析失败。修复：`JsonUtil.WriteString`。
2. **zip 嵌套目录崩溃**：zip 内 `Models/test_cube.fbx` 条目，`ExtractToFile` 前未建父目录 → DirectoryNotFoundException。修复：逐条目 `Directory.CreateDirectory(parent)`。
3. **路径双 Assets 前缀**：`"Assets/" + Path.GetRelativePath(projectDir, f)` 在 rel 已含 `Assets/` 时拼出 `Assets/Assets/...`。修复：rel 已含前缀则直接用。

清理纪律（本次新增）：

- 回归导入组：幂等预清理 4 个已知残留路径 + finally 清理；`_delete_asset_quiet` 静默吞异常（失败不阻塞主流程，靠预清理兜底）。
- 冒烟清理修复：原 `finally` 里直调 `mcp_mod.asset_delete()` 在 server 会话退出后必然失败被吞 → prefab 残留。改为 main 内 `call_tool("asset_delete")`（会话存活）+ 异常分支用直连 bridge 兜底。
- 手工清理过：Assets/Imports 与 AvatarImports 的历史残留 fbx/meta、Assets/ 下历史 smoke prefab。

## 22. 资产侧 Undo 语义 spike（2026-08-13，Animator 批前哨）

问题（用户预判命中）：AnimatorController 是资产而非场景对象，通用 batch 完整性断言（场景树快照）覆盖不到资产侧；AnimatorController/AnimatorStateMachine 历史对 Undo 服从度不稳定。决定在建任何 Animator 工具前先做一次性 spike（成本最小）。

Spike 载体：verify 项目 `Assets/Editor/SpikeAnimatorUndo.cs`（-executeMethod，独立于主编辑器）。3 个 probe：

| probe | 操作 | PerformUndo 后 |
|---|---|---|
| 1 | CreateAnimatorControllerAtPath，无 undo 注册 | 文件存活 + 对象存活（干净） |
| 2 | CreateAnimatorControllerAtPath + RegisterCreatedObjectUndo | **文件存活 + 对象被毁（内存/磁盘双重态，最危险）** |
| 3 | RegisterCompleteObjectUndo 注册后 DeleteAsset | 文件未恢复 + 对象被毁（删除不可撤销） |

结论（写进 Animator 批设计约束）：

1. **Undo 不控制资产文件**——PerformUndo 只回滚内存对象，文件创建/删除永不因 Undo 发生。probe2 双重态（对象已毁文件还在）后续访问即 MissingReferenceException（首次 spike 的 probe3 即撞此坑）。
2. **Animator/资产工具不进 batch**（与 import 工具同规则）：通用 batch 断言只对场景工具生效；资产工具的"回滚"是**工具级事务**——创建即记录文件路径，任何后续失败在工具内清理自己刚建的资产（同 zip 解压失败清理模式）。
3. **禁止 RegisterCreatedObjectUndo 于资产创建**；创建类工具要么不注册 undo（probe1 安全），要么在返回结果中提供 assetPath 供调用方（agent）在失败时显式 asset_delete（已存在该工具）。
4. 资产修改类工具（改 controller 内容）可用 RegisterCompleteObjectUndo 做内容回滚——但只回滚内容不回滚文件创建/删除，工具描述需明确。

## 23. 接口稳定性策略（2026-08-13）

`import_avatar_from_zip` 采用**演进同一工具名**策略，登记为已知 planned breaking change：

- 理由：语义上只有一个"一键导入"入口，另开完整版名字会产生两个语义重叠工具，agent 无法判断用哪个。
- 对策：返回结构顶部加 `"pipelineVersion": N`；步骤④⑥⑦⑧接入时 N+1（当前第一批=1，结构含 assetPath/hasAvatar/rootName/extracted/modelPath/instanceId/instanceName）。后续新增 validationReport/autoFixed[]/needsAttention[]/open_vrc_control_panel 时递增。
- 测试纪律：回归断言改为**严格校验当前版本必含字段集**——结构变化（版本递增或字段增删）会使断言显式失败，杜绝静默错位。测试更新与版本递增同步提交。
## 24. 阶段 3 第二批：Animator 组回归修复（2026-08-14，9/9 全绿）

animator 组回归（create/state/transition/readback + rollback）此前持续失败，排查中发现并修复 **2 个真实 bug + 1 个测试 bug**，并清理了调试代码：

1. **测试 bug（"物化"幻觉的根源）**：`run_regression.py` 的 duplicate 断言误用 `r["error"]`（期望错误响应返回 error 字典），但 `UnityBridge.request()` 对错误响应**抛 BridgeError 异常**（unity_bridge.py:170），断言代码永不执行，异常直接传播 → 整个组 FAIL。此前观察到的"create 报 already exists + Start importing 物化日志"实为**正常现象**：第一个 create 成功创建文件，duplicate create 正确拒绝（文件真实存在）；`Start importing` 是 CreateAnimatorControllerAtPath 创建后的正常 import。此前加的"Unity 6 VFS 物化"诊断循环（Sleep 750×3 + Refresh）基于错误理论，已移除。修复：duplicate 断言改为 `try/except BridgeError` 校验 `already exists` 消息。

2. **产品 bug（add_animator_transition 序列化）**：AnimatorTools.cs 用 `JsonUtil.Escape(from.name)`（仅转义不包引号）拼接 result → `{"from":Idle,...}` 非法 JSON，Python 端 `json.loads` 失败（line 1 column 9）。修复：改用 `JsonUtil.WriteString`（包引号+转义）。这是第二个 JsonUtil 系列 bug（§21 曾修复 rootName 未包引号）。

3. **心跳 vs 断开检测的权衡（Semantics A 环境性 skip）**：`unity_mcp_server.py` 的常驻 bridge 每 10s 发 `unity_mcp_heartbeat`，使 MpeTransport 的 30s 数据活动超时**永不触发**（活跃客户端永不判定断开）——这是**设计意图**（用户停顿不丢 undo 组，见 §18）。但 opencode 的 MCP bridge 常驻意味着回归的 Semantics A（"无客户端 → batch 关闭"）在 opencode 环境下**不可测**（此前 9/9 通过只是因域重载恰好断开 bridge）。修复：disconnect 组检测到仍 Active（外部常驻客户端存在）时 end_batch 清理 + 打印 `[skip]`，Semantics B（快速重连保活）无条件测试。

> **验证盲区（长期）**：Semantics A（30s 断连检测 → batch 关闭）在常驻 MCP 客户端存在时**不可自动化验证**——心跳机制（§18）是设计意图，opencode 的 MCP bridge 常态在线意味着该路径在回归里只会走 `[skip]` 分支。**这不等同于该路径已验证**。如需人工验证：1) 杀掉 opencode/MCP server 进程（或断开 ws）；2) 等 >35s；3) 从**另一个**新进程（新 channel 连接）调 `get_batch_state`，应得 `phase=Closed / closeReason=Disconnect`。排查断连问题时须知道：回归绿 ≠ 这条路径有保障。

4. **顺带清理**：移除 create 的 ghost 诊断（`[VrcMcp] create-AC exists` 日志 + 物化复制到 TEMP）；修复 `asset_fs_probe` files 数组 JSON bug（WriteString 产生 `"name":` 属性而非数组元素 → 改 Escape 包引号）；全部 AnimTestAC 残留用 asset_delete 清理。

验证：`run_regression.py` 9/9 连续 2 次 EXIT 0（disconnect 组 skip 提示 + 其余全 ok）；`test_mcp_stdio_smoke.py`（EXPECTED_TOOLS=32）通过。


## 25. JSON 构建迁移到 Newtonsoft + 负测试（2026-08-14）

背景：`JsonUtil` 是纯手拼 JSON（`Escape` 只转义不包引号；`WriteString` 包引号）。全仓
调用点扫描结论：所有 `Escape` 均在引号内（正确模式），唯二 bug 为 §21 rootName、§24
from/to —— 手拼漏引号已复发 2 次，结构性风险在，决定迁移到 Newtonsoft
（`com.unity.nuget.newtonsoft-json` 3.2.1，随 UPM 包，`JObject`/`JArray`/`JToken`）。

迁移分批（批间回归验证）：

- 批 1（读侧，嵌套结构）：`get_scene_hierarchy` + `WriteNode`（CoreTools）、
  `get_object_details` + `WriteSerializedProperties`/`WritePropertyValue`（EditTools，
  返回值改为 `JToken`）、`get_animator_controller`（AnimatorTools）、`get_console_logs`
  + `WriteEntry`（ConsoleTools）。数字全部走 `JValue`（Newtonsoft 最短往返），
  行为与 `ToString("R")` 兼容；枚举输出 `{name,value}`、ObjectReference 输出
  `{name,instanceId}` 或 null、数组/struct 输出 `{__type,size?,children?}`、
  截断标记 `"...truncated":true` 均保持。
- 批 2（写侧，简单确认体）：全部 `edit_*`、`save_scene`、hierarchy 5 件、
  batch 状态（`BatchStateJson` 内部改 JObject，返回 string 不变，get_project_info
  用 `JObject.Parse` 内嵌）、prefab_create/instantiate、asset_delete、
  animator create/state/transition、asset_fs_probe（Snapshot 改返回 JObject）。
- 完成后全仓 `JsonUtil.` 残留 0；`JsonUtil.cs` 保留（`JsonUtility` 换不掉，通道文件
  解析仍用它）。

负测试（防手拼 bug 复发）：新增诊断工具 `diagnostic_bad_json`，故意返回漏引号 JSON
（`{"from":Idle,"to":Run}`，§24 bug 类）。回归新增组 `check_bad_json_safety_net`：
调用该工具，断言 `json.loads(result)` 抛 `JSONDecodeError` —— 若安全网回归（非法
JSON 被静默接受），本组立即 FAIL。Unity 层 [McpTool] 方法数 33 -> 34（discovered 日志）；MCP server 层（FastMCP proxy）工具数不变 32，smoke `EXPECTED_TOOLS=32` 保持（负测试直连 ws，不经 MCP server 层）。

验证：verify 项目（`D:\Unity\Unity 6000.4.0a2\Editor\Unity.exe -batchmode`）编译
0 错误；运行中编辑器重启后 resolve newtonsoft 并重编译，回归 10/10 EXIT 0 +
smoke（EXPECTED_TOOLS=33）通过。

教训：向 manifest.json 新增 UPM 依赖不会让运行中编辑器自动 re-resolve（PackageCache
一直为空，编辑器带着编译错误继续跑**旧 DLL**，回归全绿是假象）。manifest 变更必须
重启编辑器验证；仅包内 .cs 源码变更才自动重编译。

## 26. VRCSDK 编译阻塞根因定论 + import v2 / open_vrc_control_panel（2026-08-14）

### VRCSDK 编译阻塞（跨两 session，最终定论）

症状：`import_avatar_from_zip`/SDK 工具不可用，`get_project_info.avatarImportAvailable:false`；
Editor.log 16×CS0246（NUnit 不可见），SDK 控制面板菜单消失。

根因（证据链闭合）：**官方 SDK 3.10.3/3.10.4 包 zip 内包含两个无 `#if` 保护的测试文件**
`Editor/VRCSDK/VTP/VTPTests.cs` 与 `Editor/VRCSDK/Dependencies/VRChat/Tests/AssetBundleFooterTest.cs`
（归属 `VRC.SDKBase.Editor` 程序集，所在目录无独立 asmdef）。而 `nunit.framework.dll`
（com.unity.ext.nunit@1.0.6）meta `isExplicitlyReferenced: 1` → 仅 TestAssemblies 程序集
或 precompiledReferences 显式列出可见 → 普通 asmdef 必然编译失败。**不是本项目配置错误**：
3.10.3 zip 同样含测试文件；VCC 缓存 repo（url=https://vrchat.github.io/packages/index.json）
中 base 3.10.4 即 GitHub release zip 且 SHA 一致；VCC exe/前端无任何测试文件剥离逻辑；
Reddit 真实用户（r/VRchat 1nrfo3d）通过 VCC 安装后报同一错误 + 控制面板不出现；
官方 Help 文章 360062658873 有对应排障页。官方无更新的修复版本（3.10.4 为最新 stable）。

修复：删除两个测试文件（+meta）于 `D:\VCC\local-packages\com.vrchat.base-3.10.4\`（file: 引用源，
不受 VCC 更新影响）。重启后编译 0 错误，`avatarImportAvailable:true`。

其他已排除项：test-framework 1.1.29 vs 1.1.33 依赖相同（均 ext.nunit 1.0.6）→ 换版本无效；
给 asmdef 加 `optionalUnityReferences:["TestAssemblies"]` 会毁掉整个程序集（否决）；
overrideReferences:true 会丢全部未列出 precompiled 引用（否决）。

### import_avatar_from_zip pipelineVersion 1 → 2（§23 演进策略执行）

- `pipelineVersion: 2`；出口新增 `validationReport {passed, errors[]}`、`autoFixed[]`、
  `needsAttention[]`、`controlPanelOpened`。
- v2 校验范围（诚实最小集）：SDK 未装 → needsAttention 提示；已装但导入根无
  `VRCAvatarDescriptor`（反射 `VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRCSDK3A`，
  组件在预编译 `VRCSDK3A.dll` 中）→ needsAttention 提示手动装配。
- import=true 成功后自动打开控制面板（§9 出口语义）→ `controlPanelOpened`。
- 回归断言同步：`_require_fields` 必含 v2 字段 + `pipelineVersion == 2`（§23 纪律）。

### 新工具 open_vrc_control_panel（SdkTools.cs，第 35 个 [McpTool]）

`EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel")`（base 包
`VRCSdkControlPanel.cs:23` 菜单）。SDK 未装（manifest 无 com.vrchat.avatars）→ McpToolException；
菜单不存在 → `{opened:false, hint}`。SDK 访问全部反射化（SdkTools.AvatarDescriptorType 缓存
Type.GetType，无硬引用 VRCSDK3A/VRCSDKBase）。

### 本批踩坑

1. **VrcSdkDetector 是顶层类不是 CoreTools 嵌套类**（CoreTools 在 176 行已闭合）：
   `CoreTools.VrcSdkDetector` → CS0117。首次编译 7 错误，改 `VrcSdkDetector` 直访后 0 错误。
2. **file: 引用包源码变更在本机 2022.3.22f1 不自动重编译**（§25 在 6000.4 实测可自动编译，
   2022.3 未触发）：DLL 时间戳停留旧值 → 按 §0 重启编辑器强制重编译（新增 .cs 文件后
   必须重启，含 .meta 生成）。

验证：重启后 `discovered 35 tools`、error CS 0；`open_vrc_control_panel` 直连实测
`{"opened":true}`（控制面板真实弹出）；回归 10/10 EXIT 0（.venv python 跑，系统 python
缺 fastmcp 属环境问题）；smoke EXPECTED_TOOLS=33 全绿。

## 27. sdkHealth 自检 + sdk_repair_test_files（对 §26 修复方式的设防）

§26 的修复是删除官方包内文件——`D:\VCC\local-packages` 是 file: 引用源，**VCC 重新拉包
（手动更新 / 换机重装 / 官方新版本跟进）会原样把坏文件覆盖回来**，症状完全复现但排查人
可能已忘根因。§26 的"勿删 zip 与目录"依赖人记，本条把该风险转为代码自检
（§0 纪律 5 + InternalApiRegistry 同款"self-check 而不是靠记忆"哲学）。

### VrcSdkDetector 扩展（CoreTools.cs）

- 新增解析 `com.vrchat.base` 的 manifest 值 → `BasePackageDir`：
  - `file:` 前缀 → 直接解析路径（含 `file:///` 形式，Windows 盘符路径）
  - 版本号（VCC 标准安装）→ `Packages/com.vrchat.base`（embedded）或
    `Library/PackageCache/com.vrchat.base-*`（UPM cache，取首个命中）
- `GetHealthIssues()`：**每次调用实时 File.Exists 检测**（不缓存——包目录在控制之外，
  随时可能被 VCC 重置），对 `KnownBrokenTestFiles` 两个已知坏文件逐路径检查。
- `get_project_info` 新增出口：
  - `sdkHealth {status: ok|broken|absent, baseDir, issues[]}`（issues 含修复指引文案）
  - `avatarImportAvailable` 语义收紧：`IsInstalled && issues==0`（坏文件存在 = 编译必败
    = 实际不可用，不再是 manifest 里有 avatars 就算可用）

### sdk_repair_test_files（SdkTools.cs，第 36 个 [McpTool]，审批前缀 sdk_* → ask）

删除存在的坏文件（.cs + .cs.meta），幂等，返回 `{baseDir, repaired[], alreadyClean[]}`。
不做自动修复：删官方包内容属破坏性动作，走审批（§5.3 权限哲学）。BasePackageDir 无法
定位（非 file:/embedded 安装）→ McpToolException。

### 回归固化（check_sdk_health，第 11 组）

完整闭环：干净态 sdkHealth=ok → **植入假坏文件**（包内 VTPTests.cs，仅几秒窗口，
2022.3 不自动编译无编译风险）→ sdkHealth=broken 且 issues 含诊断 → sdk_repair_test_files
删除（断言文件+meta 不存在）→ sdkHealth 恢复 ok → 幂等断言（alreadyClean==2）。finally
兜底清理，绝不残留。VCC 真重新同步后该组会响亮失败（检测到，不是人发现）。

验证：重启后 `discovered 36 tools`、error CS 0；直连闭环全过（ok → broken →
repair → ok → idempotent）；回归 11/11 EXIT 0；smoke EXPECTED_TOOLS=34 全绿。

## 28. TcpWs 兜底传输实测 + 多实例策略验证（阶段 5 收尾）

### 28.1 TcpWs 实测（test_tcpws_transport.py，13 项全过）

方法：EditorPref `VrcMcp.Transport=tcpws`（经 `-executeMethod` 临时脚本写入，注册表
键带 `_h<hash>` 后缀不可手工构造）→ 重启编辑器以 TcpWs 启动 → 实测 → 恢复 auto。
验证内容（不止"能连上"）：
- 掩码文本帧往返、**64KB+ 127 扩展长度 + 5 段 TCP 分片**（RawWsClient 手工帧绕过
  websockets 库）、WS 控制帧 ping→pong / close 握手、fin=0 帧丢弃不崩
- 优雅 close + 粗暴 FIN 断开 + 重连（连接生命周期，MPE 教训对应）
- **batch 边界跨全断连**：EndOnDisconnect 仅在 remaining==0 触发；单连接断开 batch 存活
- 双客户端 24 交错请求广播隔离（Send 广播 + Python id 过滤，无交叉干扰）
- 36s 长连接 + 10s 心跳 notification 稳定

**未发现服务端 bug**（websockets 库对照大消息实验确认 127 分支完好）。
**服务端已知行为**（记录不修）：
(1) ReadExactly 对不完整帧永久阻塞无超时（客户端负责完整帧，TCP 保证完整交付，
实际网络无碍）。**I/O 模型**：每连接独立线程（ReadLoop 阻塞在各自 socket），单个
连接的不完整帧只阻塞自身线程，不影响其他连接的读；但 `Send` 广播是单线程遍历，
某连接写缓冲满（对端不读）会卡住整个广播。**触发条件**：本结论的可接受性绑定在
"TcpWs 只是备胎（MPE 主路径）"这一前提——**若 TcpWs 从备用转正为主路径，本条
必须重新评估**（转正后客户端面从单一可信 Python 客户端扩展为任意/恶意客户端，
不完整帧与不读对端的行为都可能变成可用性问题）。
(2) fin=0 帧被消费丢弃不重组（websockets 客户端不分片，生产无碍）。
**测试构造教训**（3 个假失败）：切片写错 `body[i*step:]`（到末尾）→ 流错位乱码
Parse error；`//5` 截断丢尾 4 字节 → ReadExactly 死锁超时；fin=0 断言预期错误。
前两者依赖"发送字节数与服务端消费精确对齐"，是 raw 客户端测试特有的坑。

### 28.2 多实例策略验证（test_discovery_strategies.py，7 项全过）

**边界明确**（修订 6 的机制层 vs 语义层）：本批只验证**机制层**——PID 存活检测
（死 pid 跳过）、最新 mtime 胜出、mtime 平局 ctime 打破、**mtime+ctime 双平
拒绝**（BridgeError + 提示钉选）、batch worker 排除、UNITY_MCP_CHANNEL_FILE 钉选、
channel 目录缺失报错。**语义层**（"默认连最新实例"对用户是否是心智负担、工具返回
是否应明示当前实例）**刻意推迟到试用期**真实反馈，不在收尾判定，避免"机制对了但
不知道好不好用"的无验收标准地带。
**假绿教训**：测试通道文件曾用 pid 命名（双通道同名互相覆盖 → 3 项假绿单文件
通过），改 name 命名后全部为真实双文件验证。

### 28.3 验证状态更新

回归 11/11 EXIT 0、smoke 34 工具全绿、TcpWs 13/13、多实例机制层 7/7；
`discovered 36 tools`、error CS 0；编辑器恢复 MPE 主路径（transport=mpe）。

## 29. 审批流完整验证 + 阶段 5 封顶

opencode.json 权限规则 `unity-vrc_{edit,apply,save,import,sdk}_* → ask` 的完整验证
（4 路径全过，真实审批交互）：
1. **allow 默认**：无规则工具（get_scene_hierarchy 等只读）直接执行（回归全程常态）。
2. **ask 触发**：`edit_transform`（edit_* 规则）触发 opencode 审批弹窗。
3. **拒绝路径**：用户拒绝 → 工具未执行，`get_object_details` 确认 Main Camera
   position 保持 `[0.0,1.0,-10.0]` 不变（场景零副作用）。
4. **批准路径**：用户批准 → 执行成功（position 变更），再次批准 → 还原原值。

**已知限制**：MCP 工具列表与权限规则在**会话启动时快照**——新增工具
（sdk_repair_test_files）与新增规则（sdk_*）需重启 opencode 会话生效；
验证 C 项配置已就位，新会话起生效。

**阶段 5 至此全部封顶**（骨架/核心编辑/模型链路/VRChat 专项/上传管线 +
收尾：TcpWs 实测、多实例机制层、审批流验证）。遗留待办（非阻塞）：
DESIGN.md 章节结构审视（§ 编号乱序、章节膨胀）——阶段 5 完成后执行。

## 30. import_unitypackage（Hikarun 真实包验证，2026-08-15）

**动机**：试用期真实工作流——用户提供 Hikarun 3D 模型（`Hikarun3D_v1.0.unitypackage`，
51MB，已含完整装配：VRCAvatarDescriptor + PipelineManager(blueprintId) + Animator +
18 PhysBone + 表情菜单）。zip 裸装配路径（§9 import_avatar_from_zip）会丢失全部
现成配置，故新增保真导入工具。

**工具契约**：`import_unitypackage(package_path)` →
`{packagePath, importedCount, importedAssets, prefabs, models, primaryPrefab,
validationReport{passed, primaryPrefab, errors}, needsAttention}`。
选主 prefab 规则：优先含 VRCAvatarDescriptor 且名字不含 "Optional" 的 prefab；
无则取第一个。

**实现要点**：
1. `AssetDatabase.ImportPackage` 在 MPE 工具调用上下文是**异步**的（返回时文件
   未落盘），before/after DB diff 不可靠 → 弃用。
2. 改为**自建两遍 tar 解包** `ExtractUnityPackage`：unitypackage = gzip 压缩 tar，
   每个条目是 GUID 目录（asset / asset.meta / pathname / preview.png）。
   pass1 收集 guid→pathname；pass2 写 asset 到目标路径 + asset.meta（保留 GUID
   引用完整性），随后 `AssetDatabase.Refresh(ForceSynchronousImport)`。全程同步可控。
3. tar 解析教训：非 pathname 条目必须跳过整个 padded 块（body+padding），只跳
   padding 会把二进制当 header 解析（python 独立验证先提取 166 条 manifest 通过）。

**验证结果**：删目录→重导→`importedCount=149`、5 prefabs、3 models、primaryPrefab=
`Assets/KululuShippo/Hikarun3D/Hikarun.prefab`、`passed:true`、needsAttention 空；
`prefab_instantiate` 后装配完整（descriptor ViewPosition/控制器/PhysBone 全在）；
`open_vrc_control_panel` 成功弹出。回归 11/11 + smoke 35-tool 全过。

**演进记录**：pipelineVersion 语义（§26 §23）同样适用于此工具——返回结构变化需
先演进文档再改实现；新增工具需重启编辑器（包内 .cs 不自动重编译，纪律④）+
重启 opencode 会话（工具列表启动快照，纪律⑤）。

## 31. Missing Shader 检测（lilToon 依赖，2026-08-15）

**起因**：Hikarun 导入后模型变紫（粉色）。诊断路径：Built-in 管线排除（GraphicsSettings
`m_CustomRenderPipeline: {fileID: 0}`）→ 贴图 guid 全部存在排除绑定失败 → shader guid
（9 个 .mat 引用 5 个不同 guid）在项目 .meta 和 unitypackage 内均不存在 → 材质残留属性
`_AlphaMask`/`_AnisotropyScaleMask`/`_AnisotropyShiftNoiseMask`/`_AnisotropyTangentMap`
（lilToon 独有）反推家族 = lilToon。根因：**模型依赖第三方 shader（lilToon），作者
不打包 shader（VCC 仓库安装是社区惯例），工程未装 → Unity 静默降级
Hidden/InternalErrorShader → 渲染紫**。Console 无报错符合预期（非编译错误）。

**决策**：不做自动 fallback（掩盖依赖缺失 + lilToon 多贴图槽无法完整降级；"能跑但
长得不对"比紫色更隐蔽）。做检测+报告，不修改材质。

**实现（ImportTools.cs）**：
1. `kShaderFamilySignatures`：数据驱动映射表（property 特征 → 家族名），当前 lilToon
   一条（`_AlphaMask`/`_Anisotropy*`/`_Main2ndTex`/`_Main3rdTex`/`_OutlineWidth`/
   `_RimColor`/`_TriMask`/`_EmissionMap`）；Poiyomi 等后续遇到再增条目，不写死 if。
    属性匹配用 `text.IndexOf("- " + prop + ":", Ordinal)`——**`"- _"` + `"_AlphaMask"` 会拼成
    `- __AlphaMask:`（双下划线）导致永不匹配**（首个版本踩坑：字面量匹配成功但拼接全 -1，
    靠逐属性码点诊断定位）；正确写法是 `"- "` 后接完整属性名。
2. `ScanMissingShaders(matPaths)`：对导入资产里的 .mat，`LoadAssetAtPath<Material>`
   判断 shader 缺失（`shader == null || shader.name == "Hidden/InternalErrorShader"`，
   不用 `Shader.Find` 对象引用比较），**家族反推读 .mat 文件文本（m_TexEnvs/m_Floats
   里的属性名），不走 `Material.GetTexturePropertyNames()`**——后者读的是当前绑定
   shader（InternalErrorShader 无自定义槽），shader 缺失时拿不到原始属性名。
   报告进 `needsAttention`，格式：
   `"lilToon shader missing on N material(s) (a.mat, ...): the package references
   lilToon but it is not installed - install it via VCC and re-import (materials
   currently render magenta)"`；家族未知 → 泛化提示（lilToon/Poiyomi 候选）。
3. `ScanMissingShadersInGameObject(go)`：zip 管线（FBX 内嵌材质无独立 .mat 文本）
   场景级检测，只泛化报告。接入 `import_unitypackage` + `import_avatar_from_zip`
   两处 needsAttention。

**测试**：回归 import 组新增场景——构造引用不存在 shader guid + lilToon 特征属性的
最小 .mat unitypackage → 导入 → 断言 needsAttention 含 "lilToon" + 材质名。

**顺手核对（认知偏差修正）**：DESIGN 里"复用/沿用已有机制"措辞核对——`EditValidation`
（EditTools.cs:368）、`InternalApiRegistry`（Compat/InternalApiRegistry.cs:15）均有
实体代码；**唯一例外是 §8 validation_catalog 体系（run_vrchat_validation/
collect_validation_patterns/validation_catalog.json）从未落地**，阶段 3 汇报中"校验
输出走 data-driven catalog"当时未核实即接受。本节实现的映射表即未来 catalog 的
种子数据形态。

**用户处置路径（Hikarun）**：通过 VCC 安装 lilToon → 重开编辑器 → 紫色消失，
模型不改任何东西。这是 VRChat 模型普遍依赖，非模型缺陷。

## 32. 上传陪跑 + SDK 面板警告捕获（Hikarun，2026-08-15）

**背景**：Hikarun 就绪清单 15/15 后陪跑上传。上传被 VRChat 账号信任等级阻塞
（需 New User 才能 Build & Publish，当前为 Visitor）。Build & Test 本地测试通过。

**信任等级事实（官方文档核实）**：VRChat 上传要求账号信任等级 ≥ New User；
Visitor 可本地 Build & Test（仅自己可见，SDK Test Avatars 分区）。**AFK 挂机
不计信任提升**（官方 wiki 明示 "Standing or idling in a room (AFKing)" 无效）；
需实际游玩/互动，VRC+ 订阅给一次提升。Steam/Oculus/Viveport 账号永远不能上传。

**捕获项（工具链检测范围扩展候选，未实现）**：

1. **blueprintId 归属校验**：Hikarun.prefab 自带原作者 blueprintId
   （`avtr_8a311223-...`），登录邮箱账号后 SDK 自动清空（场景实例变空）→
   上传走全新路径。工具链未覆盖"场景实例 vs prefab 来源 blueprintId 差异"，
   需人工意识到。readiness 脚本对"首次上传"（blueprintId 空）报 warn 属
   误报语义——应为 ok + 标注"首次上传"，有 ID 时标注"更新路径"。
2. **SDK 面板性能分级检查**：VRChat 控制面板独有、工具链未覆盖：
   Overall Performance Rank（VeryPoor：80542 三角形 / 19 SMR / 26 材质槽 /
   18 PhysBone / 187 transform / 401 碰撞 / 261 骨骼），加上限阈值
   （70000 tri / 16 SMR）。候选：工具层出性能报告，不设阻塞。
3. **Write Defaults 检查**：SDK 报 "animator states with Write Defaults disabled
   where the animation clip is missing or empty"。已实证：Base/Action/Gesture/
   Sitting 各 5 个空 motion（fileID=0）+ WD OFF 状态（Base 16、Action 40、
   Gesture 37、FX 114），为 VRChat 标准控制器构成（Any State/纯参数状态），
   不阻止上传。候选：扫描 controller 资产统计 WD OFF 且无 clip 的状态。
4. **SDK 面板 UI NRE**：`VRCSdkControlPanelAvatarBuilder.cs:1565` PopupField
   `SetValueWithoutNotify` 空引用（Style 下拉框），构建前面板刷新即触发，
   不影响构建产物。属 SDK 自身缺陷，记录不处理。

**验证载体**：`check_avatar_readiness.py`（可复用脚本，15 项就绪检查 +
基线快照，见 §31 后续工具面）——首次上传场景的 blueprintId 语义待修正。

**陪跑教训**：跨"不可逆边界"前重跑 readiness（不信任旧结果）——本批
blueprintId 在两次检查间变化（SDK 清空），脚本当场抓到差异，验证了
"上传前复检"纪律的必要性。

## 33. Shader 自动安装（白名单驱动）

**动机**：一条龙链路——模型文件进、就绪待上传出。导入流水线在
needsAttention 阶段检测到缺失 shader 家族时，白名单内的家族自动安装，
不在白名单的保持 needsAttention（绝不猜测安装来源）。

**白名单**（kShaderInstallSources，ShaderInstallTools.cs）：
- lilToon → https://lilxyzw.github.io/vpm-repos/vpm.json / jp.lilxyzw.liltoon
- Poiyomi 无条目——只在遇到真实案例时添加（用户决策）。

**安全约束**（用户决策 2026-08-15）：
1. 来源信任：URL 只来自白名单（固定数据），运行时参数绝不进 URL。
2. 下载的 zip 必须复用 ImportTools.SafeUnzip（zip-slip + 字节/条目预算），
   白名单可信不简化解压守卫。
3. package.json name 校验前置：解压前断言 zip 根 package.json 的 name
   等于白名单包 ID，防"错包"。
4. 失败路径清理：下载失败/校验失败/解压失败均清理部分产物；只删除本次
   创建的目录（dirCreatedHere 标记），绝不触碰预存在目录。
5. 上传终点保持手动（用户决策）；审批只在 opencode 侧拦 MCP 工具调用，
   流水线内部直调 TryAutoInstall 不受审批层约束。

**接口**：
- pply_shader_package_install（MCP 工具，apply_* 前缀吃现有审批）：
  {family, localZipPath?, remove?}。localZipPath 用于离线/回归注入；
  remove 幂等卸载。
- 流水线接入：import_unitypackage 的 needsAttention 阶段 →
  ScanMissingShadersWithAutoInstall（ImportTools）：解析家族 →
  TryAutoInstall → 成功移入 autoFixed[] + 记录版本来源；失败消息追加
  "[auto-install failed for <family>: <原因>]"。
- import_avatar_from_zip 的嵌入材质走 ScanMissingShadersInGameObject
  （场景级，无 .mat 文本可推断家族），保持 generic 报告不自动安装。

**验证策略**：回归只测离线可控路径（VrcMcpE2E 已装 lilToon 2.3.4）——
白名单外拒绝（Poiyomi）、alreadyInstalled 短路（不联网）。回归中发现
AlreadyInstalled 短路优先于 localZipPath（TryAutoInstall 先查 Packages/
<PackageId>/package.json，命中即返回，绝不读 localZipPath——已装包绝不
重装/替换，与安全约束 4 一致）。因此 name mismatch / localZipPath 不存在
两条错误路径在 lilToon 已装项目**不可达**，回归断言"短路优先于
localZipPath"（传坏 zip/不存在路径仍返回 alreadyInstalled，且无解压副作用）。
这两条错误路径的自动化测试触发条件：lilToon 未安装的项目环境可用时
（新项目 / CI 干净环境）补入回归；在此之前已手动验证过一次真实下载+
安装成功路径（2026-08-15：临时移走真包→下载 2.3.4 安装→shader 解析
验证→恢复原装）。真实下载+安装成功路径手动验证时注意：VPM repo zip
与 VCC 安装版存在内容级差异（3 个 shader 的 #pragma skip_variants
variant 剔除不同，VCC 版为优化打包），验证后应恢复原装包。
回归环境若 lilToon 未装，import 场景会触发真实下载——以 VrcMcpE2E
（lilToon 已装）为回归基准环境。

**错误路径实测补记（2026-08-16）**：条件满足（lilToon 真未装项目环境）后
实测两条错误路径，均如预期失败且无残留：
1. localZipPath 不存在 → `error: localZipPath does not exist: <path>`（
   解压前返回，无副作用）。
2. localZipPath 指向 name 不匹配 zip（package.json name=com.example.wrongpkg）
   → `error: package.json name mismatch: declared 'com.example.wrongpkg',
   expected 'jp.lilxyzw.liltoon' - refusing to install`（校验在解压/建目录
   之前，Packages/ 无残留）。验证后已用本地 liltoon-2.3.4.zip 恢复原装包，
   回归 12/12 全绿。

**方法警示（实测发现）**：不要用"改名嵌入式包目录"（如 jp.lilxyzw.liltoon
→ .bak_retest）来构造"未安装"环境——Unity 按 package.json 的 name（而非
目录名）注册嵌入式包，改名目录仍会被注册为原包（Editor.log 显示
`jp.lilxyzw.liltoon@file:...\Packages\jp.lilxyzw.liltoon.bak_retest`），
此时 alreadyInstalled 短路命中、remove 的规范化路径删除会误删改名目录
（本次事故：原装 lilToon 2.3.4 被 remove 删掉，靠本地 zip 备份恢复）。
构造未安装环境必须真删目录并重启编辑器。

## 34. Undo �ڦ]�G�{�Ǥ�??�ä��D? mouse-up flush�]2026-08-15�^

**�g?**�GMCP ??�]RecordObject + ?��?�m�^�ͮġA�� Undo.PerformUndo()/
PerformRedo() ����?�q no-op�]��^ performed:true ?�ĪG�^�F��?��?
Ctrl+Z ���`�A�B�@�B�M?��? MCP ???�^�� Main Camera�C

**�ڦ]**�GUnity Undo �t?��?"��?��_"����?�ƥ�@?����?��X�X
Undo.RecordObject ��??�u���b mouse-up ���ƥ�?�~�u���`?? undo ?
�]FlushUndoRecordObjects �q�`��??���b mouse-up �Z��??�Ρ^�C�{�Ǥ�
??�]agent ����?�ơ^?��?�͹�?�ƥ�ARecordObject �����@�����b
"���`?"??�G??�ͮġBundo ?????�C��? Ctrl+Z ���ҥH�@�B�^��
Main Camera�A�O�]?���� GUI �ާ@�D?�� flush �⤧�e�Ҧ����`???
�X�}�`?���F�P�@? undo ?�C?���O�Y? API ��??�ݡA�ӬO��?
RecordObject ��??�e���]���H�b�ާ@ GUI�^�b��?��?�������ߡX�X
?���W���P�_ MPE handler �D?�� / PrefabUtility ?��? / DLL ??��
"��?�O��?��?����"? bug�C

**���`�]�T?�^**�G
1. BatchTools.UndoAction/RedoAction�GPerformUndo/Redo �e��
   Undo.FlushUndoRecordObjects()�]BatchTools.cs�^�C
2. BatchStateMachine.End()�GCollapseUndoOperations �e�� flush�]�_?
   collapse �䤣�쥼�`?�ާ@�^�C
3. McpToolRegistry.OnMessage�G? batch ?�C���u��?�Φ��\�Z flush +
   Undo.IncrementCurrentGroup()�]CloseUndoGroupOutsideBatch�^�X�X?�W�F
   BatchStateMachine ��??���� "one tool call = one undo group" �q?
   ??�A???���e?��??�G? batch ?�Ҧ��u��?��?�b�P�@?�A�@��
   undo �^?��???�]??�G???��?�H��?�سQ�@�� undo �P?�M?�^�C

**??**�Gsmoke undo/hierarchy ���` ?�F�^? 12 ?�]�s�W no-batch undo
granularity�G? batch ?��?? + �@�� undo �u�M?�̪�@���^?�F��?
??? batch ??? undo1 �u�M B�Bundo2 �M A ?�C

**�����`?�ɡ]�w���^**�G??�Z��?�� undo �u��B�a��?�b??������?
Ctrl+Z ��?������? Unity �ƥ� flush�X�Xagent ��?�Ƭy�{���q? undo
�u��M?�]�w��?�^�A��? GUI �V�άO��?�D?��?�A��?�_��?�ƫ�?�C

## 35. asset_fs_probe �����]2026-08-15�^

**�I��**�Gasset_fs_probe�]AnimatorTools.cs�^�O??"���� ghost"�z?�]��24 ����
??�^?��?�α�?�X�X?�i Unity ?�{? Assets/ ��?�����t???�A�t
?�� AssetDatabase.Refresh �e�Z�ַӡC�^?�w����?���C

**�����z��**�]��??���^�G?�z?�w�Q??�A�u��O?�w��½��??��
?�v�]���A?������?��b��?�O�d���X�X�O"�M�z��_??�z?��??�N?
?�n��?????�����īH?"�O�P�@???���t�@���]??�H???�w�ư��G
�^?����?���^�C?�ؤw�Φ���n�����N�Ҧ��G�J?�ݦ�?�����??/??
�N?�����]��0 �� 6 ?�^�A�ӫD�d?�α�?�C

**?��**�G?�� AssetFsProbe ��k���^�FAssetPathArgs �O�d�]get_animator_
controller ���ϥΡ^�FEditorPrefs ? BridgeBootstrap �ϥΤ����v?�C
�u��? 38��37�]C# ?�^�Cserver ?�]??��?�]36�A�쥻�N���]??�u��^�C
