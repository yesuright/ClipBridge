# Apple Shortcuts — SEND_TO_CLIPBRIDGE

把 iPhone / iPad 当前剪贴板文本发送到 Windows：POST 到 ClipBridge relay，Windows 客户端
几秒内收到并自动写入系统剪贴板。

> 前置：relay 已启动且 iPhone 可访问（局域网 `http://<PC-IP>:8787`，或未来 HTTPS 域名）。
> 所有请求需 `Authorization: Bearer <CLIPBRIDGE_TOKEN>`。

## Shortcut 步骤

1. **Get Clipboard**（获取剪贴板）
2. **Text / JSON** 构造请求体（见下方模板）
3. **URL** — `http://<PC-IP>:8787/api/v1/clips`（生产改为 `https://…`）
4. **Get Contents of URL**
   - Method: **POST**
   - Headers: `Authorization` = `Bearer <CLIPBRIDGE_TOKEN>`；`Content-Type` = `application/json`
   - Request Body: **JSON**（粘贴下方模板）
5. **Get Dictionary from Input** →（可选）**Show Result**：显示 `clip_id` / `size_bytes` 确认成功
6. 将 Shortcut 挂到 **Share Sheet / Back Tap / Home Screen** 即可日常单点使用

## 请求体模板（可直接粘贴）

```json
{
  "clip_id": "00000000-0000-4000-8000-000000000001",
  "device_id": "iphone",
  "content_type": "text/plain",
  "content": "{{剪贴板文本}}",
  "created_at": "2025-06-01T12:00:00.000Z",
  "origin": "shortcut"
}
```

### 关于两个可省字段（了解即可，避免踩坑）

- `content_hash`：**省略**。relay 会自动计算并返回标准 SHA-256；只有需要校验完整性时才提供。
- `clip_id`：relay 要求合法 **UUID**（短横线 8-4-4-4-12）。Shortcuts 没有内建 UUID 生成器，
  最省事且可靠的方案是用 **Data Jar**（免费 App）存一个自增数字，每次 +1，再拼成
  `00000000-0000-4000-8000-` + 12 位补零数字（如 `…8000-000000000042`）——格式合法且保证唯一。
  若不想装 Data Jar：用 Scriptable/a-Shell 一行脚本生成 UUID，或用「Random Number」→
  「Format Number」→ 拼进 4 段（略繁琐）；Data Jar 最稳。
- `created_at`：可省略（relay 用服务器时间），想记录手机本地时刻就用
  **Current Date → Format Date → ISO 8601 with timezone**。

## curl 等价（完整字段示例）

```bash
curl -X POST http://127.0.0.1:8787/api/v1/clips \
  -H "Authorization: Bearer $CLIPBRIDGE_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "clip_id": "00000000-0000-4000-8000-000000000001",
    "device_id": "iphone",
    "content_type": "text/plain",
    "content": "Hello from iPhone",
    "created_at": "2025-06-01T12:00:00.000Z",
    "origin": "shortcut"
  }'
```

## 预期响应（201 Created）

```json
{
  "clip_id": "00000000-0000-4000-8000-000000000001",
  "device_id": "iphone",
  "content_type": "text/plain",
  "content": "Hello from iPhone",
  "content_hash": "66514bde972fda44f3f9dc2c8fdea929761886b2d1fdb0a873b4f43571a88e42",
  "created_at": "2025-06-01T12:00:00.000Z",
  "received_at": "2025-06-01T12:00:03.412Z",
  "origin": "shortcut",
  "size_bytes": 17
}
```

## 错误速查

| Status | 含义 |
| --- | --- |
| 401 | token 错误（检查 `CLIPBRIDGE_TOKEN` 与 Header） |
| 413 | 内容超过 2 MiB |
| 409 `duplicate_clip_id` | 用了同一个 `clip_id`（重试/重发时有用，不算错；需换新 UUID） |
| 409 `duplicate_content_in_window` | 15 秒内同一设备发过完全相同内容（去重窗口=防循环） |
| 422 / 400 / 415 | 字段格式错误（`clip_id` 不是 UUID、`content_type` 不是 text/plain 等） |

错误响应均为 `{"error":"...","detail":...}` JSON。

## 验证

发出后 2–5 秒内 Windows 剪贴板应为该文本。若不行：
1. Safari 打开 `http://<PC-IP>:8787/api/v1/health` 确认可达；
2. 看 relay 日志 `services/relay/data/logs/relay.log`（只记录 clip_id/size/hash 前缀，无正文）；
3. 确认 Windows 客户端托盘图标存在、客户端 `client.log` 无 poll 报错。
