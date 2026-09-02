# Apple Shortcuts — SEND_TO_CLIPBRIDGE

把 iPhone / iPad 当前剪贴板文本发送到 Windows：POST 到 ClipBridge relay（简化端点），
Windows 客户端几秒内收到并自动写入系统剪贴板。

> 前置：relay 已启动且 iPhone/iPad 可访问（同一可信 Wi-Fi 的局域网
> `http://<PC-IP>:8787`）。**本 Shortcut 只依赖 Apple 自带 Shortcuts App**，
> 不需要 Data Jar / Scriptable / 任何第三方 App。

## 为什么“几乎不要配置”

Milestone 2 的 relay 新增了简化端点 `POST /api/v1/clips/shortcut`：
Shortcut 只需要发送 `device_id` + `content` 两个字段，relay 自动生成
`clip_id`（UUID）、`content_hash`（SHA-256）、`created_at`。
Apple Shortcuts 没有内置的可靠 UUID 生成器，也无法方便地计算大文本的 SHA-256
（旧方案需要 Data Jar 或几十个动作拼装）——现在整个 Send 只有 5 个动作。

## Shortcut 步骤（“Send to ClipBridge”）

创建新 Shortcut 时，**先加 3 个 Text 动作放在最上面**（只改这 3 处，其他永远不用动）：

1. **Text** — 名称/备注 `Relay URL`，值：`http://192.168.1.123:8787`
   （换成 `scripts/show-lan-setup.ps1` 或 `scripts/start-lan-dev.ps1` 显示的地址；
   结尾不要加 `/`）
2. **Text** — 名称/备注 `Bearer Token`，值：`<你的 CLIPBRIDGE_TOKEN>`
   （从 `services\relay\.env` 读取，或重新生成：
   `python -c "import secrets; print(secrets.token_hex(32))"`；无真实 token 别截图/分享）
3. **Text** — 名称/备注 `My Device ID`，值：`iphone`（iPad 用 `ipad`；
   也可以自定义如 `tony-iphone`，只能含字母数字 `_` `-`，字母数字开头）

然后按顺序连：

4. **Get Clipboard**（获取剪贴板）
5. **If** (Clipboard) has any value —— 剪贴板为空时的处理，避免发空内容
   - **Otherwise** → **Notification** “Clipboard is empty”
6. **Get Contents of URL**（获取 URL 内容）
   - Method：**POST**
   - URL：`[Relay URL]/api/v1/clips/shortcut`（点 URL 字段，插入上面的 Relay URL 变量）
   - Headers（请求头）：
     - `Authorization` = `Bearer [Bearer Token]`（用变量拼：先输入 `Bearer ` 再插入变量）
     - `Content-Type` = `application/json`
   - 展开 **Request Body**，选择 **JSON**，编辑 JSON 字段（字典形式）：
     - 键 `device_id` ← Text 变量 `My Device ID`
     - 键 `content` ← 变量 `Clipboard`
     - **不要手动写 JSON 字符串模板**：用字典输入，引号/换行/emoji 才会被 Shortcuts
       正确转义（原样发送，内容永不改动）
7. （可选）**Get Dictionary from Input** → **Get Value for Key** `size_bytes`
   → **Notification** `Sent <size_bytes> bytes`
8. 把 Shortcut 加到 **主屏幕 / 操作按钮 / 共享面板**，日常就是：复制 → 点一下。

> 所有配置只在创建时做一次；以后每次使用不输入任何东西。

### 关于 JSON 转义（重要）

不要在“Text”里手工构造 `{"device_id":"iphone","content":"..."}`——内容是换行、
引号、反斜杠、emoji 时会出错。**必须**用 Get Contents of URL 的
Request Body = JSON（字典输入），Shortcuts 会自动转义，保证 UTF-8 与内容 100% 原样。

## 预期响应（201 Created，relay 生成全部元数据）

```json
{
  "clip_id": "3d27c1e2-...（服务器生成 UUID）",
  "device_id": "iphone",
  "content_type": "text/plain",
  "content": "Hello from iPhone",
  "content_hash": "66514bde...（服务器计算 SHA-256）",
  "created_at": "2025-06-01T12:00:03.412Z",
  "received_at": "2025-06-01T12:00:03.412Z",
  "origin": "shortcut",
  "size_bytes": 17
}
```

curl 等价：

```bash
curl -X POST http://192.168.1.123:8787/api/v1/clips/shortcut \
  -H "Authorization: Bearer $CLIPBRIDGE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"device_id":"iphone","content":"Hello from iPhone"}'
```

## 错误速查

| 结果 | 含义 |
| --- | --- |
| 401 | token 错（检查 Bearer Token 变量） |
| 413 | 内容超过 2 MiB（截断发送前本地就已禁止） |
| 409 | 15 秒内同一设备发过完全相同内容（视为已发送，不重复） |
| 422 / 415 | `device_id` 格式错 / `content_type` 非 text/plain |
| 连接失败 | 见 `docs/DEVICE_TEST.md` 的 Troubleshooting（防火墙/网络/IP） |

## 验证

发出后 2–5 秒内 Windows 剪贴板应为该文本。若不行，按 `docs/DEVICE_TEST.md`
的排障清单逐项检查（先用 Safari 打开 `http://<PC-IP>:8787/api/v1/health`）。
