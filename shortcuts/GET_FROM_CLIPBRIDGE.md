# Apple Shortcuts — GET_FROM_CLIPBRIDGE

把 Windows（或任意设备）刚同步的最新文本拉取到 iPhone / iPad 并复制到系统剪贴板。

## Shortcut 步骤（Shortcuts App）

1. **URL** — `http://<PC-IP>:8787/api/v1/clips/latest`
   （生产：`https://relay.example.com/api/v1/clips/latest`）
2. **Get Contents of URL** — GET；Headers：`Authorization` = `Bearer <CLIPBRIDGE_TOKEN>`
3. **Get Dictionary from Input**（解析返回 JSON）
4. **Get Value for key `content` in Dictionary**
5. **Copy to Clipboard**
6. （可选）**Notification** 显示 `内容长度 + device_id`（不要显示全文——本步只是可选提示，
   用「Get Dictionary Value from Input → `size_bytes` / `device_id`」）

## 历史选择版（最近 N 条里挑一条）

1. **URL** — `http://<PC-IP>:8787/api/v1/clips?limit=20`
2. **Get Contents of URL**（Bearer header 同上）
3. **Get Dictionary from Input → `clips`**（数组）
4. **Choose from List**（每项显示可用 "device_id" 与首行；Shortcuts 不便做复杂 map——
   可先用「Get Item from List」取前几条，或直接固定取第 1 条 = 最新）
5. **Get Value for key `content` from Chosen Item**
6. **Copy to Clipboard**

> 提示：把「Content」值作为列表项名时选择型 Shortcuts 会直接显示整个长文本——
> 可改用「Text」动作拼接 `device_id + " · " + 字数`。

## curl 等价

```bash
curl http://127.0.0.1:8787/api/v1/clips/latest \
  -H "Authorization: Bearer $CLIPBRIDGE_TOKEN"
```

```bash
curl "http://127.0.0.1:8787/api/v1/clips?limit=20" \
  -H "Authorization: Bearer $CLIPBRIDGE_TOKEN"
```

## 预期响应

`GET /clips/latest`（200）：

```json
{
  "clip_id": "550e8400-e29b-41d4-a716-446655440000",
  "device_id": "windows-main",
  "content_type": "text/plain",
  "content": "Hello from ClipBridge",
  "content_hash": "...",
  "created_at": "2025-06-01T12:00:00.123Z",
  "received_at": "2025-06-01T12:00:00.456Z",
  "origin": "local",
  "size_bytes": 21
}
```

`GET /clips?limit=20`（200）：

```json
{
  "count": 3,
  "clips": [ { ...最新一条... }, { ... }, { ... } ]
}
```

`404 {"error":"not_found"}` = 还没有任何内容。`401` = token 错误。

## 注意

- `latest` 是**全局最新**（不限设备）。Windows 自己的上传也会出现在历史里——iPhone 端
  拉取时如果只想要「来自 Windows 的最新」，先 `GET /clips?limit=50` 再选
  `device_id == "windows-main"` 的第一条。
- 大文本（几百 KB – 2 MiB）在 Shortcuts 中完全可行，但 iPhone 上复制 2 MiB 文本会占用
  较多内存，建议按需再复制。
- ”Get from ClipBridge” 与 ”Send to ClipBridge” 两个 Shortcut 都建议配到
  **运行Widget / Back Tap / Share Sheet**，日常只需一次点击。
