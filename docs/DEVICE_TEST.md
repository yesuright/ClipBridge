# ClipBridge 真机手测指南 — iPhone / iPad Shortcuts (Milestone 2)

> 目标：同一可信 LAN/Wi-Fi 下，iPhone 和 iPad 用 Apple 自带 Shortcuts 完成
> **真实双向纯文本同步**。本指南让「拿着 iPhone 的人」一步步就能做完。
>
> 状态约定：本指南的步骤全部完成后，项目才能从
> `DEVICE VALIDATION: READY_FOR_DEVICE_TEST` 升级为 `iPhone/iPad PASS`。

---

## 0. 一分钟速览

| 操作 | 用户动作 | 预期 |
| --- | --- | --- |
| Windows → iPhone | 复制文本；iPhone 点 **Get from ClipBridge** | 文本进入 iPhone 剪贴板 → 任意 App 粘贴 |
| iPhone → Windows | 复制文本；点 **Send to ClipBridge** | 几秒内 Windows 剪贴板 = 该文本 → `Ctrl+V` |
| iPad | 同上，两个 Shortcut 中 `My Device ID` 改成 `ipad` | 同上 |

---

## 1. Prerequisites（前置条件）

- [ ] 一台 Windows PC（运行本仓库），一台 iPhone、一台 iPad，**连接同一个可信 Wi-Fi**
      （同一个路由器，同一网段；手机开热点给 PC 不算同一网段）
- [ ] Relay 与 Windows 客户端正在运行。**一条命令**（在 Windows 上，PowerShell）：

      powershell -ExecutionPolicy Bypass -File scripts\start-lan-dev.ps1

      该脚本会启动 relay（LAN 模式，绑定 0.0.0.0）+ Windows 客户端，
      并打印建议的 LAN URL 与安全提示。用 `Ctrl+C` 结束两者。

- [ ] Windows 网络配置文件是 **“专用/Private”**（不是“公用/Public”）。
      检查：设置 → 网络和 Internet → Wi-Fi → 属性 → “公用/专用”。
      只有专用配置下，手动添加的入站防火墙规则才会生效（见 §8）。

## 2. 找到 Relay URL（在 Windows 上执行）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\show-lan-setup.ps1
```

- 脚本会列出候选地址，例如 `http://192.168.1.123:8787`（接口名 WLAN/Ethernet）。
- 如果列出多个，选当前连着的那个网卡（设置 → 网络和 Internet 里能看到）。
- 脚本同时（只读地）检查：端口是否正在监听、监听的是不是 `127.0.0.1`、
  防火墙是否可能拦截，以及需要时打印**手动**防火墙步骤——脚本本身不做任何修改。
- 也可以手查：`ipconfig`，找一个 `192.168.x.x` / `10.x.x.x` / `172.16-31.x.x`
  的**物理网卡**地址（VPN 和虚拟网卡不要用）。

记下 Relay URL，例如：`http://192.168.1.123:8787`

## 3. 准备 Bearer Token（在 Windows 上）

1. 打开 `services\relay\.env`（记事本），找到你自己设置的 `CLIPBRIDGE_TOKEN`。
2. 如果你丢了 / 刚创建：
   ```powershell
   python -c "import secrets; print(secrets.token_hex(32))"
   ```
   然后把结果写进 `services\relay\.env` 并重启 relay。
3. 把该 token 记下来（只在下面配置 Shortcut 时输入一次）。
   **不要在聊天里发、不要截图、不要发给本指南以外的任何人。**

> start-lan-dev.ps1 / show-lan-setup.ps1 / relay 日志都**不会**打印 token。

## 4. 配置 iPhone：Send to ClipBridge

打开 **Shortcuts** App → ＋ 新建 Shortcut。依次添加：

1. **Text** — 计划文本，内容 `http://192.168.1.123:8787`
   （第 2 步拿到的 Relay URL，结尾不要 `/`）
2. **Text** — 内容 `Bearer <你的 CLIPBRIDGE_TOKEN>` 里**不要带 Bearer**，
   只放 token 本身（后面拼 Header 时再加 `Bearer `）→ 实际：
   - Text 内容 = token 字符串
3. **Text** — 内容 `iphone`
4. **Get Clipboard**（获取剪贴板）
5. **If** (Clipboard) 有值 — 否则 **Notification** “Clipboard is empty”
6. **Get Contents of URL**（获取 URL 内容）：
   - Method：**POST**
   - URL：`[Text1]/api/v1/clips/shortcut`（在 URL 字段里插入第 1 个 Text 变量）
   - Headers：
     - `Authorization` = `Bearer ` +（插入第 2 个 Text 变量）
     - `Content-Type` = `application/json`
   - 展开 **Request Body**：选 **JSON**，字典加两项：
     - `device_id` ← 插入第 3 个 Text 变量（“iphone”）
     - `content` ← 插入 **Clipboard** 变量
   - **不要**手写 JSON 字符串；用字典输入才能保证换行/引号/emoji 原样发送
7. （可选）**Get Dictionary from Input** → **Get Value for Key** `size_bytes` →
   **Notification** `Sent <size> bytes`
8. 重命名 Shortcut 为 `Send to ClipBridge`，添加到主屏幕。

## 5. 配置 iPhone：Get from ClipBridge

1. **Text** — `http://192.168.1.123:8787`（同第 4 步）
2. **Text** — token
3. **Text** — `iphone`
4. **URL** — `[Text1]/api/v1/clips/latest?exclude_device_id=[Text3]`
5. **Get Contents of URL** — GET，Headers 只加：
   `Authorization` = `Bearer ` +（Text2）
6. **Get Dictionary from Input** → **Get Value for Key** `content`
7. **Copy to Clipboard**（复制到剪贴板）
8. （可选）Notification：`Copied from <device_id>`（取返回的 `device_id` 字段）
9. 重命名 `Get from ClipBridge`，添加到主屏幕。

> 说明：没有“其他设备的内容”时本版会报 `not_found` 错误并停止——这是**正常空状态**
> （意思：“没有任何非你设备的新文本”）。想看到优雅提示，改用
> `.../api/v1/clips?limit=10&exclude_device_id=[Text3]` 并在“If (clips) 有值”
> 分支里取第 1 条的 `content`（见 `shortcuts/GET_FROM_CLIPBRIDGE.md`）。

## 6. 配置 iPad（和 iPhone 的区别只有 device ID）

按 §4、§5 在 iPad 上各建一个同款 Shortcut，**只改一处**：

| 字段 | iPhone | iPad |
| --- | --- | --- |
| Send 里的 Text3（我的设备 ID） | `iphone` | `ipad` |
| Get 里的 URL 的 `exclude_device_id=` | `iphone` | `ipad` |
| Relay URL 与 Token | 相同 | 相同 |

> device ID 可自定义（如 `tony-iphone`），但必须在**所有设备**上各自一致或唯一，
> 且只含 `[A-Za-z0-9_-]`、字母数字开头。

## 7. 测试

### T1 Windows → iPhone

1. Windows 复制一段文本（例如 `Hello from Windows`）
2. iPhone 点 **Get from ClipBridge**
3. 打开备忘录 / 任意文本框 → 粘贴
4. 预期：原文完整出现（含换行/表情/Markdown/代码，一个字符不多不少）
5. 跨设备核对：Windows 再复制一段中文 + emoji + 多行，再点一次 Get，粘贴应为新内容

### T2 iPhone → Windows

1. iPhone 任意 App 复制文本
2. 点 **Send to ClipBridge**，看到成功通知
3. 几秒内 Windows 托盘客户端自动写入剪贴板；`Ctrl+V` → 得到原文（保留所有换行）

### T3 iPad

重复 T1 / T2（iPad 的 Shortcut，`ipad` 身份），预期同上。

### T4 循环（loop）验证

1. 从 iPhone 发送一段文本后，Windows 剪贴板出现该文本
2. 等 10 秒左右，确认 Windows 托盘客户端**没有**把该文本再“发出去”
   （可在 Windows 上 `Get-Clipboard` 再次粘贴，内容应保持不变；更严格的对照见
   `tests/e2e/run-end-to-end.ps1` 的自动版本）

### T5 长 prompt 测试

iOS 备忘录里拿一段 ≥100 KB 的文本（例如你常用的 AI prompt 或一篇 Markdown）：

1. iPad 复制 → Send to ClipBridge → Windows `Ctrl+V`，逐字节一致（建议用
   `git diff` 或 PowerShell 比对哈希：`Get-FileHash` 不行——用
   `(Set-Clipboard ...); Get-Clipboard` 前后对比 `$text.Equals($got)`）
2. Windows 复制 100 KB 长文本 → iPhone 点 Get → 粘贴完整

## 8. Troubleshooting（都是只读 + 手动步骤；ClipBridge 永远不会替你改系统安全设置）

| 现象 | 排查 |
| --- | --- |
| iPhone Safari 打开 `http://<PC-IP>:8787/api/v1/health` **打不开 / 转圈** | ① iPhone 是否连的同一个 Wi-Fi；② PC 的 IP 是否变了（重启/换网可能变——重新跑 `show-lan-setup.ps1`）；③ 手机 4G/5G 关了没 |
| **connection refused** | relay 没在跑，或绑定的不是 0.0.0.0：看 relay 启动输出；用 `start-lan-dev.ps1` 启动 |
| **timeout**（能上别的网站，就是连不到 PC） | Windows 防火墙入站拦截。**手动**（管理员 PowerShell）：`New-NetFirewallRule -DisplayName "ClipBridge relay" -Direction Inbound -Protocol TCP -LocalPort 8787 -Action Allow -Profile Private`；并确认网络配置文件为 **专用**。做完后从 iPhone 重试 |
| **401 Unauthorized** | Shortcut 里的 token 与 relay `.env` 不一致；或 token 复制时带了空格/换行 |
| **404 not_found**（Get 时） | 正常空状态：没有“其他设备”的内容。先在其他设备发一条再试 |
| **409**（Send 时） | 15 秒内同一设备发过相同内容（去重窗口，防循环）——视为已发出，不是故障 |
| **413** | 超过 2 MiB：短一些再发 |
| 错拿 **VPN 网卡 IP** | `show-lan-setup.ps1` 已排除 VPN/虚拟网卡；若手工 `ipconfig` 请选物理网卡（WLAN/Ethernet）地址 |
| iPhone **不在同一 Wi-Fi** | 检查两台设备的网络名；手机热点 ≠ 同一网段 |
| Windows 网络配置文件是“**公用**” | 设置 → 网络和 Internet → 属性 → 切到“专用”，否则防火墙规则/私有网络行为会拦截 |
| PC 有 **多网卡** | 用 `show-lan-setup.ps1` 列出的候选；或临时禁用不用的网卡后在 Shortcut 里改用对应 IP |

### 日志（排查用，不泄露正文/token）

- Relay：`services\relay\data\logs\relay.log`（只有 clip_id / device / size / hash 前缀）
- 客户端：`%APPDATA%\ClipBridge\client.log`
- 只读检查端口：`Get-NetTCPConnection -LocalPort 8787 -State Listen`

---

## 9. 需要人工确认的自动化等价检查（无需 iPhone）

```powershell
powershell -ExecutionPolicy Bypass -File tests\e2e\run-end-to-end.ps1
# 已包含：iphone/ipad 简化端点 POST、exclude_device_id GET、Unicode、长文本、循环防御
powershell -ExecutionPolicy Bypass -File scripts\tests\test-lan-setup.ps1   # LAN 逻辑单测
```
