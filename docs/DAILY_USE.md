# ClipBridge 日常使用指南 (Milestone 3)

> 写给普通用户：你不需要 Python、PowerShell、Relay、Docker 或任何开发知识。
> 你只需要做一件事：**双击 ClipBridge.exe**。

---

## 1. 启动

1. 打开文件夹 `artifacts\ClipBridge\`，双击 **ClipBridge.exe**。
2. 几秒钟后，屏幕右下角（系统托盘）会出现 ClipBridge 图标。
3. 看到提示气泡 “ClipBridge is running.” 即为正常。
4. 没有黑窗口、没有终端、不需要保持任何窗口打开。

> 第一次使用时，程序需要知道你的 token（配置在
> `%APPDATA%\ClipBridge\config.json` 的 `DeviceToken` 字段，与原版相同）。
> 没配置时会弹窗说明，你去设置 `DeviceToken`（或设置环境变量
> `CLIPBRIDGE_DEVICE_TOKEN`）后重启即可。

## 2. 怎么知道它在工作

- 右键托盘图标，菜单里会显示：
  - **Sync: Active** —— 同步正常
  - **Relay: Running** —— 中继服务正常（iPhone 能连上）
  - **LAN: http://192.168.x.x:8787** —— iPhone Shortcuts 应该填写的地址
- 复制一段文字，气泡提示 “Copied to relay (N bytes)”。
- 手机上发送的文本会在 1–3 秒内写进 Windows 剪贴板（直接 Ctrl+V）。

## 3. iPhone 配合使用（和以前一样）

- **Send to ClipBridge**：发文本给 Windows（地址填菜单里的 LAN URL）。
- **Get from ClipBridge**：把 Windows 上的最新文本复制到手机。
- 地址变了怎么办：见第 6 节。

## 4. 开机自动启动（可选，默认关闭）

托盘菜单勾选 **“Start ClipBridge when I sign in”** 即可。
- 只对当前用户生效，不需要管理员权限，随时可取消勾选。
- 也可以命令行开关：
  - `ClipBridge.exe --enable-autostart` / `--disable-autostart` / `--autostart-status`
- 勾选后，Windows 登录后 ClipBridge 会自动悄悄运行。

## 5. 暂停 / 恢复

托盘菜单选 **Pause Sync**：本机复制不再上传，手机内容也不再写入本机剪贴板；
Relay 仍然在线。选 **Resume Sync** 恢复。暂停期间的历史内容不会补同步，
恢复后从下一条新内容开始。

## 6. 手机连不上 / PC 的 IP 变了

1. 先看托盘菜单的 **LAN:** 一行的地址：
   - 还是 `http://192.168.1.14:8787` 之类 → 说明本机地址没变，问题在别处（见下）。
   - 地址和 Shortcut 里写的不一样 → 在手机 Shortcuts 里把 URL 改成新地址（改一次即可）。
2. 也可以先试试稳定地址 **`http://clipbridge.local:8787`**：
   - 如果手机能打开 `http://clipbridge.local:8787/api/v1/health`，
     就把 Shortcuts 里的 URL 换成本地主机名 —— 以后 PC 换 IP 也不用改。
   - 打不开也正常（某些网络/多网卡/虚拟网卡环境下 mDNS 不稳）——
     继续用菜单里显示的 IP 地址即可；这属于“能用就用，不行回退”的增强功能。
3. 还是连不上：
   - 确认手机和 PC 连的是同一个 Wi-Fi；
   - 确认 PC 网络配置文件是 **“专用”**；
   - 确认 Windows 防火墙至少有一条 8787 入站放行规则（以前手动加过就不用管了）；
   - 检查 Kaspersky 是否拦截了 8787（以前手工加过排除就不用管了）。

ClipBridge **不会**替你做这些（不改防火墙、不改杀毒软件、不动路由器）。

## 7. 如何退出

右击托盘图标 → **Exit**。程序会依次停止：剪贴板监听 → 轮询 → 中继服务，
不会留下后台残留进程。**不要**用任务管理器强杀——强杀也不会留孤儿
（程序用 Windows Job Object 保证），但优雅退出更干净。

## 8. 常见问题

| 现象 | 处理 |
| --- | --- |
| 托盘没有图标 | 是否被系统折叠了？点托盘区 “^” 找找看；或重新双击 exe |
| 提示 Relay: Failed | 右键菜单 → **Restart Relay**；仍失败就 **Open Logs** 看日志 |
| Relay: Failed，日志说 port 8787 in use | 有别的程序占用了 8787 端口，关掉那个程序后重启 ClipBridge |
| 手机 Shortcut 报 401 | Shortcut 里的 token 和 `%APPDATA%\ClipBridge\config.json` 的 DeviceToken 不一致 |
| 手机 Shortcut 报 404 | 正常空状态：还没有“其他设备”的新内容（先让另一台设备发一条） |
| 提示 Sync: Offline | 本机网络问题，检查 Wi-Fi；恢复后自动继续同步 |

## 9. 日志和文件位置

| 内容 | 位置 |
| --- | --- |
| 客户端日志 | `%LOCALAPPDATA%\ClipBridge\logs\client.log`（自动轮转，总量有上限） |
| 中继日志 | `%LOCALAPPDATA%\ClipBridge\logs\relay.log` |
| 历史数据库 | `%LOCALAPPDATA%\ClipBridge\clipbridge.db` |
| 配置 | `%APPDATA%\ClipBridge\config.json` |

日志里永远不包含剪贴板正文，也不会记录 token。
