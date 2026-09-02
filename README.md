# ClipBridge

个人使用的跨设备**纯文本剪贴板同步**工具：Windows 11 PC、iPhone、iPad 之间互相传递长文本
（AI prompt / handoff / 日志 / 代码），不再依赖微信、QQ 等聊天软件。

> **当前状态：Milestone 1 (MVP)** — Windows 客户端 + Relay 已验证端到端跑通。
> 打开 relay 的 `/docs` (Swagger) 即可手动调试 API。**无正式视觉 UI**（属于后续里程碑）。

---

## 它能做什么（Milestone 1）

```
Windows 复制文本 ──► ClipBridge 客户端 ──► Relay ──► 存储 latest + history
iPhone/iPad (API/Shortcuts) ──► POST ──► Relay ──► Windows 轮询 ──► 写入 Windows 剪贴板
```

- 复制后自动同步出去；其他设备的文本几秒内写入 Windows 剪贴板（默认 2s 轮询）
- 单条最大 **2 MiB** UTF-8 纯文本，不会截断
- 最近 **50 条**历史记录（`GET /api/v1/clips?limit=N`）
- 多设备（`windows-main` / `iphone` / `ipad` …），设备 ID 可配置
- 剪贴板循环（loop）在架构层防住（详见下）
- 中英文 / emoji / Markdown / JSON / CRLF / tabs 完整 round-trip

## 不支持（Milestone 1 明确范围）

图片、文件、HTML/富文本剪贴板、二进制、密码管理集成。Windows 检测到非文本剪贴板时安全忽略。

## 目录结构

```text
ClipBridge/
├─ apps/
│  └─ windows/                 # C#/.NET 8 客户端（Core 库 + 最小 tray 宿主）
├─ services/
│  └─ relay/                   # Python FastAPI + SQLite relay
├─ shortcuts/                  # iPhone/iPad Shortcuts 接入指南
├─ docs/                       # ARCHITECTURE / PROTOCOL / SECURITY / DEVELOPMENT
├─ scripts/                    # dev-relay / dev-client / secret-scan
├─ tests/
│  └─ e2e/                     # 全链路端到端验证脚本
└─ README.md
```

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

或让 5 秒内改 config：

```powershell
# 设置匹配 relay 的 token（二选一）
$env:CLIPBRIDGE_DEVICE_TOKEN = "<同一个 token>"
# 或写入 %APPDATA%\ClipBridge\config.json
@{
  RelayUrl = "http://127.0.0.1:8787"
  DeviceId = "windows-main"
  DeviceToken = "<同一个 token>"
  PollIntervalMs = 2000
} | ConvertTo-Json | Set-Content "$env:APPDATA\ClipBridge\config.json"
```

启动后：系统托盘出现 ClipBridge 图标（系统图标，非定制），复制任意文本即自动上传；
其他设备 POST 的文本自动写入剪贴板。菜单：暂停同步 / 立即检查 / 打开日志目录 / 退出。

### 3. 模拟 iPhone 发送（curl / PowerShell）

```powershell
$token = "<CLIPBRIDGE_TOKEN>"
$body = @{
  clip_id      = [guid]::NewGuid().ToString()
  device_id    = "iphone"
  content_type = "text/plain"
  content      = "Hello from iPhone"
  content_hash = (Get-FileHash -InputObject "Hello from iPhone" -Algorithm SHA256).Hash.ToLower()
  created_at   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
  origin       = "shortcut"
} | ConvertTo-Json
Invoke-RestMethod -Method POST -Uri "http://127.0.0.1:8787/api/v1/clips" `
  -Headers @{ Authorization = "Bearer $token" } -ContentType "application/json" -Body $body
```

几秒内 Windows 剪贴板会变成 `Hello from iPhone`，随后 `Ctrl+V` 粘贴即可。
完整的 iPhone Shortcuts 配置见 `shortcuts/`。

## 如何测试

```powershell
# Relay 单元/集成测试（61 个）
cd services\relay
.\.venv\Scripts\pip install -r requirements-dev.txt
.\.venv\Scripts\python -m pytest

# Windows Core 单元测试
dotnet test apps\windows\ClipBridge.sln

# 全链路端到端（真实剪贴板 + 真实 relay + 真实客户端）
powershell -ExecutionPolicy Bypass -File tests\e2e\run-end-to-end.ps1
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
- 本地 HTTP + token；生产部署需 HTTPS（见 `docs/SECURITY.md`）
- 无离线队列；网络恢复后自动恢复同步
- 无正式 UI / 自定义图标 / 安装器（后续里程碑）
- token 为全设备共享（无 per-device 键）；E2E 加密为未来方向，本轮未实现
- 剪贴板在 Windows 侧被其他程序短占时，客户端重试 5 次后跳过该次复制（不崩溃）

## 文档索引

| 文档 | 内容 |
| --- | --- |
| `docs/ARCHITECTURE.md` | 组件、数据流、接收策略、信任边界 |
| `docs/PROTOCOL.md` | 端点、schema、鉴权、hash、时间、dedup、错误码、限制 |
| `docs/SECURITY.md` | 威胁模型、token 处理、日志脱敏、HTTPS、已知限制 |
| `docs/DEVELOPMENT.md` | 前置条件、命令、环境变量、调试 |
| `shortcuts/SEND_TO_CLIPBRIDGE.md` | iPhone Shortcuts：发送剪贴板到 Windows |
| `shortcuts/GET_FROM_CLIPBRIDGE.md` | iPhone Shortcuts：拉取/复制最新文本 |

## 技术选型（Milestone 1）

- **Windows 客户端**：C# / .NET 8 + WinForms（隐藏窗口 + `AddClipboardFormatListener`），
  核心逻辑放在可单元测试的 `ClipBridge.Core`（无 UI 依赖）。
  不用 Electron，不引入 Chromium。
- **Relay**：Python 3.13 + FastAPI + uvicorn，存储用 stdlib `sqlite3`（零额外基础设施）。
  理由见 `docs/ARCHITECTURE.md`。
- **传输**：短轮询（默认 2s，可配 0.5–60s）。理由见 `docs/ARCHITECTURE.md`。

## 许可

个人工具项目；未定 license（如需开源请在后续里程碑决定）。
