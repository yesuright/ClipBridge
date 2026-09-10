# ClipBridge 日常使用指南 (Milestone 4)

**双击 artifacts\ClipBridge\ClipBridge.exe 即可。** 无需 PowerShell、Python 或 .NET 安装。
主窗口打开后，ClipBridge 也会驻留在系统托盘。

## Home

- Connected：本机中继可用，并且有局域网地址；不是 iPhone 实时在线检测。
- Sync is active / Pause sync：同步启用；可暂停，再点 Resume sync 恢复。
- iPhone connection / Copy address：复制当前 PC 的局域网 IP 地址。
- Recent activity：本次运行最近两次传输的方向、大小和时间，不显示正文。

原有工作流保持不变：

1. Windows Ctrl+C → iPhone 运行 Get from ClipBridge。
2. iPhone 复制 → 运行 Send to ClipBridge → Windows Ctrl+V。

iPad 继续使用 Apple Universal Clipboard；没有单独的 iPad 页面或流程。

## iPhone 地址

使用 Home 显示的 http://<当前-PC-LAN-IP>:8787，例如 http://192.168.1.14:8787。
两个已有 Shortcut 的 API、token 和流程保持不变。

地址变化时，Home 会更新并提醒更新 Shortcuts。clipbridge.local 在用户真实 iPhone 上
未成功，属于实验功能；本里程碑不修复它，也不把它当作首选地址。

No local network：检查 PC 的 Wi-Fi / Ethernet，以及手机是否在同一局域网。
应用不会更改 VPN、Kaspersky、防火墙、路由器或 Windows 安全设置。

## History

显示中继保留的最近最多 50 条记录：来源、时间、大小和简短预览。点击一条才显示全文。
Markdown 按纯文本显示，长行在阅读区自动换行；Copy text 复制原始文字和换行。
复制历史或地址不会上传新的同步记录。离开 History 或关闭窗口会清空页面中的正文，
数据库中的历史仍按原来的规则保留。

## Settings

- Start with Windows：当前用户登录后自动运行，默认关闭；登录启动只显示托盘。
- Appearance：System 跟随 Windows；也可选择 Light 或 Dark。
- Clipboard sync：Pause / Resume 与 Check now 使用现有功能；没有离线队列。
- iPhone connection：查看和复制当前地址。
- Open logs / Data folder：打开日志或数据文件夹。
- Authentication: Configured：仅显示已配置，不显示 token。

主题保存在 %APPDATA%\ClipBridge\appearance.json，不改变原有配置文件。

## 托盘和退出

点击或双击托盘图标、右键 Open ClipBridge、或再次双击 EXE，都打开同一个主窗口。
关闭按钮或 Escape 只隐藏窗口，后台同步继续。真正退出：右键托盘 → Exit ClipBridge。
退出沿用原来的有序停机，不留下中继子进程。

Tab / Shift+Tab 切换控件，Space / Enter 激活按钮，Ctrl+1 / 2 / 3 切换页面。

## 排障

| 状态 | 处理 |
| --- | --- |
| Starting… | 等待中继完成启动 |
| Relay restarting… | 等待原有自动恢复流程 |
| Offline | 中继会自动重试（含 restart budget 耗尽后的自动自愈），先等 1–5 分钟；若 Settings 出现 Restart relay 可手动立即重试；仍失败则 Open logs |
| History unavailable | 点击 Retry |
| Clipboard is busy | 稍后再次复制 |
| Shortcut 报 401 | 检查已有 Shortcut 与 config.json 的 token 是否一致 |
| 托盘图标未见 | 查看 Windows 托盘折叠区，或再次双击 EXE |

首次配置仍使用 %APPDATA%\ClipBridge\config.json 的 DeviceToken；缺少配置时原有提示框
会说明。此次没有添加 token 编辑或配对向导。

## 文件位置

| 内容 | 位置 |
| --- | --- |
| 日志 | %LOCALAPPDATA%\ClipBridge\logs\ |
| 历史数据库 | %LOCALAPPDATA%\ClipBridge\clipbridge.db |
| 配置 | %APPDATA%\ClipBridge\config.json |
| 主题 | %APPDATA%\ClipBridge\appearance.json |

M4 iPhone 真机回归：READY_FOR_USER_RETEST。
