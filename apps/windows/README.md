# ClipBridge Windows Client

C# / .NET 8 tray app：监听剪贴板 → 上传到 relay；轮询 relay → 写入本地剪贴板。
Milestone 3 起还负责：relay 子进程生命周期（Job Object + 有界重启）、单实例、
局域网地址监控、mDNS 应答器、开机自启（HKCU Run）、状态模型。
Milestone 4 加入原生 Home / History / Settings、System / Light / Dark 主题、原创图标，
以及不产生新同步记录的地址和历史复制。完整 UI 设计见根目录 `docs/UI.md`。

```powershell
dotnet build ClipBridge.sln
dotnet run --project src\ClipBridge.Win
```

配置：`CLIPBRIDGE_DEVICE_TOKEN`（必须）等环境变量，或 `%APPDATA%\ClipBridge\config.json`。
详见根目录 `docs/DEVELOPMENT.md` 与 `docs/DAILY_USE.md`。

## 项目结构

```text
src/
  ClipBridge.Core/   协议模型、SHA-256、dedup/tracker 缓存、RelayClient、SyncEngine、
                     配置/迁移、状态模型、LAN 地址选择、mDNS 编解码与应答器、
                     RelayManager、单实例、开机自启（全部无 UI，可单测）
  ClipBridge.Win/    原生主窗口及托盘：隐藏窗口 + WM_CLIPBOARDUPDATE + STA 剪贴板访问
                     + 生命周期编排 + UI/ 展示层（发布产物名为 ClipBridge.exe）
tests/
  ClipBridge.Core.Tests/  xunit 单测（hash / dedup / 引擎 / API client / config / 日志脱敏 /
                           M3: 生命周期/单实例/状态模型/网络选择/mDNS/轮转/迁移/自启；
                           M4: 状态展示/主题/安全复制/窗口隐藏/历史隐私/DPI）
```

## 手动剪贴板集成验证（OS 剪贴板难以自动化）：

1. `Set-Clipboard "test"` → 客户端日志出现 `clip_uploaded`，relay 收到 201；
2. 用 API 以另一 device_id POST → 数秒内系统剪贴板更新，日志出现 `clip_applied`；
3. 确认没有针对同一内容的重复 `clip_uploaded`（loop 防护生效）；
4. 复制图片 → 无任何上传日志（安全忽略，不崩溃）。
