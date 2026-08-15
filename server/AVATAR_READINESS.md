# Hikarun 上传就绪状态（2026-08-15 固化）

> 本文件把"易逝的、靠内存记住的就绪状态"固化成以后可核对的证据。
> **验证结论只对当时的环境成立**：任何环境变化（Unity 升级、VCC 重新同步、
> lilToon 重装）都会使本文档的结论失效，上传前必须重新验证（见下方流程）。

## 验证时的环境（与结论绑定的关键上下文）

| 项目 | 值 | 校验方法 |
|---|---|---|
| Unity 版本 | 2022.3.22f1c1 | `get_project_info` |
| VRCSDK | 3.10.4（com.vrchat.avatars / com.vrchat.base，D:/VCC/local-packages/） | `get_project_info` / manifest.json |
| lilToon | 2.3.4（内嵌包 `Packages/jp.lilxyzw.liltoon`） | 无 manifest 引用，embedded package |
| 项目 | VrcMcpE2E | — |
| 场景 | 默认场景 + Hikarun 根对象 | — |

## 当时通过的验证（2026-08-15，全部属实）

1. **check_avatar_readiness.py：15/15 检查通过**
   `python check_avatar_readiness.py Hikarun`
   覆盖：场景根存在、VRCAvatarDescriptor、5 个动画层
   （Base/Additive/Gesture/Action/FX）、PhysBones、材质/shader 引用、表情菜单等。
2. **SDK 控制面板（VRChat SDK > Show Control Panel）**：Build & Test 通过。
3. **无 lilToon missing shader 报告**（闭环验证：真实 lilToon guid 材质导入不报缺失）。

## 未完成（阻塞于账号信任等级，非技术问题）

- **正式上传**：账号当前为 Visitor，VRChat 要求 New User 才能上传
  （官方信任等级规则，非本工具问题）。用户决定不刷等级，等账号自然升级。

## 上传触发流程（账号升级到 New User 后执行）

```powershell
# 0. 环境核对（最重要——验证结论绑定环境，任何变化都使旧结论失效）
#    Unity 版本 / VRCSDK 版本 / lilToon 版本与上表一致？

# 1. 打开编辑器，确认 bridge 已启动（channel 文件存在于 .unity-mcp/）

# 2. 重跑就绪检查（编辑器连接中）
python check_avatar_readiness.py Hikarun    # 期望 15/15

# 3. SDK 控制面板 → Build & Test（期望通过）

# 4. 控制面板手动点 Publish（保持手动终点，设计如此）
```

## 常见环境漂移警示（本项目实测踩过的坑）

- **VCC 重新同步可能把官方包内已知坏文件覆盖回来**（DESIGN §26/§27）：
  `get_project_info.sdkHealth.status` 非 ok 时先 `sdk_repair_test_files` 再继续。
- **file: 引用包源码变更在本机 2022.3.22f1c1 不自动重编译**（DESIGN §25）：
  改过 `Packages/com.vrchat-mcp` 源码后必须重启编辑器，核对 `discovered N tools`。
- 旧验证结论不可凭记忆直接沿用——环境变了，结论失效，重新跑第 2 步。