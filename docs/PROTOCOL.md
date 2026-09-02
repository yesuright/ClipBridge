# ClipBridge Protocol v1 (Milestone 2)

All endpoints use **JSON, UTF-8, UTC ISO 8601 (RFC 3339)** timestamps with `Z` suffix,
e.g. `2025-06-01T12:00:00.123Z`.

Base URL: `http://127.0.0.1:8787` (dev) — production MUST be HTTPS (see SECURITY.md).
LAN mode for iPhone/iPad: `http://<PC-LAN-IP>:8787` (same trusted Wi-Fi; see DEVICE_TEST.md).

## 1. Endpoints

| Method | Path | Auth | Description | Success |
| --- | --- | --- | --- | --- |
| GET | `/api/v1/health` | no | liveness | 200 `{"status":"ok", ...}` |
| POST | `/api/v1/clips` | yes | create a clip (canonical, client-generated IDs) | 201 Clip |
| POST | `/api/v1/clips/shortcut` | yes | create a clip (Minimal schema; server generates IDs) | 201 Clip |
| GET | `/api/v1/clips/latest` | yes | newest clip overall | 200 Clip / 404 |
| GET | `/api/v1/clips/latest?exclude_device_id=<id>` | yes | newest clip NOT from `<id>` | 200 Clip / 404 |
| GET | `/api/v1/clips?limit=N` | yes | recent history, newest first | 200 `{"count":N,"clips":[...]}` |
| GET | `/api/v1/clips?limit=N&exclude_device_id=<id>` | yes | recent history excluding one device | 200 `{"count":N,"clips":[...]}` |
| GET | `/api/v1/clips/{clip_id}` | yes | single clip | 200 Clip / 404 |
| POST | `/api/v1/admin/shutdown` | yes | **M3 internal only**: ask the relay to stop gracefully (used by the tray app; additive, does not affect any other endpoint) | 200 `{"status":"shutting_down"}` / 503 when running without a controlling server |

Server-hosted: the daily-use artifact always runs `POST /api/v1/admin/shutdown`
(auth-required). The endpoint is purely lifecycle; it never touches stored clips.

`limit`: default 50, clamped to 1–200.

`exclude_device_id` (Milestone 2): read-side filter for "get the newest clip from
*other* devices" — exactly what an iPhone Shortcut needs so it never re-fetches its
own clipboard. Rules:

- The value must match the device-id grammar (1–64 chars `[A-Za-z0-9_-]`, alphanumeric
  start); anything else → **422**.
- The filter applies to **queries only** and is **stateless** — no per-device server
  state, no sessions, no "last seen" tracking. Two requests with the same param get
  the same result until new clips arrive.
- It never affects `POST` behaviour: dedup, idempotency and the 15 s window consider
  ALL records including the requester's own (loop prevention stays intact).
- Device ids are never hardcoded server-side; any valid id from any device works
  (`iphone`, `ipad`, `tony-iphone`, `windows-main`, ...).
- When no clip from another device exists, the read returns **404 `not_found`**
  (`latest`) or an **empty list** (`/clips` — always 200), so Shortcuts can branch
  on "no new content" cleanly.

## 2. Authentication

- Every request (except `/api/v1/health` and `/`) must send:
  `Authorization: Bearer <CLIPBRIDGE_TOKEN>`
- Token is shared by all devices (single-trust model, Milestone 1).
- Comparison is constant-time on the server.
- Failure → `401` with body `{"error":"unauthorized"}`. Tokens are never logged and never
  returned in responses.

## 3. Clip record (canonical schema)

```json
{
  "clip_id": "550e8400-e29b-41d4-a716-446655440000",
  "device_id": "windows-main",
  "content_type": "text/plain",
  "content": "...",
  "content_hash": "sha256 hex (64 chars, lowercase)",
  "created_at": "2025-06-01T12:00:00.123Z",
  "received_at": "2025-06-01T12:00:00.456Z",
  "origin": "local",
  "size_bytes": 12345
}
```

| Field | Required (request) | Rules |
| --- | --- | --- |
| `clip_id` | yes | UUID string; server canonicalizes to lowercase; **client-generated** |
| `device_id` | yes | 1–64 chars `[A-Za-z0-9_-]`, starting alphanumeric; free-form (`windows-main`, `iphone`, `ipad`) |
| `content_type` | optional | only `text/plain` in M1; anything else → **415** |
| `content` | yes | string; ≤ **2 MiB UTF-8 bytes** (else 413); never truncated |
| `content_hash` | optional | if provided, server **re-computes SHA-256** and rejects mismatch → **422 hash_mismatch** |
| `created_at` | optional | RFC 3339 **with timezone** (naive → 422); missing → server now; stored normalized to UTC |
| `origin` | optional | 0–64 chars `[A-Za-z0-9._-]`; default `local`; examples: `local`, `shortcut`, `api` |
| `received_at`, `size_bytes` | server-side | always computed/stored by the server; clients ignore their own values |

Notes:
- `content` is never truncated anywhere in the pipeline (JSON parse → size check → SQLite TEXT).
- Neither `content` nor `content_hash` full values appear in logs (only `hash_prefix` = first 8 chars).

## 3b. Minimized create request for Apple Shortcuts

`POST /api/v1/clips/shortcut` accepts **only** `device_id` + `content` (+ optional
`content_type` / `origin`) and the server derives `clip_id`, `content_hash` and
`created_at`. The stored record is **protocol-identical** to the canonical endpoint:
same ClipOut schema, same 2 MiB limit, same dedup window, same error codes.

| Field | Required | Default | Rules |
| --- | --- | --- | --- |
| `device_id` | yes | — | same as canonical |
| `content` | yes | — | ≤ 2 MiB UTF-8 bytes (else 413) |
| `content_type` | optional | `text/plain` | only `text/plain` (else 415) |
| `origin` | optional | `shortcut` | 0–64 chars `[A-Za-z0-9._-]` |
| `clip_id`, `content_hash`, `created_at` | **not accepted — always server-generated** | | |

```http
POST /api/v1/clips/shortcut
Authorization: Bearer <token>
Content-Type: application/json

{"device_id": "iphone", "content": "Hello from iPhone"}
```

Response: `201` with the full canonical Clip (incl. server-generated `clip_id`,
`content_hash`, `created_at`, `size_bytes`).

Why: Apple Shortcuts has no built-in, reliable UUID generator (the usual workarounds
need a third-party app like Data Jar, or brittle Random-Number/string assembly) and
cannot practically compute SHA-256 of a large clipboard. Moving those derivations to
the relay keeps a Shortcut at ~5 actions with no third-party dependency.

Evaluation (Milestone 2):

- **Protocol consistency**: request schema is a strict subset; response is the
  canonical Clip; storage/history/dedup are the same code path (`origin=shortcut`
  marks these records).
- **Spoofing**: `device_id` remains a client-declared string (already documented as
  not-authenticated identity); the endpoint is gated by the same shared bearer token;
  clip ids are server-generated UUIDs, so no collision-based overwrite is possible.
  No new trust boundary is introduced.
- **Loop prevention**: unchanged — same 15 s same-device/content dedup window; the
  Windows client filters its own `device_id` and echo trackers; iPhone/iPad Get
  shortcuts use `exclude_device_id` to skip their own records.
- **Idempotency**: server-generated clip ids mean a failed HTTP retry after a
  successful POST is de-duplicated by the 15 s window (409) instead of the clip_id
  key. That is acceptable for a personal tool and documented in the Shortcuts guide;
  retries within the window are treated as "already sent".

## 4. Hashes

- `content_hash = SHA-256(UTF-8 bytes of content)`, lowercase hex.
- Computed identically in the C# client (`SHA256.HashData(Encoding.UTF8.GetBytes(...))`)
  and the relay (`hashlib.sha256(content.encode("utf-8")).hexdigest()`).
- The client verifies the hash of every clip before applying it to the clipboard; mismatch → skip.

## 5. Timestamps

- `created_at`: client wall clock (authoritative for display).
- `received_at`: server clock (authoritative for history ordering & dedup window).
- Both: UTC ISO 8601 with milliseconds, `Z` suffix.

## 6. History

- Newest-first ordering by `received_at` (tiebreak: insertion order).
- Default keep 50 (`CLIPBRIDGE_HISTORY_LIMIT`); older rows pruned on insert.
- `GET /api/v1/clips` never returns unbounded results (limit clamp 200).

## 7. Dedup & loop prevention (authoritative strategy)

The loop to prevent:

```text
Windows copy A → upload A → relay → Windows downloads A → SetClipboard(A)
  → listener fires → "copy" detection → upload A → ... infinite
```

**Explicitly NOT used**: "content is equal, therefore ignore" — users deliberately re-copy the
same text at different times, and those are legitimate new events. Instead:

### Layer 1 — client apply-suppression (primary)
When the client writes a remote clip to the clipboard:
1. `MarkApplied(clip_id, content_hash)` is recorded **before** `SetText` (race-free).
2. The resulting `WM_CLIPBOARDUPDATE` for the same content reaches the listener, which finds
   `content_hash` in `AppliedRemoteTracker` (window = `SuppressWindowSeconds`, default 30 s)
   and does nothing — no re-upload.

Trade-off (documented): deliberately re-copying the *same text within 30 s of receiving it*
is treated as an echo and not re-uploaded. After the window expires, re-copies upload normally.

### Layer 1b — client recent-upload skip
Content uploaded within the last `UploadMemorySeconds` (default 900 s, but the relay's own
15 s dedup window is the effective bound) is not POSTed again on repeated clipboard
notifications. The relay would reject it with `409 duplicate_content_in_window` anyway,
so this only saves the round trip — no user-visible event is lost.

### Layer 2 — client poll filter
- Skip clips whose `device_id` equals the local device id.
- Skip `clip_id`s uploaded during this session (`UploadedEchoTracker`, 15 min).
- Skip clips already applied within the window; skip if clipboard already holds that content.
- Skip clips strictly **older** than the newest remote clip applied this session
  (`received_at` floor) — the client syncs "up to" the newest remote clip and never walks
  back into history, even across restarts with a full history.
- Integrity gate: hash & size of remote clip verified before apply.

### Layer 3 — relay dedup window
Same device posting identical content (`content_hash`, `device_id`) within
`CLIPBRIDGE_DEDUP_WINDOW_SECONDS` (default 15 s) → **409 `duplicate_content_in_window`**
with the existing `clip_id`; the record is NOT duplicated. Different devices are always allowed.

### Layer 4 — clip_id idempotency
`clip_id` is PRIMARY KEY. Re-POSTing the same `clip_id` → **409 `duplicate_clip_id`**
(protects HTTP retries from double-storing).

## 8. Error responses

Uniform shape: `{"error": "<error_code>", ...extra fields}`.

| Status | error_code | Meaning | Extra |
| --- | --- | --- | --- |
| 400 | `invalid_json` / `bad_request` | body not parseable / wrong shape | `detail` |
| 401 | `unauthorized` | missing or wrong token | — |
| 404 | `not_found` | no clip / no history / no clip from another device | — |
| 409 | `duplicate_clip_id` | clip_id already stored | `clip_id` |
| 409 | `duplicate_content_in_window` | same device + same content within window | `clip_id` (existing) |
| 413 | `content_too_large` | content > 2 MiB | `max_bytes`, `actual_bytes` |
| 413 | `body_too_large` | HTTP body > 16 MiB (JSON escape guard) | `max_bytes` |
| 415 | `unsupported_content_type` | content_type ≠ text/plain | `supported` |
| 422 | `invalid_payload` | schema/validation failure (incl. bad `exclude_device_id`) | `detail` (loc/msg/type) |
| 422 | `hash_mismatch` | provided content_hash ≠ server-computed | `provided`, `computed` |
| 500 | `internal_error` | unexpected server error (content never logged) | — |

## 9. Payload limits

- `CLIPBRIDGE_MAX_CONTENT_BYTES` = 2 MiB (2,097,152) of UTF-8 text — hard content limit
  (enforced on `len(content.encode('utf-8'))`; the JSON string length is NOT the limit).
- `CLIPBRIDGE_MAX_BODY_BYTES` = 16 MiB — HTTP body guard (JSON escaping of e.g. newlines can
  expand the body; content limit is still authoritative).
- Client and relay share the same content limit; the client refuses to upload oversized text
  locally (log + skip, never truncate).

## 10. Example: complete request/response

```http
POST /api/v1/clips
Authorization: Bearer <token>
Content-Type: application/json

{
  "clip_id": "0f7f2b7e-3e4b-4c1b-9f6a-111111111111",
  "device_id": "iphone",
  "content_type": "text/plain",
  "content": "Hello from iPhone",
  "content_hash": "66514bde972fda44f3f9dc2c8fdea929761886b2d1fdb0a873b4f43571a88e42",
  "created_at": "2025-06-01T12:00:00.000Z",
  "origin": "shortcut"
}
```

```http
201 Created
{
  "clip_id": "0f7f2b7e-3e4b-4c1b-9f6a-111111111111",
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
