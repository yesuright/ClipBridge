# ClipBridge（中文说明）

**Windows 电脑与 iPhone 之间的剪贴板同步——纯文本、局域网直连、不经过云端。**

英文说明见 [README.md](README.md)。本页是同一份内容的中文版本。

ClipBridge 把长文本（AI prompt / 日志 / 代码片段 / 链接 / 笔记）在 Windows 剪贴板和
iOS 剪贴板之间互传，走你自己的 Wi-Fi：一边复制，另一边粘贴。数据不经过任何第三方
服务器，不需要注册账号。

```text
Windows  Ctrl+C ──►  ClipBridge 客户端 ──► 本机 Relay ──► SQLite
iPhone「Get from ClipBridge」──────────► 本机 Relay ──► Windows 剪贴板（几秒内）

iPhone 复制 ──►「Send to ClipBridge」──► 本机 Relay ──► Windows 剪贴板（几秒内）
```

- **Windows → iPhone**：电脑复制，手机点一下 *Get from ClipBridge*，文本进入手机剪贴板。
- **iPhone → Windows**：手机复制，点一下 *Send to ClipBridge*，几秒内电脑 `Ctrl+V` 即得。

两个方向共用 Windows 应用自带的一个本地中继（Relay）。手机端只需要系统自带的
**快捷指令（Shortcuts）App**，不装任何第三方应用；地址与 token 只在创建时填一次。

## 它解决什么问题

聊天软件当“传输桥”意味着你的 prompt、日志、代码要过别人的服务器，还会被截断或改格式。
ClipBridge 保留同样的使用习惯，但数据只在你的设备之间：

- **纯本地**：Relay 跑在你自己的电脑上，流量不出局域网。
- **逐字节一致**：Unicode、emoji、引号、反斜杠、Tab、CRLF、上百 KB 的长文本原样到达
  （单条上限 2 MiB）。
- **日常零操作**：双击一个 exe，Relay 作为子进程自动管理。

## 核心功能

- Windows 侧剪贴板自动上传；默认每 2 秒轮询接收手机发来的文本。
- 两个 Apple 快捷指令（`Send to ClipBridge` / `Get from ClipBridge`），各 5–9 个动作，
  只用系统自带 Shortcuts 编辑器；地址/token 填一次，之后免输入。
- 首次启动自动完成全部配置：生成 256-bit 随机访问 token、设备 ID，并弹出
  *Connect your iPhone* 窗口显示需要填入快捷指令的地址与 token。
- 托盘常驻 + 原生界面（Home / History / Settings）、System/Light/Dark 主题、
  暂停/恢复、立即检查、历史浏览与安全复制、开机自启（每用户，默认关闭）。
- 自愈运行时：Relay 崩溃后按有界退避自动重启，睡醒/断网自动恢复，端口被占用自动回退
  到下一个空闲端口（持久化并提示）。
- 路径可移植：配置在 `%APPDATA%\ClipBridge`，数据在 `%LOCALAPPDATA%\ClipBridge`；
  与安装位置、用户名、工作目录无关，含空格/中文的路径有测试覆盖。
- 四层防循环，自己复制的内容不会被当成新片段回传。
- 诊断工具：`ClipBridge.exe --doctor`（只读）、`--status`；日志永不包含剪贴板正文和 token。

**明确不支持（当前版本）**：图片、文件、富文本/HTML、二进制；公网部署；HTTPS；
端到端加密；安装器；自动更新；代码签名；离线队列。详见
[Known issues](#known-issues) 与 [docs/SECURITY.md](docs/SECURITY.md)。

## 系统要求

| 项目 | 要求 |
| --- | --- |
| Windows PC | Windows 11（开发与测试平台；Windows 10 64-bit 预期可用但未实测） |
| 运行时 | 无需安装——self-contained 构建（不需要 .NET / Python） |
| iPhone / iPad | 能运行系统自带“快捷指令”的设备 |
| 网络 | PC 与手机在**同一可信 Wi-Fi/局域网**，IPv4；路由器不能开 AP 隔离 |
| 权限 | 不需要管理员（自启动与数据都在用户目录） |

## 安装（Windows）

1. 从 [Releases](../../releases) 下载发布包并解压到任意目录（例如 `D:\Tools\ClipBridge\`，
   路径可以含空格，无需安装）。
2. 双击 **`ClipBridge.exe`**——它会拉起 Relay 子进程和托盘应用。
3. Windows 防火墙首次询问时选择**允许**（仅**专用网络**）。ClipBridge 不会自己改防火墙，
   也不会做端口转发。
4. 首次启动会弹出 *Connect your iPhone* 窗口，显示局域网**地址**和**访问 token**，
   按下一节填进两个快捷指令。

> 当前 exe 未做代码签名，SmartScreen 可能提示“Windows 已保护你的电脑”——
> *更多信息 → 仍要运行*。发布版会附带校验和，下载后可比对。

## 连接 iPhone（Apple 快捷指令）

在系统自带的“快捷指令”App 中创建两个快捷指令，需要 *Connect your iPhone* 窗口里的
**地址**与**token**（之后随时可从托盘 *Connection setup…* 重新打开）。

**快捷指令 1 —— “Send to ClipBridge”**（手机 → 电脑），5 个动作：

1. **Text** —— 中继地址，例如 `http://192.168.1.14:8787`（结尾不加 `/`）
2. **Text** —— 访问 token（只填 43 位字符串本身，不要带 `Bearer `）
3. **Text** —— 本机设备 ID，例如 `iphone`（仅字母数字 `-` `_`）
4. **获取剪贴板**
5. **如果**（剪贴板）有任何值 → **获取 URL 内容**：
   - 方法 **POST**，URL 填 `[地址]/api/v1/clips/shortcut`
   - 请求头：`Authorization` = `Bearer ` + token 变量；`Content-Type` = `application/json`
   - 请求体：选 **JSON（字典）**，键 `device_id` ← 文本 3、`content` ← 剪贴板
   - （必须用字典输入，不要手写 JSON 字符串，否则引号/emoji 会出错。）
   - 否则 → 通知“剪贴板为空”

**快捷指令 2 —— “Get from ClipBridge”**（电脑 → 手机），约 9 个动作：

1. **Text** —— 中继地址（同上）
2. **Text** —— 访问 token
3. **Text** —— 本机设备 ID（如 `iphone`）
4. **URL** —— `[地址]/api/v1/clips?limit=10&exclude_device_id=iphone`
5. **获取 URL 内容**（GET）—— 请求头 `Authorization` = `Bearer ` + token 变量
6. **从输入获取字典** → **获取字典值** `clips`
7. **如果**（clips）有任何值 → **从列表获取项**（第一项）→ **获取字典值** `content` →
   **拷贝到剪贴板**；否则 → 通知“暂无新内容”
8. 可选：用 `size_bytes` / `device_id` 做通知
9. 把两个快捷指令都放到主屏幕或操作按钮

> `exclude_device_id` 的作用是“不取回自己刚发出的内容”。更详细的图文步骤见
> [shortcuts/SEND_TO_CLIPBRIDGE.md](shortcuts/SEND_TO_CLIPBRIDGE.md) 与
> [shortcuts/GET_FROM_CLIPBRIDGE.md](shortcuts/GET_FROM_CLIPBRIDGE.md)。

## 首次使用

1. **启动**——双击 `ClipBridge.exe`，主窗口打开；关窗后驻留托盘。
2. **Connect your iPhone**——首次启动自动弹出，显示：
   - **Address**：本机当前局域网地址，如 `http://192.168.1.14:8787`
   - **Access token**：本安装专属的随机 token（可 Show / Copy）
3. **配置手机上的两个快捷指令**（见上一节）。
4. **双向验证**：电脑复制 → 手机点 Get → 粘贴；手机复制 → 手机点 Send →
   电脑几秒后 `Ctrl+V`。
5. **可选**——Settings → *Start with Windows*，登录后自动进托盘。

地址来自电脑当前局域网 IP；路由器重新分配 IP 后，重新打开 *Connection setup…*
把两个快捷指令里的地址更新一下即可，**token 永远不变**。

## 日常使用

| 目标 | 操作 |
| --- | --- |
| 把电脑剪贴板发给手机 | 电脑复制 → 手机点 **Get from ClipBridge** |
| 把手机剪贴板发给电脑 | 手机复制 → 点 **Send to ClipBridge** → 电脑 `Ctrl+V` |
| 暂停同步（如共享屏幕时） | 托盘 → *Pause sync*（Settings → *Resume sync*） |
| 查看最近历史 | Home → History（最近 50 条，点开阅读，*Copy text* 重新复制） |
| 查看当前地址 | Home → *iPhone connection* → *Copy address* |
| 真正退出 | 托盘右键 → *Exit ClipBridge*（Relay 子进程随之退出） |

关闭窗口只是隐藏，同步仍在托盘继续。

## 工作原理

```text
┌────────────────────────── Windows ──────────────────────────┐      ┌─────────┐
│  ClipBridge.exe（托盘，.NET 8，self-contained）              │      │ iPhone  │
│   ├── 剪贴板监听 → POST /api/v1/clips                       │      │快捷指令 │
│   ├── 每 2 秒轮询 /api/v1/clips/latest → 写入剪贴板          │      │  Send   │
│   └── 管理 ──► relay\ClipBridgeRelay.exe（PyInstaller）      │      │  Get    │
│                  FastAPI + SQLite，0.0.0.0:8787   ◄──────────┼──────┴─────────┘
│                  保存最新一条 + 最近 50 条历史               │   同一可信局域网
│   Job Object：客户端退出时 Relay 必然退出                    │   Bearer token 认证
└─────────────────────────────────────────────────────────────┘
```

- **Relay** 是本机 HTTP 服务（FastAPI + SQLite），保存最新片段与最近 50 条历史；
  所有 `/api/v1/*` 接口都要求共享 Bearer token。
- **客户端**事件驱动地监听 Windows 剪贴板并上传，同时轮询接收、过滤自己上传/刚应用的内容。
- **快捷指令**调用同一 Relay：`POST …/clips/shortcut` 发送，`GET …/clips?exclude_device_id=…`
  接收；Relay 负责 clip_id / 时间戳 / SHA-256（Shortcuts 无法原生计算）。
- **防循环**（四层）：客户端 MarkApplied + 30 秒同哈希忽略；轮询结果排除本设备上传
  （900 秒记忆）；Relay 对同一设备 15 秒内完全相同内容返回 `409 duplicate_content_in_window`；
  clip_id 幂等（`409 duplicate_clip_id`）。

协议细节：[docs/PROTOCOL.md](docs/PROTOCOL.md) · 架构：[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 安全与隐私

完整威胁模型见 [docs/SECURITY.md](docs/SECURITY.md)。要点：

- **无云、无遥测、无账号**。唯一的网络对端是你自己电脑上的 Relay，流量不出局域网。
- **认证**：所有 API 请求携带 256-bit 随机 token（每台安装各自生成），常数时间比较。
- **传输是局域网明文 HTTP**：token 与剪贴板文本在传输中不加密。这是本版本公开声明的
  取舍——只用于可信的家庭/办公 Wi-Fi，不要在公共 Wi-Fi 使用，**不要在路由器做端口转发**。
- **日志按设计与测试保证不含正文/token**：只有 id、大小、8 位哈希前缀和状态码。
- **存储**：剪贴板文本在本机 SQLite（`%LOCALAPPDATA%\ClipBridge\clipbridge.db`），
  token 在 `%APPDATA%\ClipBridge\config.json`，均受你的 Windows 账户保护。
- **Token 为全部设备共享**（单一信任边界）：拿到 token 即可读写历史——不要发到聊天、
  截图或仓库里。
- **最小权限**：不需要管理员；不装服务/驱动；**绝不修改防火墙、VPN、代理设置**。
- **子进程安全**：Relay 在 Job Object（kill-on-close）内作为子进程运行，token 只经
  环境变量传递，绝不出现在命令行。

漏洞报告方式见 [SECURITY.md](SECURITY.md)。

## Known Issues（已知问题）

1. **特定 HTTP 代理环境可能劫持本机请求**（已知，修复决策待定）。
   如果某个 Windows 进程是从设置了 `HTTP_PROXY`/`HTTPS_PROXY`、且 `NO_PROXY` 未排除
   `127.0.0.1` 的 shell 启动的，.NET 可能把 ClipBridge 的本机回环请求也交给该代理；
   代理不认识你本机的 Relay，于是轮询与上传持续返回 `relay_404`（健康检查可能仍正常）。
   - **影响**：仅该 shell/会话内同步失效。从资源管理器/开始菜单启动的日常托盘实例
     不受影响（实测：测试机代理常开，真实日常实例日志中 `relay_404` 计数为 0）。
   - **原因**：ClipBridge 有意沿用 .NET 默认代理解析，未对回环地址做特殊处理；
     是否改变属于公开的设计决策（[docs/PORTABILITY.md §3.6](docs/PORTABILITY.md)）。
   - **绕过建议**（按受影响的 shell 任选其一）：把回环加入 `NO_PROXY`
     （`NO_PROXY=localhost,127.0.0.1`）；或在代理客户端里开启“绕过本地地址”；
     或改从资源管理器/开始菜单启动。
   - **风险提示**：被劫持的请求会把剪贴板文本与 Bearer token 带给代理进程——这也是
     只把 ClipBridge 指向你可控 Relay 的又一个理由。
   - ClipBridge **不会**替你修改系统代理、VPN 或防火墙；未来的可选修复（启动时对回环
     强制直连）已预留，本版本未实现。

2. **未签名二进制**——首次运行可能有 SmartScreen 提示，代码签名在未来里程碑。

3. **`clipbridge.local`（mDNS）为实验特性**——部分 iPhone 解析不了；请用
   *Connection setup…* 显示的 IP 地址。

4. **局域网 IP 变化后需要更新快捷指令**——DHCP 续租/切换 Wi-Fi 会改变 PC 地址；
   token 始终有效。地址变化时托盘会提示。

5. **端口被占用时自动更换**——8787 被其他程序占用时自动换到下一个空闲端口并提示；
   需要把两个快捷指令里的端口一并更新。

6. **没有离线队列**——发送时对方不可达，该条不会排队；恢复连接后重发一次即可
   （上传有 3 次退避重试）。

7. **剪贴板被占用**——其他程序短暂锁住 Windows 剪贴板时，客户端重试若干次后跳过该次
   复制（记日志，不崩溃）。

## 排障

先跑只读诊断：`ClipBridge.exe --doctor`（输出配置/数据目录、Relay 组件、端口可绑定、
LAN 地址、自启动健康；只打印 8 位 token 指纹，不打印 token）。

| 现象 | 检查 / 处理 |
| --- | --- |
| 手机浏览器打不开 `http://<PC-IP>:8787/api/v1/health` | 是否同一 Wi-Fi；PC IP 是否变了；路由器是否 AP 隔离 |
| Connection refused | Relay 没在跑：启动 `ClipBridge.exe`，看托盘状态与 `--doctor` |
| 能上网但连不上 PC（超时） | Windows 防火墙拦截：放行 `ClipBridgeRelay.exe`（仅专用网络），网络配置文件切为“专用” |
| 快捷指令报 **401** | token 不一致或带了空格/换行；从 *Connection setup…* 重新复制 |
| Get 返回 **404 not_found** | 正常空状态：还没有“其他设备”的新内容，先从电脑发一条 |
| Send 返回 **409** | 15 秒内重复发送相同内容（去重窗口），视为已发送 |
| **413** | 超过单条 2 MiB 上限，拆分发送 |
| VPN/扩展坞之后连不上 | 自动选择的网卡变了；手动改地址或配置 `LanIpOverride` |
| 一直 Offline | 等 1–5 分钟自愈，或 Settings → *Restart relay*；仍失败看日志 |
| “Relay component missing” | `relay\` 目录没和 `ClipBridge.exe` 一起复制；重新完整解压 |
| 日志在哪 | `%LOCALAPPDATA%\ClipBridge\logs\`（客户端 + Relay；只有 id/大小/哈希前缀） |

## 从源码构建

前置：**.NET 8 SDK** 与 **Python 3.12+**（3.13 已测试）。脚本以 Windows PowerShell 5.1 为准。

```powershell
# 1. Relay（开发运行，仅回环）
cd services\relay
python -m venv .venv
.\.venv\Scripts\pip install -r requirements.txt
Copy-Item .env.example .env          # 设置 CLIPBRIDGE_TOKEN（必填，缺失即拒绝启动）
.\.venv\Scripts\python run.py        # http://127.0.0.1:8787（Swagger 见 /docs）

# 2. Windows 客户端（开发运行）
dotnet run --project apps\windows\src\ClipBridge.Win

# 3. 发布构建（self-contained 客户端 + PyInstaller Relay）
powershell -ExecutionPolicy Bypass -File scripts\build-daily.ps1 -OutputDir artifacts\ClipBridge
```

`scripts/dev-relay.ps1`、`scripts/dev-client.ps1`、`scripts/start-lan-dev.ps1`
（LAN 模式，带安全提示）封装了开发流程；环境变量与存储覆盖（`CLIPBRIDGE_HOME` 等）见
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)。

### 测试

```powershell
# Relay 单元/集成测试
cd services\relay && .\.venv\Scripts\python -m pytest && cd ..\..

# Windows 核心测试
dotnet test apps\windows\tests\ClipBridge.Core.Tests

# 发布产物集成冒烟（真实进程、隔离 HOME、无需管理员）
powershell -ExecutionPolicy Bypass -File scripts\verify-daily.ps1 -Artifact artifacts\ClipBridge

# 跨环境可移植性验证（全新配置 / 中文路径 / 端口冲突 / 重复启动 / 损坏配置 / 缺组件）
powershell -ExecutionPolicy Bypass -File scripts\verify-portability.ps1 -Artifact artifacts\ClipBridge

# 提交前密钥扫描
powershell -ExecutionPolicy Bypass -File scripts\secret-scan.ps1
```

iPhone 真机验收按 [docs/DEVICE_TEST.md](docs/DEVICE_TEST.md) 手动执行。

## 目录结构

```text
apps/windows/        C# .NET 8 托盘客户端（Core + Win + xunit 测试）
services/relay/      Python FastAPI Relay（SQLite，PyInstaller 打包）+ pytest
shortcuts/           Apple 快捷指令配置指南（Send / Get）
docs/                架构 / 协议 / 安全 / 开发 / 日常使用 / 可移植性
scripts/             构建 / 验证 / 开发辅助脚本（Windows PowerShell）
tests/e2e/           全链路端到端脚本（真实剪贴板 + 模拟手机流程）
```

## 贡献

Issue 与 PR 欢迎，构建/测试清单见 [CONTRIBUTING.md](CONTRIBUTING.md)。请保持安全不变量
（日志不含 token/正文、不新增对外服务），提交前跑完测试套件。

## 许可证

本项目以 [MIT License](LICENSE) 发布——Copyright (c) 2026 yesuright。第三方依赖沿用各自
的许可证，清单见 `apps/windows/src/**/*.csproj` 与 `services/relay/requirements.txt`。

---

**状态**：代码、测试与发布构建已完成，本仓库已就绪，可进行首次公开发布。
