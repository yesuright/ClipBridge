# ClipBridge 真机验收指南 — iPhone / iPad（Apple Shortcuts）

> 目标：在同一可信 Wi-Fi/局域网内，用 iPhone/iPad 系统自带的「快捷指令（Shortcuts）」App，
> 完成 Windows ↔ iPhone 的**双向纯文本剪贴板同步**验收。本指南面向第一次接触 ClipBridge 的用户。
>
> 所有连接参数（地址、端口、Token）以 **Windows 端首次启动时弹出的 *Connect your iPhone*
> 窗口**为准——窗口显示什么就填什么，不需要手工生成或查找任何 Token。

---

## 0. 一分钟速览

| 方向 | 你做什么 | 预期 |
| --- | --- | --- |
| Windows → iPhone | Windows 复制文本 → iPhone 点 **Get from ClipBridge** | 文本进入 iPhone 剪贴板，任意 App 可粘贴 |
| iPhone → Windows | iPhone 复制文本 → 点 **Send to ClipBridge** | 几秒内 Windows 剪贴板变为该文本，`Ctrl+V` 可粘贴 |
| iPad | 与 iPhone 完全相同，仅设备 ID 改为 `ipad` | 同上 |

---

## 1. 前置条件

- [ ] 一台 Windows PC（10/11，64 位）与一台 iPhone 或 iPad，连接**同一个可信 Wi-Fi**
      （同一路由器、同一网段；手机开热点给 PC 不算同一网段）。
- [ ] Windows 网络配置文件为**专用（Private）**：设置 → 网络和 Internet → Wi-Fi → 属性 →
      「专用」。公用网络配置下防火墙会拦截入站连接。
- [ ] 已获得 ClipBridge 程序目录（内含 `ClipBridge.exe` 与 `relay\` 子目录）：
      若 GitHub Releases 已提供发布包，下载并**完整解压**到任意目录（路径可含空格，无需安装）；
      若尚未发布，按 [README](../README.md) 的「Building from source」自行构建，产物结构相同。
- [ ] 首次入站连接时 Windows 防火墙可能弹窗：选择**允许（仅专用网络）**。
      ClipBridge 永远不会替你修改防火墙、VPN 或代理设置。

## 2. 启动并获取连接参数（Windows，一次性）

1. 双击 **`ClipBridge.exe`**——它会自动拉起 relay 子进程与托盘应用。
2. 首次启动自动完成全部配置：生成 256-bit 随机访问 Token（43 字符）与设备 ID，
   并弹出 **Connect your iPhone** 窗口。记下窗口里的三项：
   - **地址**：`http://<PC 局域网 IPv4>:<端口>`，例如 `http://192.168.1.20:8787`
     （结尾不加 `/`；PC 的 IP 变化后此地址需要更新，Token 不变）
   - **Token**：43 字符字符串本身（Show / Copy 按钮；拼请求头时再加 `Bearer ` 前缀；
     不要发到聊天、截图或粘贴进任何文件）
   - **设备 ID**：自定，如 `iphone`。规则：字母或数字开头，仅字母/数字/`-`/`_`，1–64 字符，
     无需注册——**Send 与 Get 两个快捷指令必须填同一个值**
3. 随时重开窗口：托盘图标 → **Connection setup…**；Home 页 → *Copy address* 可直接复制地址。
4. 端口说明：默认 **8787**；被其他程序占用时会自动换到下一个空闲端口并持久化、托盘提示——
   此时两个快捷指令里的端口要一起更新（Token 不受影响）。
5. 程序目录整体移动不影响任何参数：配置与 Token 存于 `%APPDATA%\ClipBridge`，与安装位置无关。

> mDNS 说明：客户端可在局域网内应答 `clipbridge.local`，但该特性为**实验性**
> （部分 iPhone 无法解析，见 README「Known issues」#3）。请以窗口显示的 **IP 地址**为准。

## 3. iPhone 快捷指令 1 — Send to ClipBridge（iPhone → Windows）

打开「快捷指令」App → ＋ 新建，依次添加动作：

1. **文本 (Text)** — 内容：`<Relay地址>`（如 `http://192.168.1.20:8787`，结尾无 `/`）
2. **文本 (Text)** — 内容：`<Token>`（只填 43 字符本身，不要带 `Bearer `）
3. **文本 (Text)** — 内容：`<设备ID>`（如 `iphone`）
4. **获取剪贴板 (Get Clipboard)**
5. **如果 (If)**：剪贴板有任何值 → **获取 URL 内容 (Get Contents of URL)**：
   - 方法 **POST**；URL：`[Text1]/api/v1/clips/shortcut`（在 URL 字段插入第 1 步变量）
   - 请求头：`Authorization` = `Bearer ` + 第 2 步变量；`Content-Type` = `application/json`
   - 请求体：选 **JSON**，用**字典编辑器**添加两项：
     `device_id` ← 第 3 步变量；`content` ← **剪贴板**变量
     （**不要**手写 JSON 字符串——引号/换行/emoji 会被转义弄坏）
   - **否则** → 通知：「剪贴板为空」
6. （可选）**从输入获取词典** → **获取词典值** `size_bytes` → 通知 `Sent <size> bytes`
7. 重命名为 `Send to ClipBridge`，添加到主屏幕。

成功时服务端返回 `201`，并自动生成 `clip_id` / 时间戳 / 内容哈希——快捷指令不需要自己计算。

## 4. iPhone 快捷指令 2 — Get from ClipBridge（Windows → iPhone）

1. **文本 (Text)** — `<Relay地址>`（同上）
2. **文本 (Text)** — `<Token>`
3. **文本 (Text)** — `<设备ID>`（**必须与快捷指令 1 第 3 步完全一致**）
4. **URL** — `[Text1]/api/v1/clips?limit=10&exclude_device_id=[Text3]`
   （服务端把 `limit` 钳制在 1–200，默认 50）
5. **获取 URL 内容 (Get Contents of URL)** — GET，请求头只加：
   `Authorization` = `Bearer ` + 第 2 步变量
6. **从输入获取词典** → **获取词典值** `clips`（响应形如 `{"count":N,"clips":[…]}`）
7. **如果** clips 有任何值 → **从列表获取项**（第 1 项）→ **获取词典值** `content` →
   **拷贝到剪贴板**；**否则** → 通知：「暂无来自其他设备的新内容」
8. （可选）用返回的 `size_bytes` / `device_id` 做通知
9. 重命名为 `Get from ClipBridge`，添加到主屏幕（或操作按钮）。

> 端点说明：`exclude_device_id` 的语义是「返回发送者 ID ≠ 该值的最新内容」，
> 用来避免取回自己刚发出的文本——因此它必须与本快捷指令第 3 步的设备 ID 一致。
> 另有一个极简 4 动作变体 `GET /api/v1/clips/latest?exclude_device_id=<设备ID>`，
> 但在没有其他设备内容时它会返回 `404 not_found`（快捷指令显示为获取失败，属正常空态）；
> 推荐使用上面的列表版。

图文版步骤：[shortcuts/SEND_TO_CLIPBRIDGE.md](../shortcuts/SEND_TO_CLIPBRIDGE.md) 与
[shortcuts/GET_FROM_CLIPBRIDGE.md](../shortcuts/GET_FROM_CLIPBRIDGE.md)。

## 5. iPad

与 iPhone 完全相同，只改一处：两个快捷指令里的设备 ID 用 `ipad`
（Get 的 `exclude_device_id=` 同步改为 `ipad`）。

## 6. 验收用例

### T0 连通性预检（先做这个）

iPhone Safari 打开 `http://<PC IP>:<端口>/api/v1/health` → 应显示 `{"status":"ok",...}`。
此端点无需 Token。打不开按 §7 排查（同一 Wi-Fi → 防火墙 → AP 隔离）。

### T1 Windows → iPhone

1. Windows 复制一段文本（例如 `Hello from Windows`）
2. iPhone 点 **Get from ClipBridge** → 打开备忘录粘贴
3. 预期：原文完整出现（换行/表情/Markdown/代码，一个字符不多不少）
4. Windows 再复制一段「中文 + emoji + 多行」，重复 Get → 粘贴应为**新**内容

### T2 iPhone → Windows

1. iPhone 任意 App 复制文本 → 点 **Send to ClipBridge**（看到成功反馈）
2. 几秒内（默认 2 秒轮询）Windows `Ctrl+V` 得到原文，所有换行原样保留

### T4 防循环验证

1. 完成 T2 后等约 10 秒
2. Windows 剪贴板内容**保持不变**——客户端没有把刚应用的内容再上传回去
   （四层防循环：30 秒同哈希忽略、900 秒本设备排除、15 秒服务端去重、clip_id 幂等）

### T5 长文本

准备 ≥100 KB 的文本（单条上限 2 MiB）：

1. Windows 复制 → iPhone 点 Get → 粘贴完整
2. iPhone 复制 → 点 Send → Windows 粘贴后与原文逐字节比对
   （PowerShell：把原文与取回文本分别放入 `$text` / `$got`，`$text.Equals($got)` 应为 True）

### T6 断网恢复

1. iPhone 关闭 Wi-Fi → 点 Get / Send 失败，属预期
2. 重新打开 Wi-Fi → 同步自动恢复（客户端自愈，通常 1–5 分钟内；也可在 Windows 托盘 →
   *Restart relay* 立即重试）→ 重复 T1 或 T2 应成功

**非故障判定**：Send 返回 **409** = 15 秒内同设备重发相同内容（去重窗口），视为已发出；
Get 的 `latest` 变体返回 **404 not_found** = 正常空态；**413** = 超过 2 MiB，请拆分发送。

## 7. Troubleshooting（均为只读 + 手动步骤；ClipBridge 永远不会替你改系统安全设置）

| 现象 | 排查 |
| --- | --- |
| T0 / health **打不开或转圈** | ① iPhone 与 PC 是否同一 Wi-Fi；② PC IP 是否变化（重开 *Connection setup…* 查看当前地址）；③ 手机蜂窝数据与 Wi-Fi 状态；④ 路由器 AP 隔离 |
| **connection refused** | 程序没在运行：双击 `ClipBridge.exe`；看托盘状态；运行 `ClipBridge.exe --doctor`（只读诊断） |
| **timeout**（手机能上其他网，连不到 PC） | Windows 防火墙入站拦截：首次弹窗选允许（仅专用网络）；或手动放行 `ClipBridgeRelay.exe`（仅专用网络）；确认网络配置文件为**专用** |
| **401 Unauthorized** | Token 不一致，或复制时带了空格/换行：从 *Connection setup…* 重新复制 |
| **404 not_found**（`latest` 变体） | 正常空态：没有"其他设备"的新内容，先发一条 |
| **409**（Send 时） | 15 秒内同设备重发相同内容（防循环去重窗口），视为已发出，不是故障 |
| **413** | 单条超过 2 MiB：拆分发送 |
| 错拿 **VPN/虚拟网卡 IP** | 窗口地址由客户端自动选择物理网卡；多网卡环境可在 Settings 配置 `LanIpOverride`，或用 `ClipBridge.exe --doctor` 查看候选 |
| Windows 网络配置文件是**公用** | 设置 → 网络和 Internet → 属性 → 切为专用 |
| 同步一直 **Offline** | 等 1–5 分钟自愈；或托盘 → *Restart relay*；仍失败看日志 |
| PC 从设置了 `HTTP_PROXY` 的 shell 启动后同步失败（`relay_404`） | 见 README「Known issues」#1：给该 shell 加 `NO_PROXY=localhost,127.0.0.1`，或改从资源管理器/开始菜单启动；ClipBridge 不修改任何代理设置 |

### 日志（只含 id / 大小 / 哈希前缀，永不包含正文或 Token）

- 客户端 + Relay：`%LOCALAPPDATA%\ClipBridge\logs\`
- 只读检查端口监听：`Get-NetTCPConnection -LocalPort 8787 -State Listen`

## 8. 常见变化场景

- **移动安装目录**：程序目录可整体移动。Token 与配置存于 `%APPDATA%\ClipBridge`，
  与安装位置无关——IP/端口不变的前提下，快捷指令无需改动。
- **PC 的 IP 变了（DHCP 续租/换网）**：重新打开 *Connection setup…*，把两个快捷指令里的
  地址更新为当前显示值；**Token 永远不变**。地址变化时托盘会提示。
- **端口被占用**：自动换到下一空闲端口并持久化 + 托盘提示 → 两个快捷指令的端口同步更新。
- **并行运行多份实例**（高级）：用 `CLIPBRIDGE_HOME` / `CLIPBRIDGE_INSTANCE_ID` /
  `CLIPBRIDGE_RELAY_PORT` 等环境变量隔离，见 [DEVELOPMENT.md](DEVELOPMENT.md) 的环境变量表。

## 9. 开发者等价自检（无需 iPhone）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\verify-daily.ps1 -Artifact artifacts\ClipBridge
# 已覆盖：打包产物真实启动、relay 0.0.0.0 监听、单实例、剪贴板上传/接收、崩溃恢复、干净退出
powershell -ExecutionPolicy Bypass -File tests\e2e\run-end-to-end.ps1
# 已覆盖：iphone/ipad 简化端点 POST、exclude_device_id GET、Unicode、长文本、循环防御
```

真实设备验收以本指南的人工用例为准；自动化检查只是开发侧的等价物。

---

相关文档：[README](../README.md) · [安全模型](SECURITY.md) · [日常使用](DAILY_USE.md) ·
[已知问题](../README.md#known-issues)
