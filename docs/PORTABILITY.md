# ClipBridge 可移植性与可靠性（Portability Hardening）

本文记录把 ClipBridge 从"只在我的笔记本上可用"改造成"陌生用户也能安装运行"的过程：
审计到的问题、修复方式、验证证据，以及仍然存在的平台限制。

面向普通用户的操作说明见 [DAILY_USE.md](DAILY_USE.md)；进程生命周期见 [LIFECYCLE.md](LIFECYCLE.md)。

---

## 1. 目标与边界

目标：在新用户名、新安装路径、新网络环境、没有开发者配置的 Windows 电脑上，
双击 `ClipBridge.exe` 就能完成首次配置并正常同步；已有用户的数据与 Shortcut 不被破坏。

不变的部分（刻意保留）：

- iPhone Shortcuts 使用的 HTTP 协议、路径、`Bearer` 认证方式
- `%APPDATA%\ClipBridge\config.json` 的位置与字段名
- relay 的 SQLite 表结构与轮询/去重/循环防护逻辑
- 托盘、主窗口、主题等交互
- 不接管防火墙 / 杀毒 / 系统安全策略

---

## 2. 审计结论（按严重程度）

| # | 严重度 | 问题 | 影响 | 修复 |
|---|---|---|---|---|
| 1 | **P0** | 没有配置文件时程序直接退出，要求用户手工编辑 JSON 并"自己发明" token | 陌生用户**完全无法**首次运行 | `ConfigBootstrap`：自动生成 256-bit token、唯一 DeviceId、唯一 mDNS 名，原子写盘，并弹出设置窗口 |
| 2 | **待决策** | `HttpClient` 遵循 `HTTP_PROXY`，若该代理拦截 `127.0.0.1` 且 `NO_PROXY` 未排除本机 | 在这类 shell 中同步请求会被代理截获，表现为 `relay_404` | **按要求保持原状，未修改**；证据与选项见 §3.6 |
| 3 | **P0** | `RelayUrl` 手写错误（如 `192.168.1.14:8787`）→ `new Uri()` 抛 `UriFormatException` | 启动即崩溃 | `BuildApiBaseUri` 不再抛异常；`Normalize` 补 scheme/校验并给出说明 |
| 4 | **P0** | `RelayPort` 与 `RelayUrl` 端口不一致 | 客户端轮询一个没人监听的端口，表现为"永远 Offline" | 规范化：loopback URL 端口跟随 `RelayPort`，并记录变更原因 |
| 5 | **P0** | 8787 被别的程序占用时进入永久 `Failed`，每 5 分钟重试同一端口 | 新用户可能永远无法使用 | 启动前探测 + 运行时回退到下一个空闲端口，写入配置并提示更新 Shortcut |
| 6 | **P0** | 打包产物若位于源码仓库内，会从 `services/relay/.env` 导入开发者的 token | 向他人分发时可能泄漏他人凭证 | 只在 `apps\windows\...` 开发布局下导入；打包目录一律不导入 |
| 7 | **P1** | 缺少 `relay\ClipBridgeRelay.exe` 时静默进入 external 模式，界面显示 Offline | 用户不知道是"装漏了" | `RelayState.Disabled` + `LastError=relay_component_missing` + 明确文案与托盘提示 |
| 8 | **P1** | 配置 JSON 损坏时静默回落默认值，token 丢失且原文件被覆盖 | 用户 Shortcut 突然 401，无法恢复 | 先备份 `config.corrupt-<时间>.json`，再生成新配置，并在设置/日志中说明备份位置 |
| 9 | **P1** | 配置写入非原子（直接覆盖） | 断电/崩溃可能留下半截文件 | 临时文件 + `File.Replace`（失败退回 rename）+ `WriteThrough` |
| 10 | **P1** | 自启动注册表项指向旧路径（移动/重命名安装目录后） | "开机自启"静默失效 | 启动时检测并修正（只修正失效项，不劫持另一份存活安装） |
| 11 | **P1** | LAN 地址选择按网卡名称排序，等于"第一个碰到的" | 多网卡/VPN/扩展坞机器选错地址 | 有默认网关优先 → 以太网/Wi-Fi 优先 → 名称；另加 `LanIpOverride` 手动指定 |
| 12 | **P1** | 单个网卡枚举异常会中断整个发现过程（构造函数内） | 某些 VPN 客户端存在时启动崩溃 | 逐网卡 try/catch，整体失败视为"无 LAN" |
| 13 | **P1** | 健康检查把端口上任何 200 都当成"我们的 relay" | 端口冲突被误判为"已在运行" | 校验响应体 `service=clipbridge-relay` / `status=ok` |
| 14 | **P1** | 非法 `DeviceId`（空格、中文、超长）→ relay 返回 422，每次上传都失败 | 手改配置后同步全挂 | 按 relay 契约 `^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$` 规范化 |
| 15 | **P1** | 打包 relay 的默认 DB/日志路径在安装目录内（`_internal`） | 安装到 Program Files 时不可写 | 打包模式下默认落到 `%LOCALAPPDATA%\ClipBridge\`；相对路径也解析到用户目录 |
| 16 | **P1** | relay 端口被占用时 uvicorn 只打一行日志并退出，客户端等 20 秒超时 | 排障困难 | 启动前 `bind` 预检，输出明确错误并返回专用退出码 3 |
| 17 | **P2** | 新装默认 mDNS 名固定 `clipbridge`，同一局域网两用户冲突 | `.local` 名称冲突（实验功能） | 新装使用 `clipbridge-<机器名>`；已有配置保持不变 |
| 18 | **P2** | `CLIPBRIDGE_*` 环境变量在首次配置流程中可能被绕过 | 隔离实例/测试、进阶用户覆盖失效 | `ConfigBootstrap` 读取后再应用环境覆盖，且**永不写回**配置文件 |

### 不存在的问题（已确认）

- 没有硬编码开发者绝对路径（如 `C:\Users\<某开发者>`）；测试数据与注释中也不保留真实用户名。
- 代码不依赖当前工作目录：资源按 `AppContext.BaseDirectory` 定位，数据按用户目录。
- 不需要管理员权限：HKCU Run、用户目录、无服务、无驱动、无防火墙改动。
- 不依赖开发者机器上的 Python/.NET：产物为 self-contained + PyInstaller。
- 端口、token、设备 ID 均可配置；没有写死的设备身份。

---

## 3. 设计

### 3.1 目录布局

| 内容 | 默认位置 | 说明 |
|---|---|---|
| 配置 | `%APPDATA%\ClipBridge\config.json` | 小、需要漫游；含 token |
| 数据 | `%LOCALAPPDATA%\ClipBridge\` | 机器本地：`clipbridge.db`、`logs\` |
| 主题 | `%APPDATA%\ClipBridge\appearance.json` | 与配置分离 |
| 程序 | 任意目录（含空格/中文） | 只读即可运行 |

覆盖方式（全部可选）：

```
CLIPBRIDGE_HOME=<dir>          → <dir>\config + <dir>\data
CLIPBRIDGE_CONFIG_DIR=<dir>    → 只改配置目录
CLIPBRIDGE_DATA_DIR=<dir>      → 只改数据目录
```

相对路径一律拒绝（避免隐式依赖当前目录）。这些覆盖用于测试隔离、便携使用，
也可让支持人员在不触碰真实用户数据的前提下复现问题。

### 3.2 配置生命周期

```
无文件            → 生成 token/DeviceId/主机名 → 原子写入 → 弹出连接设置
文件存在且有效     → 原样使用（不重写、不轮换 token）
文件存在但无 token → 保留其它字段，仅补 token
文件损坏          → 备份原文件 → 生成新配置 → 告知备份路径
文件不可读        → 拒绝启动并说明（绝不覆盖看不见的文件）
```

配置带 `ConfigVersion`（当前 2）。缺字段由记录类型默认值补齐，因此 1.x 配置
（M2/M3/M4）无需迁移即可读取；字段名未变。

### 3.3 网络与端口

- 启动顺序：探测配置端口 → 可绑定？直接用；被**本产品**的健康 relay 占用？复用；
  被其它程序占用？取下一个空闲端口 → 写配置 → 提示。
- 运行中冲突（竞态）：`RelayManager` 发现 `port_in_use` 时询问宿主一次，
  宿主选择并持久化新端口，同时更新健康检查客户端与 LAN URL。
- 不静默扩大暴露面：仍然只监听 `0.0.0.0`（局域网），不做端口转发，
  不因冲突而关闭认证或改为公网可达。
- LAN 地址按"默认网关 → 接口类型 → 名称/地址"排序；`LanIpOverride` 存在且有效时优先，
  失效则自动回到自动选择。

### 3.4 relay 进程

保持不变（原设计可靠）：Job Object kill-on-close、有界重启预算、退避、
耗尽后按 30s→60s→120s→300s 自愈、优雅关停（admin 端点 → 等待 → 强杀）。
新增：端口回退、健康响应校验、缺失组件的明确状态、打包模式下的可写数据路径。

### 3.5 首次设置 UI

`Connection setup` 窗口只读展示当前 Address 与 Token（可显示/复制），并给出两步说明。
首次自动配置后自动弹出；之后可从托盘菜单或 Settings 打开。
不会因为打开窗口而更改任何值，因此不会让已配置好的 Shortcut 失效。

### 3.6 代理行为（保持原状，待决策）

**结论：本轮没有改动代理相关代码。** relay 客户端仍使用 .NET 默认的代理解析。

观察到的事实（仅记录，未修复）：

- 在注入了 `HTTP_PROXY=http://127.0.0.1:10250` 且 `NO_PROXY` 为空的 shell 中启动的实例，
  `/api/v1/health` 可能仍然成功，但轮询与上传稳定返回 404，日志为
  `poll_failed error=relay_404 path=/api/v1/clips?limit=20`、
  `clip_upload_failed error=relay_404 path=/api/v1/clips` —— 请求被代理接走，没有到达本机 relay。
- 用户真实会话中（同一台机器、代理常开）的 `client.log`：`relay_404` 计数 **0**，
  且存在正常 `clip_uploaded` 记录。说明日常环境的代理并未拦截本机回环流量，
  上述现象只出现在"环境变量强制走代理且未排除 loopback"的场景。

如果将来希望改变，可选方案（需要你确认后再做）：

1. 启动器/快捷方式里设置 `NO_PROXY=127.0.0.1,localhost`（不改产品代码）；
2. 产品内对回环/私有地址强制直连（`UseProxy=false`），代价是外部 relay 走代理的场景失效；
3. 保持现状，只在文档中提示。

对验证的影响：在上述 shell 中运行 `verify-portability.ps1` 时，
两项依赖客户端 HTTP 的检查（`Sync=Active`、剪贴板→relay）会被标记为 **SKIPPED**
并说明原因；其余检查照常执行。

---

## 4. 验证

### 4.1 自动化测试

```powershell
# Windows 客户端（273 个）
dotnet test apps\windows\tests\ClipBridge.Core.Tests

# Relay（112 个）
cd services\relay
.\.venv\Scripts\python -m pytest
```

新增覆盖（摘要）：

- `AppPathLayoutTests`：不同用户名、空格/中文路径、相对路径拒绝、空特殊目录、override
- `ConfigStoreTests`：缺失/旧版/损坏/不可写、原子写、备份幂等、URL 与端口规范化
- `ConfigBootstrapTests`：首次自动配置、重启 token 不变、有效配置字节不变、
  损坏恢复、无 token 保留设置、环境覆盖不落盘、旧 `.env` 只在开发布局导入
- `TokenGeneratorTests`：43 字符 URL-safe、200 次无重复、弱熵拒绝、DeviceId 契约
- `LanRankingTests`：网关/接口类型排序、`PreferIp`、`IsBindable`、端口回退策略
- `RelayManagerPortFallbackTests`：占用→回退、回退端口也占用→明确失败、健康 relay 复用
- `StartupStaleTests`：路径失效检测、不劫持另一份安装、修复幂等
- `RelayHealthPayloadTests`：只有 ClipBridge 的响应算健康
- relay 侧 `test_config.py` / `test_run_portability.py`：打包默认目录、相对路径解析、端口占用退出码

### 4.2 打包产物验证（本机真实进程，不触碰真实用户配置）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-daily.ps1
powershell -ExecutionPolicy Bypass -File scripts\verify-portability.ps1
```

脚本用 `CLIPBRIDGE_HOME` + 独立 `CLIPBRIDGE_INSTANCE_ID` 模拟不同用户，覆盖：

1. 全新配置（无 config）→ 自动生成 token/设备 ID → relay 健康 → `/api/v1/clips` 往返 → 错误 token 401
2. 含空格与中文的配置路径 → 正常启动、数据库落在该路径
3. 配置端口被占用 → 自动换端口并持久化，`RelayUrl` 同步更新
4. 重复启动 → 第二个进程 100ms 内退出，端口只有一个监听者
5. 配置损坏 → 备份保留旧 token，新配置可用
6. 缺少 relay 组件 → `--doctor` 与状态都明确指出
7. 剪贴板 → relay 上传链路（自动生成的 token 下）

同时修正了既有 `verify-daily.ps1` 的三处**测试环境**缺陷（与产品行为无关）：

- `Start-Process -RedirectStandardOutput` 在环境同时含 `HTTPS_PROXY` / `https_proxy` 时抛异常
  → 改用 `System.Diagnostics.Process`；
- 向 relay 发送中文字符串体时被 Windows PowerShell 按 Latin-1 编码 → 改为 UTF-8 字节 /
  `StringContent`；
- 剪贴板子进程用控制台编码回传文本会丢失 emoji → 改为 Base64 传输。
- 该脚本现在用独立的 `CLIPBRIDGE_HOME` 与 `CLIPBRIDGE_DEVICE_ID`，不再与正在运行的真实实例
  共用数据库；若检测到系统代理，仅在该次测试进程中把 loopback 加入 `NO_PROXY`
  （产品本身的代理行为未改动）。

最终结果：`verify-daily.ps1` 28/28 通过（含双向剪贴板、精确 Unicode）；
`verify-portability.ps1` 在"代理原样"环境下 31 项通过 + 2 项 SKIP，
在测试进程排除 loopback 代理后 31/31 全部通过。

### 4.3 尚未完成的验证

- **真实第二台 Windows 电脑 / 干净虚拟机**：未执行（无虚拟机控制权）。第 5 节给出可复现步骤。
- **真实 iPhone 真机**：未执行。协议未改，但仍需用户按 `docs/DEVICE_TEST.md` 回归一次。
- **macOS/Linux**：客户端仅 Windows；relay 可跨平台运行但未在本里程碑验证。
- **代码签名 / SmartScreen**：未签名，首次运行会有 SmartScreen 提示（已知限制）。

---

## 5. 干净 Windows 环境人工验收

在一台**没有装过 ClipBridge**、使用不同用户名、最好不同安装路径的电脑上：

1. 把 `artifacts\ClipBridge\` 整个目录复制到例如 `D:\Apps\ClipBridge Test\`（可含空格）。
2. 双击 `ClipBridge.exe`。预期：出现主窗口 + "Connect your iPhone" 窗口；
   地址形如 `http://192.168.x.x:8787`，token 为 43 个字符；**没有任何错误弹窗**。
3. 检查 `%APPDATA%\ClipBridge\config.json` 存在且 `DeviceToken` 非空，
   `%LOCALAPPDATA%\ClipBridge\clipbridge.db` 存在。
4. 在 iPhone（同一 Wi-Fi）上把地址与 token 填入两个 Shortcut：
   - iPhone 复制文本 → `Send to ClipBridge` → Windows `Ctrl+V` 得到同样文本
   - Windows 复制文本 → `Get from ClipBridge` → iPhone 剪贴板得到同样文本
5. 关闭窗口 → 托盘仍存在；右键 → Exit → 任务管理器中 `ClipBridge.exe` 与
   `ClipBridgeRelay.exe` 均消失。
6. 重新双击 → 只出现一个窗口，不重复启动 relay。
7. 把整个目录改名/移动到另一个路径 → 再次运行正常；若之前开过"开机自启"，
   重启后仍能自启（自启动项已被修正）。
8. 手动占用 8787（例如另一个程序）→ 启动后应自动换端口并提示；Shortcut 改成新端口后仍可同步。
9. `ClipBridge.exe --doctor`：确认 `config_state=Loaded`、`relay_exe_found=true`、
   `port_bindable=true`、`lan_url` 合理、`startup_stale=false`。
10. 防火墙：第一次询问时选"专用网络"，确认 iPhone 能连；选"取消"后应能在
    Windows 防火墙设置里为 `ClipBridgeRelay.exe` 放行（程序不会自动改规则）。

---

## 6. 保留的平台限制

- 仅 Windows 客户端；iPhone 侧依赖 Apple Shortcuts（无独立 App）。
- 局域网明文 HTTP + 共享 Bearer token（可信私有网络假设；详见 SECURITY.md）。
- 无安装器、自动更新、代码签名；分发为解压即用目录。
- mDNS（`*.local`）仍为实验特性，不作为首选地址。
- 单实例为"每用户每实例 ID"，同一台机器的不同 Windows 账户各自独立运行。
- 不接管防火墙/杀毒；首次入站连接可能被系统拦截，需用户放行。
