# ClipBridge 日常使用指南

**双击 `ClipBridge.exe` 即可。** 无需 PowerShell、Python 或 .NET 安装，也不需要管理员权限。
主窗口打开后，ClipBridge 会驻留在系统托盘。

## 首次使用（新电脑 / 新用户）

第一次启动时，ClipBridge 会在这台电脑上自动创建自己的配置和访问令牌，
并弹出一个 **"Connect your iPhone"** 窗口，里面只有两样东西：

1. **Address** —— 本机当前的局域网地址，例如 `http://192.168.1.14:8787`
2. **Access token** —— 这个安装专属的随机令牌（可点 Show 显示、点 Copy 复制）

把这两个值分别填进 iPhone 上两个 Shortcut 的对应字段即可：

| Shortcut | 用途 | 需要填 |
| --- | --- | --- |
| `Send to ClipBridge` | iPhone → Windows | Address、Token |
| `Get from ClipBridge` | Windows → iPhone | Address、Token |

之后随时可以重新打开这个窗口：**托盘图标 → Connection setup…**，
或 Settings 页的 "Authentication: Configured · Connection setup"。

> 不需要再手工编辑 `config.json`。配置由程序生成；已有用户的 token 不会被改动。
> 令牌等同于密码：只填进你自己的 Shortcut，不要发到聊天群里。

## Home

- Connected：本机中继可用，并且有局域网地址；不是 iPhone 实时在线检测。
- Sync is active / Pause sync：同步启用；可暂停，再点 Resume sync 恢复。
- iPhone connection / Copy address：复制当前 PC 的局域网 IP 地址。
- Recent activity：本次运行最近两次传输的方向、大小和时间，不显示正文。

日常流程：

1. Windows `Ctrl+C` → iPhone 运行 `Get from ClipBridge`。
2. iPhone 复制 → 运行 `Send to ClipBridge` → Windows `Ctrl+V`。

iPad 继续使用 Apple Universal Clipboard；没有单独的 iPad 页面或流程。也可以使用与 iPhone 完全相同的快捷指令（设备 ID 改为 `ipad`，见 DEVICE_TEST.md）。

## iPhone 地址

使用 Home 显示的 `http://<当前-PC-LAN-IP>:<端口>`（端口回退后以 Connection setup 显示为准）。
两个 Shortcut 的 API、token 和流程保持不变。

地址变化时（DHCP 续租、切换 Wi-Fi、插拔网线、VPN），Home 会更新并提醒更新 Shortcuts。

`clipbridge.local` 属于实验功能：在部分 iPhone 上无法解析，**不作为首选地址**，
请使用 IP 地址。多网卡机器如果自动选错网卡，可在配置里指定：

```json
{ "LanIpOverride": "192.168.1.14" }
```

（该地址不存在时会被忽略并回到自动选择，不会让 Home 变成空白。）

### 端口 8787 被其他程序占用

ClipBridge 会在启动时检测端口：

- 如果占用者是**已经运行的 ClipBridge 中继** → 直接复用，不重复启动。
- 如果是**其他程序** → 自动改用下一个空闲端口，写入配置，并在托盘提示新地址。
  这时需要把两个 Shortcut 里的端口一起更新（Connection setup 会显示新地址）。
- 如果附近没有可用端口 → 状态显示 Relay failed: port_in_use，日志里有明确原因。

## History

显示中继保留的最近最多 50 条记录：来源、时间、大小和简短预览。点击一条才显示全文。
Markdown 按纯文本显示，长行在阅读区自动换行；Copy text 复制原始文字和换行。
复制历史或地址不会上传新的同步记录。离开 History 或关闭窗口会清空页面中的正文，
数据库中的历史仍按原来的规则保留。

## Settings

- Start with Windows：当前用户登录后自动运行，默认关闭；登录启动只显示托盘。
  安装目录被移动/改名后，下次启动会自动修正这个注册表项。
- Appearance：System 跟随 Windows；也可选择 Light 或 Dark。
- Clipboard sync：Pause / Resume 与 Check now 使用现有功能；没有离线队列。
- iPhone connection：查看和复制当前地址。
- Authentication: Configured · Connection setup：查看地址与令牌。
- Open logs / Data folder：打开日志或数据文件夹。

主题保存在 `%APPDATA%\ClipBridge\appearance.json`，不改变原有配置文件。

## 托盘和退出

点击或双击托盘图标、右键 Open ClipBridge、或再次双击 EXE，都打开同一个主窗口。
关闭按钮或 Escape 只隐藏窗口，后台同步继续。真正退出：右键托盘 → Exit ClipBridge。
退出沿用原来的有序停机，不留下中继子进程。

Tab / Shift+Tab 切换控件，Space / Enter 激活按钮，Ctrl+1 / 2 / 3 切换页面。

## 排障

先运行一次诊断（只读，不改任何配置）：

```powershell
ClipBridge.exe --doctor
```

输出包含：配置/数据目录、配置状态、设备 ID、relay 路径是否存在、端口是否可绑定、
当前 LAN 地址、自启动项是否失效。**不包含令牌本身**（只有 8 位指纹）。

| 状态 | 处理 |
| --- | --- |
| Starting… | 等待中继完成启动（通常 < 5 秒） |
| Relay restarting… | 等待原有自动恢复流程 |
| Relay component missing | 安装包不完整：`relay\ClipBridgeRelay.exe` 不在 `ClipBridge.exe` 旁边。重新解压/安装，不要只复制 exe |
| Offline | 先等 1–5 分钟（自动重试 + 自愈）；Settings 出现 Restart relay 可手动重试；仍失败则 Open logs |
| Relay failed: port_in_use | 见上文「端口被占用」；日志中同时有尝试过的端口 |
| History unavailable | 点击 Retry |
| Clipboard is busy | 稍后再次复制 |
| Shortcut 报 401 | Shortcut 里的 token 与 Connection setup 显示的不一致；重新复制一次 |
| iPhone 打不开地址 | 手机与 PC 是否同一 Wi-Fi；Windows 防火墙是否允许 `ClipBridgeRelay.exe`（仅专用网络）；路由器是否开了 AP 隔离 |
| 托盘图标未见 | 查看 Windows 托盘折叠区，或再次双击 EXE |
| 配置损坏 | 程序会自动把原文件备份成 `config.corrupt-<时间>.json` 并生成新配置；旧 token 仍可从备份里找回 |

防火墙：Windows 第一次询问时选择「专用网络」。ClipBridge **不会**自动修改防火墙规则，
也不会关闭 Defender / Kaspersky，更不会在路由器上开端口。

## 文件位置

| 内容 | 位置 |
| --- | --- |
| 日志 | `%LOCALAPPDATA%\ClipBridge\logs\` |
| 历史数据库 | `%LOCALAPPDATA%\ClipBridge\clipbridge.db` |
| 配置（含令牌） | `%APPDATA%\ClipBridge\config.json` |
| 主题 | `%APPDATA%\ClipBridge\appearance.json` |

需要把配置和数据放到别处（例如 U 盘或测试环境），设置环境变量 `CLIPBRIDGE_HOME`，
配置会写到 `<CLIPBRIDGE_HOME>\config`，数据写到 `<CLIPBRIDGE_HOME>\data`。
不设置时一律使用上面的标准 Windows 用户目录，与安装位置、用户名无关。

## 兼容性

- 旧版本（M2/M3/M4）的 `config.json` 可直接读取，token、端口、设备 ID 全部保留。
- 升级不需要迁移脚本；只有配置**无法解析**时才会备份并重新生成。
- iPhone 侧协议未变：`/api/v1/clips`、`/api/v1/clips/shortcut`、`/api/v1/clips/latest`。
