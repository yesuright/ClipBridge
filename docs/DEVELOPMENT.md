# ClipBridge Development

## 1. Prerequisites

| Tool | Version | Where |
| --- | --- | --- |
| Git | 2.x | any |
| .NET SDK | 8.0 (LTS) | https://dotnet.microsoft.com/download (or `dotnet-install.ps1 -Channel 8.0 -InstallDir $env:USERPROFILE\.dotnet`; `scripts/dev-client.ps1` and `tests/e2e/run-end-to-end.ps1` automatically prefer `%USERPROFILE%\.dotnet\dotnet.exe`) |
| Python | 3.12+ (3.13 tested) | https://www.python.org |

> Network note: if nuget.org is slow on your network (common in CN), the repo ships a
> **repository-local** `NuGet.Config` at the repo root that adds a working mirror
> (Huawei: `https://repo.huaweicloud.com/repository/nuget/v3/index.json`) for
> ClipBridge projects **only**. The user-level NuGet config (`%APPDATA%\NuGet\NuGet.Config`)
> stays untouched by the repo and was restored to its original nuget.org-only state
> in Milestone 2 — this repo does not change global NuGet behaviour. The test project
> restores ~25 MB on first build; everything is cached afterwards. (If a previous
> run of this repo added a `huawei` source to the user-level config, it is safe to
> delete that line; the backup lives at `%APPDATA%\NuGet\NuGet.Config.bak-clipbridge-m2`.)

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
| `CLIPBRIDGE_BIND_HOST` | `127.0.0.1` | bind address; **LAN mode** for iPhone/iPad Shortcuts: `0.0.0.0`. `CLIPBRIDGE_HOST` (M1 name) still works as an alias. Loopback-only is the safe default — LAN exposure is opt-in |
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

## 5b. LAN mode for iPhone/iPad (Milestone 2)

One command starts everything for LAN testing:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\start-lan-dev.ps1
```

This script (no firewall changes, no public exposure, no token printed):

1. checks the relay venv and `services\relay\.env` (must define `CLIPBRIDGE_TOKEN`),
2. starts the relay bound to `0.0.0.0` in this script session (your shell env is not
   modified persistently),
3. builds + starts the Windows tray client pointed at `http://127.0.0.1:<port>`,
4. prints the suggested LAN URL(s) (from `scripts/lib/LanSetup.psm1`) and the security
   notice. Ctrl+C stops both.

Helpers:

| Script | Purpose |
| --- | --- |
| `scripts/show-lan-setup.ps1` | read-only: suggests LAN URL(s), checks the listener bind address, reports whether the firewall may block inbound, prints the manual firewall step (never executes it) |
| `scripts/start-lan-dev.ps1` | one-shot LAN dev starter (relay + client) |
| `scripts/tests/test-lan-setup.ps1` | unit tests for the LAN helper logic (private-IP/VPN/virtual filtering), no Pester needed |
| `scripts/dev-relay.ps1` | localhost-only relay dev runner (unchanged from M1) |
| `scripts/dev-client.ps1` | client dev runner (unchanged from M1) |

The LAN helper prioritises private IPv4 addresses and excludes loopback, APIPA,
disconnected adapters, VPN tunnels and virtual adapters (heuristics — it lists every
candidate and never silently picks). **It never prints the bearer token.**

To test from the phone: find the LAN URL, then follow `docs/DEVICE_TEST.md`.

## 6. Tests

```powershell
# Relay (pytest): auth / CRUD / history / validation / long text / unicode / dedup /
#   redaction / shortcut-minimized create / exclude_device_id filtering
cd services\relay
.\.venv\Scripts\python -m pytest

# Windows Core (xunit): hashing / caches / trackers / sync engine / relay client /
#   config / logging / device routing
dotnet test apps\windows\ClipBridge.sln

# LAN helper logic (Windows PowerShell or PowerShell 7):
powershell -ExecutionPolicy Bypass -File scripts\tests\test-lan-setup.ps1

# Full-stack E2E on THIS machine (real clipboard, real relay, real client):
powershell -ExecutionPolicy Bypass -File tests\e2e\run-end-to-end.ps1
```

The E2E script starts a relay with a generated token, starts the tray client, drives the real
Windows clipboard, and checks: upload, remote apply, **loop prevention**, 100 KB / 1 MB /
~2 MiB boundaries, Unicode round-trips, plus the simulated iPhone/iPad Shortcut flows
(simplified POST endpoint + `exclude_device_id` GET semantics). It restores any
pre-existing `.env` / client config.

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
