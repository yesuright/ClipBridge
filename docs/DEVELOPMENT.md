# ClipBridge Development

## 1. Prerequisites

| Tool | Version | Where |
| --- | --- | --- |
| Git | 2.x | any |
| .NET SDK | 8.0 (LTS) | https://dotnet.microsoft.com/download (or `dotnet-install.ps1 -Channel 8.0 -InstallDir $env:USERPROFILE\.dotnet`; `scripts/dev-client.ps1` and `tests/e2e/run-end-to-end.ps1` automatically prefer `%USERPROFILE%\.dotnet\dotnet.exe`) |
| Python | 3.12+ (3.13 tested) | https://www.python.org |

> Network note: if nuget.org is slow on your network (common in CN), add a mirror to the
> user-level NuGet config — e.g. a NuGet v3 source pointing at a working mirror (Huawei:
> `https://repo.huaweicloud.com/repository/nuget/v3/index.json`). The test project restores
> ~25 MB on first build; everything is cached afterwards.

The repo pins .NET 8 via `global.json` (`rollForward: latestFeature`).

## 2. Repository layout

```text
apps/windows/            C# solution (Core library + Win tray host + xunit tests)
services/relay/          Python FastAPI relay (+ pytest tests)
shortcuts/               Apple Shortcuts integration notes
docs/                    architecture / protocol / security / development
scripts/                 dev helpers (relay, client, secret scan)
tests/e2e/               full-stack end-to-end script
```

## 3. Relay — local run

```powershell
cd services\relay
python -m venv .venv
.\.venv\Scripts\pip install -r requirements.txt        # runtime
# optional test deps:
.\.venv\Scripts\pip install -r requirements-dev.txt

Copy-Item .env.example .env                            # set CLIPBRIDGE_TOKEN
.\.venv\Scripts\python run.py                          # http://127.0.0.1:8787
```

Or use `scripts/dev-relay.ps1` (generates an ephemeral dev token and prints it if no `.env`).

Swagger UI at `http://127.0.0.1:8787/docs` — the provided debug page (plain, no custom UI).
Health: `GET /api/v1/health`.

## 4. Windows client — build & run

```powershell
dotnet build apps\windows\ClipBridge.sln
dotnet run --project apps\windows\src\ClipBridge.Win
```

Requires a token: set `CLIPBRIDGE_DEVICE_TOKEN` (env) or create `%APPDATA%\ClipBridge\config.json`:

```json
{
  "RelayUrl": "http://127.0.0.1:8787",
  "DeviceId": "windows-main",
  "DeviceToken": "<same token as relay>",
  "PollIntervalMs": 2000
}
```

The app runs in the system tray (system icon). Right-click → pause / check now / open log /
exit. Logs: `%APPDATA%\ClipBridge\client.log`.

## 5. Environment variables

### Relay (`services/relay/`, see `.env.example`)

| Variable | Default | Notes |
| --- | --- | --- |
| `CLIPBRIDGE_HOST` | `127.0.0.1` | bind address (0.0.0.0 only behind HTTPS) |
| `CLIPBRIDGE_PORT` | `8787` | — |
| `CLIPBRIDGE_TOKEN` | — | **required**; fail-fast if missing |
| `CLIPBRIDGE_DB_PATH` | `data/clipbridge.db` | relative to `services/relay/` |
| `CLIPBRIDGE_MAX_CONTENT_BYTES` | `2097152` | 2 MiB UTF-8 content limit |
| `CLIPBRIDGE_MAX_BODY_BYTES` | `16777216` | HTTP body guard |
| `CLIPBRIDGE_HISTORY_LIMIT` | `50` | keep newest N |
| `CLIPBRIDGE_DEDUP_WINDOW_SECONDS` | `15` | same-device identical-content window |
| `CLIPBRIDGE_LOG_LEVEL` | `INFO` | — |
| `CLIPBRIDGE_LOG_DIR` | `data/logs` | — |

### Client (override priorities: env > config.json > defaults)

| Variable | Default | Notes |
| --- | --- | --- |
| `CLIPBRIDGE_RELAY_URL` | `http://127.0.0.1:8787` | base URL; `/api/v1` appended |
| `CLIPBRIDGE_DEVICE_ID` | `windows-main` | any `[A-Za-z0-9_-]{1,64}` |
| `CLIPBRIDGE_DEVICE_TOKEN` | — | **required** |
| `CLIPBRIDGE_POLL_INTERVAL_MS` | `2000` | clamped 500–60000 |
| `CLIPBRIDGE_HTTP_TIMEOUT_SECONDS` | `20` | clamped 5–300 |
| `CLIPBRIDGE_SUPPRESS_WINDOW_SECONDS` | `30` | applied-remote echo window |
| `CLIPBRIDGE_UPLOAD_MEMORY_SECONDS` | `900` | own-upload echo memory |

## 6. Tests

```powershell
# Relay (61 tests): auth / CRUD / history / validation / long text / unicode / dedup / redaction
cd services\relay
.\.venv\Scripts\python -m pytest

# Windows Core (xunit): hashing / caches / trackers / sync engine / relay client / config / logging
dotnet test apps\windows\ClipBridge.sln

# Full-stack E2E on THIS machine (real clipboard, real relay, real client):
powershell -ExecutionPolicy Bypass -File tests\e2e\run-end-to-end.ps1
```

The E2E script starts a relay with a generated token, starts the tray client, drives the real
Windows clipboard, and checks: upload, remote apply, **loop prevention**, 100 KB / 1 MB /
~2 MiB boundaries, Unicode round-trips. It restores any pre-existing `.env` / client config.

## 7. Debug workflow

1. Start relay → visit `/docs` or `curl http://127.0.0.1:8787/api/v1/health`.
2. Start client with a 1 s poll (`CLIPBRIDGE_POLL_INTERVAL_MS=1000`) for snappy round-trips.
3. Watch `services/relay/data/logs/relay.log` and `%APPDATA%\ClipBridge\client.log`.
   Content is never logged; you see clip ids, sizes, hash prefixes, statuses.
4. `Get-Clipboard` / `Set-Clipboard` in PowerShell 7 to drive the clipboard manually.
5. Manual clipboard integration check (documented, since OS clipboard is hard to unit-test):
   - set clipboard → expect `clip_uploaded` in client log + 201 in relay log;
   - POST from another device id → expect `clip_applied` in client log and `Get-Clipboard`
     shows the new text;
   - confirm no re-upload (no `clip_uploaded` for the same content within seconds).

## 8. Secret scan (pre-commit)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\secret-scan.ps1
git status && git diff --cached
```
