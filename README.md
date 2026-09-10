# ClipBridge

个人使用的跨设备**纯文本剪贴板同步**工具：Windows 11 PC 与 iPhone 之间互相传递长文本
（AI prompt / handoff / 日志 / 代码），不再依赖微信、QQ 等聊天软件。

> **Milestone 4 — Polished Windows UI**：Home / History / Settings 原生主窗口，
> Light / Dark / System 主题、原创图标、托盘和安全复制。同步核心与 API 保持不变。
> iPhone 使用当前 PC 的 LAN IP 地址；`.local` 实验未在真实 iPhone 上成功。
> M4 iPhone 回归：**READY_FOR_USER_RETEST**。iPad 继续使用 Apple Universal Clipboard，
> 不作为单独产品界面。设计及行为见 [docs/UI.md](docs/UI.md)。

---

## 它能做什么（Milestone 4）

```
Windows 复制文本 ──► ClipBridge 客户端 ──► Relay ──► 存储 latest + history
iPhone (Apple Shortcuts) ──► POST /clips/shortcut ──► Relay ──► Windows 轮询 ──► 写入剪贴板
```

- 复制后自动同步出去；其他设备的文本几秒内写入 Windows 剪贴板（默认 2s 轮询）
- **iPhone 双向同步**：同一可信 Wi-Fi 下，两个 Apple Shortcuts（Send / Get）
  即可工作，不依赖任何第三方 App，无需每次输入 URL/token/device ID
- **日常使用 = 双击 `artifacts\ClipBridge\ClipBridge.exe`**：
  - 无 PowerShell、无 Python、无 .NET 安装需求（self-contained + PyInstaller 打包）
  - 托盘常驻；relay 作为子进程自动启动、自动健康检查、崩溃自动重启（有界退避）
  - 单实例（重复双击只会激活已有实例）；退出不留孤儿进程（Job Object 保证）
  - 可选“开机自启”（HKCU Run，无需管理员，默认关闭）
  - 网络变化 / 睡醒自动恢复；LAN 地址变化时托盘气泡提示
  - Home 显示并复制当前物理 LAN 地址；mDNS 保留为实验实现，不作为首选地址
  - 原生 Home / History / Settings；关闭隐藏到托盘，Exit ClipBridge 退出
  - System / Light / Dark 主题，历史按需阅读及安全复制，原创多尺寸图标
- `GET /clips/latest?exclude_device_id=<自己>`：设备不会取回自己产生的记录
- `POST /clips/shortcut`：Shortcut 只需发 `device_id` + `content`，relay 生成
  `clip_id` / `content_hash` / `created_at`（Apple Shortcuts 无法原生做 UUID/SHA-256）
- 单条最大 **2 MiB** UTF-8 纯文本，不会截断
- 最近 **50 条**历史记录（`GET /api/v1/clips?limit=N`）
- Windows 与 iPhone 的设备 ID 可配置
- 剪贴板循环（loop）在架构层防住（详见下）
- 中英文 / emoji / Markdown / JSON / CRLF / tabs 完整 round-trip

## 不支持（明确范围）

图片、文件、HTML/富文本剪贴板、二进制、密码管理集成。Windows 检测到非文本剪贴板时安全忽略。
公网部署 / HTTPS / Tailscale / E2E 加密 / pairing UI / 正式安装器 **不在本里程碑**。

## 目录结构

```text
ClipBridge/
├─ apps/
│  └─ windows/                 # C#/.NET 8 客户端（Core 库 + 原生主窗口 / tray 宿主 + 生命周期管理）
├─ services/
│  └─ relay/                   # Python FastAPI + SQLite relay（可 PyInstaller 打包为单 exe）
├─ shortcuts/                  # iPhone Shortcuts 接入指南（两个 Shortcut）
├─ docs/                       # ARCHITECTURE / PROTOCOL / SECURITY / DEVELOPMENT / DEVICE_TEST
│                              #   / DAILY_USE / LIFECYCLE
├─ scripts/
│  ├─ build-daily.ps1          # 构建 daily-use artifact（publish + pyinstaller → artifacts\ClipBridge）
│  ├─ verify-daily.ps1         # daily artifact 集成冒烟测试（单实例/崩溃恢复/无孤儿/干净退出…）
│  ├─ dev-relay.ps1 / dev-client.ps1 / start-lan-dev.ps1 / show-lan-setup.ps1
│  ├─ lib/LanSetup.psm1        # 可测试的 LAN 地址选择逻辑
│  └─ tests/test-lan-setup.ps1 # LAN 逻辑单测
├─ tests/
│  └─ e2e/                     # 全链路端到端验证脚本（含模拟 iPhone Shortcut 流程）
├─ artifacts/                  # （构建生成，gitignored）daily-use 产物
├─ NuGet.Config                # 仓库级 NuGet 镜像配置（不影响全局）
└─ README.md
```

## 日常使用（最终用户体验）

```powershell
# 构建 daily-use artifact（一次性；内含 relay 打包 + 客户端 self-contained publish）
powershell -ExecutionPolicy Bypass -File scripts\build-daily.ps1
```

之后用户只需要：**双击 `artifacts\ClipBridge\ClipBridge.exe`**。
主窗口提供 Home / History / Settings。开机自启、暂停/恢复、复制 iPhone 地址和日志也可通过托盘操作。
详细普通用户指南：`docs/DAILY_USE.md`；进程/生命周期细节：`docs/LIFECYCLE.md`。

## 快速开始（本地开发）

### 1. Relay

需要 Python 3.12+。

```powershell
cd services\relay
python -m venv .venv
.\.venv\Scripts\pip install -r requirements.txt
Copy-Item .env.example .env          # 然后编辑 CLIPBRIDGE_TOKEN（必填）
.\.venv\Scripts\python run.py        # http://127.0.0.1:8787 （Swagger: /docs）
```

Relay 没有 token 会拒绝启动（fail-fast）。token 生成：
`python -c "import secrets; print(secrets.token_hex(32))"`

### 2. Windows 客户端

```powershell
# 需要 .NET 8 SDK
dotnet run --project apps\windows\src\ClipBridge.Win
```

开发运行时若旁边没有打包的 relay exe，客户端自动进入 “external relay” 模式
（只同步，不管理 relay 进程）——开发体验与 M2 一致。启动后：系统托盘出现 ClipBridge 图标，
复制任意文本即自动上传；主窗口和托盘提供暂停同步 / 立即检查 / 复制 iPhone 地址 / 日志 /
数据目录 / 开机自启 / 退出。

### 3. LAN 模式（iPhone 接入）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\start-lan-dev.ps1
```

该脚本会：启动 relay（绑定 `0.0.0.0`，LAN 模式）+ 启动 Windows 客户端 + 显示建议的
LAN URL 与安全提示。**它不会**：改防火墙 / 暴露公网 / 打印 token / 覆盖你的配置。

> 默认仍然是 `127.0.0.1`（仅本机）。只有显式配置 `CLIPBRIDGE_BIND_HOST=0.0.0.0`
> 后 relay 才对外监听。LAN HTTP 只用于可信家庭/办公 Wi-Fi（token 明文传输，
> 详见 `docs/SECURITY.md`），不要做路由器端口转发。

拿到 LAN URL 后，按 `docs/DEVICE_TEST.md` 在 iPhone 上配置两个 Shortcut
（约 5–10 个动作，无需第三方 App），完成真机双向同步验证。

### 4. 模拟 iPhone 发送（curl / PowerShell）

```powershell
$token = "<CLIPBRIDGE_TOKEN>"
# Shortcut 简化端点（推荐，relay 生成 clip_id/hash/created_at）:
Invoke-RestMethod -Method POST -Uri "http://127.0.0.1:8787/api/v1/clips/shortcut" `
  -Headers @{ Authorization = "Bearer $token" } -ContentType "application/json" `
  -Body (@{ device_id = "iphone"; content = "Hello from iPhone" } | ConvertTo-Json)

# 获取最新“非本设备”内容:
Invoke-RestMethod -Uri "http://127.0.0.1:8787/api/v1/clips/latest?exclude_device_id=iphone" `
  -Headers @{ Authorization = "Bearer $token" }
```

几秒内 Windows 剪贴板会变成 `Hello from iPhone`，随后 `Ctrl+V` 粘贴即可。
完整的 iPhone Shortcuts 配置见 `shortcuts/` 与 `docs/DEVICE_TEST.md`。

## 如何测试

```powershell
# Relay 单元/集成测试（98 个：含 shortcut 简化创建 / exclude_device_id / 去重 / Unicode /
#   admin shutdown 端点…）
cd services\relay
.\.venv\Scripts\pip install -r requirements-dev.txt
.\.venv\Scripts\python -m pytest

# Windows 单元测试（160 个：含 M4 界面/安全复制/主题，以及 M2/M3 生命周期/状态/LAN/
#   mDNS 编解码/配置迁移/日志轮转/启动注册…）
dotnet test apps\windows\ClipBridge.sln

# LAN helper 逻辑测试（纯函数，无需 Pester）
powershell -ExecutionPolicy Bypass -File scripts\tests\test-lan-setup.ps1

# 全链路端到端（真实剪贴板 + 真实 relay + 真实客户端 + 模拟 iPhone Shortcut 流程）
powershell -ExecutionPolicy Bypass -File tests\e2e\run-end-to-end.ps1

# M4.1 cold-path 可靠性压测（fresh launch / idle / relay 恢复后的第一次复制 → 100% 成功）
# 默认 50 cycles；-Scenario All|Fresh|Idle|RelayRestart；隔离实例 + 独立端口，不影响真实会话
powershell -ExecutionPolicy Bypass -File tests\e2e\cold-path-reliability.ps1 -Cycles 50

# daily artifact 集成冒烟（无控制台/单实例/relay 启动/崩溃恢复/干净退出/无孤儿）
powershell -ExecutionPolicy Bypass -File scripts\verify-daily.ps1

# Relay lifecycle 恢复压测（意外退出 → 自动恢复；restart budget 耗尽 → 自动自愈，无需用户重启）
# 20 次 kill/recover + 4 次完整重启 + 2 轮「耗尽 budget → 自愈 → 稳定运行 → 再次崩溃仍能恢复」
powershell -ExecutionPolicy Bypass -File tests\e2e\relay-lifecycle-stress.ps1
```

## 循环（clipboard loop）是怎么防住的

Windows 复制 A → 上传 A → relay → Windows 又下载 A → 写剪贴板 A → listener 触发 → 再上传 A → …

ClipBridge 在 **4 层**防御：

1. **客户端应用层**：写到剪贴板前先 `MarkApplied(clip_id, content_hash)`，随后 listener 看到同一
   content_hash 直接忽略（30s 窗口），不重新上传 —— 主防线。
2. **客户端轮询层**：轮询结果过滤掉本设备（device_id == 自己）及本会话上传过的 clip_id 回显，
   不会用旧内容反复改写剪贴板。
3. **Relay 层**：同一设备在 15s 内发布完全相同的 content（同 content_hash）→ `409
   duplicate_content_in_window`，history 里不会出现重复记录。
4. **幂等**：clip_id 是主键，重复 POST 同一 clip_id → `409 duplicate_clip_id`。

**不会误伤用户**：内容相同的两次复制是不同的事件（不同 clip_id），超过窗口后会被正常接受；
"字符串相同就忽略" 的设计被显式避免。详见 `docs/PROTOCOL.md`。

## 当前限制

- 仅纯文本（`text/plain`）；非文本剪贴板被忽略
- 本地 HTTP + token；**LAN 明文 HTTP 仅限可信私有网络**，生产需 HTTPS（见 `docs/SECURITY.md`）
- 无离线队列；网络恢复后自动恢复同步
- 无安装器、自动更新、代码签名（后续里程碑）
- token 为全设备共享（无 per-device 键）；E2E 加密为未来方向，本轮未实现
- 剪贴板在 Windows 侧被其他程序短占时，客户端重试 5 次后跳过该次复制（不崩溃）
- M3 已由用户完成 iPhone 双向验证；M4 真机回归待用户执行（`docs/DEVICE_TEST.md`）

## 文档索引

| 文档 | 内容 |
| --- | --- |
| `docs/UI.md` | M4 原生界面架构、状态绑定、主题、隐私、键盘与 DPI |
| `docs/ARCHITECTURE.md` | 组件、数据流、接收策略、信任边界、LAN/Shortcut 设计 |
| `docs/PROTOCOL.md` | 端点、schema、`/clips/shortcut`、`exclude_device_id`、鉴权、hash、dedup、错误码 |
| `docs/SECURITY.md` | 威胁模型、token 处理、LAN 明文 HTTP 定位、HTTPS、已知限制 |
| `docs/DEVELOPMENT.md` | 前置条件、命令、环境变量、LAN 脚本、NuGet 配置说明 |
| `docs/DEVICE_TEST.md` | **iPhone 真机手测指南**（前置、URL、两个 Shortcut、双向测试、排障） |
| `docs/DAILY_USE.md` | **普通用户日常指南**：启动 / 开机自启 / 暂停 / 退出 / 排障 / IP 变化 |
| `docs/LIFECYCLE.md` | 进程模型、parent/child 生命周期、崩溃恢复、睡醒恢复、数据位置 |
| `shortcuts/SEND_TO_CLIPBRIDGE.md` | iPhone Shortcuts：发送剪贴板到 Windows |
| `shortcuts/GET_FROM_CLIPBRIDGE.md` | iPhone Shortcuts：拉取/复制最新「非本设备」文本 |

## 技术选型（Milestone 1 + Milestone 3 增量）

- **Windows 客户端**：C# / .NET 8 + WinForms（隐藏窗口 + `AddClipboardFormatListener`），
  核心逻辑放在可单元测试的 `ClipBridge.Core`（无 UI 依赖）。
  不用 Electron，不引入 Chromium。M3 增量：self-contained publish（用户无需装 .NET）、
  单实例（mutex + named pipe）、Relay 子进程管理（Job Object kill-on-close）、
  事件驱动网络监控、应用内 mDNS 应答器、HKCU Run 开机自启、可观测状态模型。
- **Relay**：Python 3.13 + FastAPI + uvicorn，存储用 stdlib `sqlite3`（零额外基础设施）。
  M3 增量：PyInstaller onedir 打包成单个自包含 exe（用户无需 Python）；
  新增 `POST /api/v1/admin/shutdown`（Bearer 鉴权，仅用于优雅停服，不影响既有协议）。
  理由见 `docs/ARCHITECTURE.md`。
- **传输**：短轮询（默认 2s，可配 0.5–60s）。理由见 `docs/ARCHITECTURE.md`。

## 许可

个人工具项目；未定 license（如需开源请在后续里程碑决定）。
