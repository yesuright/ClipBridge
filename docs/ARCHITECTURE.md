# ClipBridge Architecture (Milestone 2)

## 1. Components

```text
┌──────────────────────────┐        ┌──────────────────────────┐
│  Windows 11 PC           │        │  iPhone / iPad           │
│  ClipBridge.Win (tray)   │        │  Apple Shortcuts         │
│  - hidden window +       │        │  Send: POST /clips/shortcut
│    WM_CLIPBOARDUPDATE    │        │  Get:  GET /clips/latest
│  - SyncEngine (Core)     │        │        ?exclude_device_id=self
│  - RelayClient (HTTP)    │        └───────────┬──────────────┘
└───────────┬──────────────┘                    │
            │  POST /api/v1/clips               │ LAN mode: plain HTTP on
            │  GET  /api/v1/clips?limit=N       │ the private Wi-Fi (opt-in;
            ▼                                   ▼ default = loopback only)
┌────────────────────────────────────────────────────────┐
│  Relay (services/relay)                                │
│  FastAPI app  →  auth (Bearer token)  →  ClipStore     │
│                                       (SQLite, WAL)    │
│  history: last 50 clips, newest first                 │
│  M2: /clips/shortcut (server-generated IDs)           │
│      ?exclude_device_id=... read filter (stateless)   │
└────────────────────────────────────────────────────────┘
```

| Component | Responsibility | Technology |
| --- | --- | --- |
| `ClipBridge.Win` | hidden window clipboard listener, tray shell, STA marshalling | C#/.NET 8 + WinForms |
| `ClipBridge.Core` | hashing, dedup/caches, relay client, sync engine (unit-testable) | C#/.NET 8 classlib |
| Relay API | REST endpoints, auth, validation, size limits | Python 3.13 + FastAPI |
| ClipStore | persistent clip storage, pruning, exclusion queries | stdlib `sqlite3` (WAL) |

### Milestone 2 additions

- **Explicit LAN mode** (`CLIPBRIDGE_BIND_HOST=0.0.0.0`; loopback is the default).
  The relay prints a security notice when bound beyond loopback. No firewall/router
  changes are ever made by ClipBridge — detection only, user acts manually.
- **Simplified Shortcut create** (`POST /api/v1/clips/shortcut`): Apple Shortcuts send
  only `device_id` + `content`; the relay generates `clip_id` (UUID4), `content_hash`
  and `created_at`. Rationale: Shortcuts cannot reliably generate UUIDs or compute
  SHA-256 natively, and the previous draft needed a third-party app (Data Jar) —
  removed. The stored record is canonical (same schema, limits, dedup).
- **Requester-exclusion read filter**: `GET /clips/latest?exclude_device_id=<id>` (and
  the same param on `/clips`). Stateless query-level filter, no per-device server
  state; stores learn nothing about requesters; POST/dedup semantics untouched.

## 2. Data flow

### Windows → Relay (upload)

```
user copies text
  → WM_CLIPBOARDUPDATE (hidden window)
  → engine.OnClipboardChangedAsync(text)
  → non-text?  → ignore (never crash; images/files are out of scope)
  → > 2 MiB?   → log + skip (boundary is explicit, no truncation)
  → sha256(content) in AppliedRemoteTracker? → suppress (remote echo; loop prevention)
  → build ClipRecord (new clip_id GUID, device_id, created_at UTC, origin="local")
  → POST /api/v1/clips (Authorization: Bearer <token>)
  → relay validates (hash recompute, size, schema) → SQLite insert → 201
  → mark clip_id as "uploaded" (poll echo filter)
```

### Relay → Windows (receive)

```
poll every PollIntervalMs (default 2 s; 0.5–60 s clamp)
  → GET /api/v1/clips?limit=20
  → pick the newest clip that is:
       • not from our own device         (skip own uploads / own echoes)
       • not an exact echo of this session's uploads (clip_id in UploadedEchoTracker)
       • not already applied within the suppression window (clip_id in AppliedRemoteTracker)
       • not strictly older than the newest remote clip already applied this session
         (received_at floor — no cascading re-applies of stale history)
       • text/plain and hash/size pass integrity verification
       • clipboard does not already hold this exact content
  → MarkApplied(clip_id, content_hash)  ← BEFORE SetText (race-free suppression)
  → Clipboard.SetText (bounded 5 retries with backoff on clipboard busy)
  → the resulting WM_CLIPBOARDUPDATE echo is suppressed by step above → NO loop
```

### iPhone/iPad (Apple Shortcuts, Milestone 2)

```
Send:  Get Clipboard
       → POST /api/v1/clips/shortcut {"device_id":"iphone","content":<clipboard>}
           (Authorization: Bearer <token>; server generates clip_id/hash/created_at)
       → 201 -> optional notification (size_bytes/device_id)

Get:   GET /api/v1/clips/latest?exclude_device_id=iphone   (Authorization: Bearer <token>)
       → 200 {content,...} -> Set Clipboard (content field verbatim)
       → 404 -> "no content from other devices" (Shortcut branches on this)
       (fallback variant: GET /api/v1/clips?limit=10&exclude_device_id=iphone
        -> always 200; empty "clips" list = nothing new)
```

The Shortcuts contain NO dynamic fields except the three configured once at creation
(URL, token, device id). The device id is a free-form string — the phone's shortcut
uses its own id (`iphone`/`ipad`/`tony-iphone`...), so `exclude_device_id` keeps each
device from fetching its own clipboard back.

### UI (Minimal, Milestone 1)

- Tray icon (system icon only — no custom art) with: pause/resume sync, check now, open log folder, exit.
- No settings window: config is env vars + `%APPDATA%\ClipBridge\config.json`.
- Everything visual beyond this is explicitly deferred (see README deferred section).

## 3. Windows clipboard integration

- **Listener**: `AddClipboardFormatListener(hwnd)` on an invisible message-only-style form
  (opacity 0, off-screen, not in taskbar). `WM_CLIPBOARDUPDATE` arrives on the STA UI thread.
- **Reading**: `Clipboard.ContainsText(UnicodeText)` → `GetText(UnicodeText)`.
  Any `ExternalException` (clipboard briefly locked by another app) → return null → ignored.
- **Writing**: `Clipboard.SetText(text, UnicodeText)` marshalled via `Form.Invoke` to the STA
  thread; engine retries up to 5 times with 50–250 ms backoff, then gives up *for that clip*
  and keeps running.
- No polling of the clipboard itself; the OS event is the trigger (zero idle CPU, no wakeups).

## 4. Relay

Choice rationale — **Python 3.13 + FastAPI + uvicorn + stdlib SQLite**:

1. **Python 3.13 is already installed** on the machine; zero extra language runtimes.
2. **stdlib `sqlite3`**: no infra (no Redis/Kafka/Postgres), single file, WAL mode, more than
   enough for 3 devices and 50-clip history.
3. **FastAPI**: typed schemas, automatic `/docs` Swagger — a free *debug* page for development
   and for building the Shortcuts payloads; no frontend work needed.
4. **No body-size surprises**: uvicorn/h11 impose no default request body cap; the relay sets
   explicit, documented limits (2 MiB content / 16 MiB JSON body guard) instead of inheriting
   framework defaults that would truncate long prompts.
5. Sync endpoints are simple; no ORM needed (small, explicit `CREATE TABLE` wins).

## 5. Storage

- SQLite DB at `services/relay/data/clipbridge.db` (gitignored), WAL journaling.
- Schema:

```sql
CREATE TABLE clips (
  clip_id      TEXT PRIMARY KEY,          -- client-generated UUID (idempotency)
  device_id    TEXT NOT NULL,             -- source device
  content_type TEXT NOT NULL,             -- 'text/plain' (M1)
  content      TEXT NOT NULL,             -- full text, up to 2 MiB
  content_hash TEXT NOT NULL,             -- sha256 hex
  created_at   TEXT NOT NULL,             -- client UTC ISO 8601
  received_at  TEXT NOT NULL,             -- server UTC ISO 8601 (history order)
  origin       TEXT NOT NULL DEFAULT 'local',
  size_bytes   INTEGER NOT NULL           -- UTF-8 byte count
);
```

- `ClipStore` is an abstract interface: `SqliteClipStore` (default) and `MemoryClipStore`
  (tests). API layer only talks to the interface — swapping storage later (e.g. Postgres)
  requires no API changes.
- **History**: newest N kept (default 50, `CLIPBRIDGE_HISTORY_LIMIT`); older rows pruned on insert.

## 6. Receive strategy — why short polling (2 s)

Alternatives considered:

| Strategy | Verdict |
| --- | --- |
| Short polling (2 s) | **Chosen.** Simplest, stateless, survives restarts, zero persistent connections. 2 s ≈ "a few seconds" UX on a personal tool, negligible load (~0.5 req/s for one client). |
| Long polling | Simple-ish but adds hanging-request bookkeeping and timeout complexity; no real benefit at 1 client. |
| SSE | One-way only (simplifies upload needed), half-open connections can die silently on home NATs. |
| WebSocket | Bidirectional, but needs upgrade handling, reconnect logic, keepalive. Overkill for 3 devices. |

Also: the client does **not** poll every 100 ms; interval is clamped 0.5–60 s, default 2 s,
configurable via env/config. A relay restart is handled naturally (next poll succeeds).

## 7. Future: Shortcuts / PWA, trust boundaries

- **Shortcuts (V1 iOS)**: no native app. Apple Shortcuts call the same REST API
  (see `shortcuts/`). The API is intentionally plain JSON — Shortcuts' "Get Contents of URL"
  action works directly.
- **PWA (later, Codex)**: plain frontend served separately; it consumes the same REST API.
  The relay protocol must not change; a future web UI will be a separate artifact.
  CORS is intentionally **not** enabled in M1 (nothing cross-origin needs it yet); when the PWA
  needs it, add a config-driven CORS allow-list — a relay config change, not a protocol change.
- **Trust boundaries**:

```text
[clipboard content]  --(trust: user's own device)--> [client app]
[client]             --(trust: shared token; TLS local now, HTTPS later)--> [relay]
[relay]              --(trust: server admin; NOT trusted for E2E privacy)--> storage
```

  The relay sees plaintext clipboard content today (deployment is localhost/private). Future
  E2E encryption would move trust to the clients: content encrypted before POST, relay stores
  opaque blobs. The schema already separates `content` from `content_hash`/metadata, so an
  encrypted-content variant can be added as a new `content_type` without breaking M1 records.
  **Not implemented in M1** (explicitly deferred).

## 8. Resource envelope (targets)

- Client: tray-only, no visible window; clipboard listener is event-driven (no polling);
  idle CPU ≈ 0%; RAM low (one HTTP client + string buffers).
- Relay: single uvicorn process, single SQLite connection; no background workers.
- LAN reads are stateless — the relay keeps no requester state, so iPhone/iPad can
  poll at their own pace without server-side memory growth.

## 9. Roadmap pointer

- Shortcuts guide: `shortcuts/SEND_TO_CLIPBRIDGE.md`, `shortcuts/GET_FROM_CLIPBRIDGE.md`.
- Manual device test: `docs/DEVICE_TEST.md`.
- Remote access (HTTPS / tunnel / E2E encryption / pairing) is explicitly NOT part of
  this milestone; it is designed later (see SECURITY.md §7).
