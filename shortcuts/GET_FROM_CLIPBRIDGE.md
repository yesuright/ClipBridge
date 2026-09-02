# Apple Shortcuts — GET_FROM_CLIPBRIDGE

把 Windows（或任意其他设备）刚同步的最新文本拉取到 iPhone / iPad 并复制到系统剪贴板。
**本 Shortcut 只依赖 Apple 自带 Shortcuts App**，无第三方依赖。

## 关键机制：`exclude_device_id`

Milestone 2 的 relay 支持：

```
GET /api/v1/clips/latest?exclude_device_id=iphone     ← 最新“非 iphone 自己”的文本
GET /api/v1/clips?limit=10&exclude_device_id=iphone   ← 最近 N 条“非 iphone 自己”的文本
```

- 你永远**不会**取回自己设备刚发出的内容（不会出现“自己发出去又被自己复制回来”）；
- 没有任何其他设备的内容时：`latest` 返回 `404 not_found`（此版本把 404 当作“没有新内容”），
  `clips` 列表版总是返回 `200` 空列表（可干净地分支提示）；
- 纯查询参数，服务端无任何 per-device 状态；Windows 客户端逻辑不受影响；
- `exclude_device_id` 里的值只对你自己的 Shortcut 有意义：iPhone = `iphone`，
  iPad = `ipad`（可自定义）。

## 推荐版（列表版，约 9 个动作，空内容也能优雅提示）

创建新 Shortcut，先加 3 个 **Text** 动作放最上面（只改这 3 处）：

1. **Text** `Relay URL` = `http://192.168.1.123:8787`（以 `show-lan-setup.ps1` 显示为准，
   结尾不要 `/`）
2. **Text** `Bearer Token` = 你的 `CLIPBRIDGE_TOKEN`（从 `services\relay\.env` 读取）
3. **Text** `My Device ID` = `iphone`（iPad 用 `ipad`）

然后：

4. **URL**：`[Relay URL]/api/v1/clips?limit=10&exclude_device_id=[My Device ID]`
   （在 URL 字段里插入两个变量）
5. **Get Contents of URL** — GET（只需一个 Header）：
   - `Authorization` = `Bearer [Bearer Token]`
6. **Get Dictionary from Input**（解析返回 JSON）
7. **Get Value for Key** `clips` in Dictionary（得到列表）
8. **If** (clips) has any value：
   - **Get Item from List**（first item）
   - **Get Value for Key** `content`
   - **Copy to Clipboard**（写入系统剪贴板；内容 100% 原样：换行/Unicode/Markdown/代码保留）
   - （可选）Notification：拼接 `size_bytes` 与 `device_id`，提示“已复制”
   - **Otherwise**：**Notification** “No new content from other devices”
9. 挂到 **主屏幕 / 操作按钮 / 共享面板**：日常 = 点一下 → 粘贴，结束。

## 极简版（4 个动作，但“无新内容”时会看到请求失败）

1. **URL**：`[Relay URL]/api/v1/clips/latest?exclude_device_id=[My Device ID]`
2. **Get Contents of URL**（GET；`Authorization: Bearer [Bearer Token]`）
3. **Get Dictionary from Input** → **Get Value for Key** `content`
4. **Copy to Clipboard** → Notification（可选）

> 极简版的代价：当 `latest` 返回 `404 {"error":"not_found"}`（没有其他设备的内容）
> 时，Get Contents of URL 会报错并停止——这属于“预期空”，不是故障；介意的话用推荐版。

## 保持原样

- 不做任何 trim / 不替换换行 / 不改编码：`content` 字段原样进入剪贴板。
- 2 MiB 上限与 Windows 一致；几百 KB 的长 prompt 完全可行，超大文本建议按需再复制
  （避免手机内存压力）。

## 预期响应

`GET /api/v1/clips/latest?exclude_device_id=iphone`（200）：

```json
{
  "clip_id": "550e8400-...",
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

`GET /api/v1/clips?limit=10&exclude_device_id=iphone`（200）：

```json
{ "count": 3, "clips": [ { ...最新一条非本设备... }, { ... }, { ... } ] }
```

- `404 {"error":"not_found"}` = 还没有任何“其他设备”的内容（正常空状态）。
- `401` = token 错。

## curl 等价

```bash
curl "http://192.168.1.123:8787/api/v1/clips/latest?exclude_device_id=iphone" \
  -H "Authorization: Bearer $CLIPBRIDGE_TOKEN"
```

## 注意

- 想“挑一条历史”，把推荐版的 `limit=10` 改大并在 `clips` 列表上 **Choose from List**
  （列表项建议显示 `device_id · size_bytes`，不要显示全文）。
- iPhone 与 iPad 各建一个 Shortcut，唯一区别是 `My Device ID`（`iphone` vs `ipad`）。
- 与 “Send to ClipBridge” 配合即形成完整闭环：
  其它设备发 → 你点一次 → 粘贴。
