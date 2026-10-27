# ClipBridge

**Clipboard sync between your Windows PC and your iPhone — plain text, local network, no cloud.**

ClipBridge moves text between the Windows clipboard and the iOS clipboard (via Apple
Shortcuts) over your own Wi-Fi: long AI prompts, log excerpts, code snippets, links,
notes. Copy on one device, paste on the other. Nothing leaves your network, no account
is required, and there is no third-party service in the middle.

```text
Windows  Ctrl+C ──►  ClipBridge client ──► local Relay ──► SQLite
iPhone "Get from ClipBridge" ─────────────► local Relay ──► Windows clipboard (a few seconds)

iPhone copy ──► "Send to ClipBridge" ──► local Relay ──► Windows clipboard (a few seconds)
```

- **Windows → iPhone**: copy on Windows, tap the *Get from ClipBridge* shortcut on the
  phone — the text lands in the iOS clipboard.
- **iPhone → Windows**: copy on the phone, tap the *Send to ClipBridge* shortcut — the
  text appears on the Windows clipboard within seconds.

Both directions run through one small local relay that ships with the Windows app. The
iPhone side needs only the built-in **Shortcuts** app — no third-party app, no login,
and the address/token are entered once per shortcut.

---

## Why

Chat apps (WeChat, WhatsApp, Telegram, "File Transfer" helpers) are the usual bridge for
getting text from a phone to a PC. That means your prompts, logs and code pass through
someone else's servers, get mixed into chat history, and often get truncated or
reformatted. ClipBridge keeps the same workflow but keeps the data on your own devices:

- **Local only** — the relay runs on your PC; traffic never leaves your LAN.
- **Byte-exact** — Unicode, emoji, quotes, backslashes, tabs, CRLF and ~100 KB prompts
  survive the trip unchanged (up to 2 MiB per clip).
- **Zero-config daily use** — double-click one exe; the relay is a managed child process.

## Features

- Automatic upload of Windows clipboard changes; automatic poll (default every 2 s) for
  text sent from the phone.
- Two Apple Shortcuts (`Send to ClipBridge` / `Get from ClipBridge`), ~5–9 actions each,
  built with the built-in Shortcuts editor. You never re-enter the URL or token.
- First run provisions everything: a 256-bit random access token, a device id, and a
  *Connect your iPhone* window that shows the exact address and token to paste into the
  shortcuts.
- Tray-resident Windows app with a native UI (Home / History / Settings), System/Light/Dark
  themes, pause/resume, "check now", history browser with safe copy, start-with-Windows
  (per-user, off by default).
- Self-healing runtime: the relay restarts with bounded backoff after a crash, survives
  sleep/wake and network changes, and frees itself from a busy port by falling back to
  the next free port (persisted, with a tray notice).
- Portable paths: config in `%APPDATA%\ClipBridge`, data in `%LOCALAPPDATA%\ClipBridge`;
  nothing depends on the install location, the user name, or the working directory.
  Paths with spaces and Chinese characters are covered by tests.
- Loop protection at four layers so your own copies never bounce back as new clips.
- Diagnostics: `ClipBridge.exe --doctor` (read-only), `--status`, JSON status line, and
  logs that never contain clipboard content or tokens.

**Not supported (by design, today):** images, files, rich text/HTML, binary formats;
public-internet deployment; HTTPS; end-to-end encryption; an installer; auto-updates;
code signing; an offline sync queue. See [Known issues](#known-issues) and
[docs/SECURITY.md](docs/SECURITY.md).

## System requirements

| Component | Requirement |
| --- | --- |
| Windows PC | Windows 11 (developed and tested on Windows 11; Windows 10 64-bit expected to work but untested) |
| Runtime | None to install — the build is self-contained (no .NET or Python install needed) |
| iPhone / iPad | Any device that can run the built-in Apple **Shortcuts** app |
| Network | PC and phone on the **same trusted Wi-Fi/LAN**, IPv4; router must not have AP isolation enabled |
| Privileges | No administrator rights needed (per-user autostart, per-user data) |

## Installation (Windows)

1. Download the release archive from the
   [Releases](../../releases) page and unzip it anywhere — for example
   `D:\Tools\ClipBridge\`. (The folder can contain spaces; you do not need to install it.)
2. Double-click **`ClipBridge.exe`**. That single exe starts the relay child process and
   the tray app.
3. If Windows Defender Firewall asks, click **Allow** — for **private networks only**.
   ClipBridge never edits firewall rules itself and never forwards ports.
4. A first-run window *Connect your iPhone* appears with the LAN **address** and the
   **access token**. Keep it open and set up the phone (next section).

> The exe is not code-signed yet, so SmartScreen may show "Windows protected your PC" —
> *More info → Run anyway*. Verify what you download against the release checksum
> (published with each release).

## Connecting your iPhone (Apple Shortcuts)

Create two shortcuts in the built-in Shortcuts app. You need the **address** and
**token** from the *Connect your iPhone* window (re-open it any time: tray icon →
*Connection setup…*).

**Shortcut 1 — "Send to ClipBridge"** (iPhone → Windows), 5 actions:

1. **Text** — the relay address, e.g. `http://192.168.1.14:8787` (no trailing `/`)
2. **Text** — the access token (the 43-character string only; no `Bearer ` prefix)
3. **Text** — this device's id, e.g. `iphone` (letters/digits/`-`/`_`)
4. **Get Clipboard**
5. **If** Clipboard has any value → **Get Contents of URL**:
   - Method **POST**, URL `[address]/api/v1/clips/shortcut`
   - Headers: `Authorization` = `Bearer ` + token variable; `Content-Type` = `application/json`
   - Request Body: **JSON dictionary** with keys `device_id` ← text-3 and `content` ← Clipboard
   - (*Use the dictionary editor — do not hand-build a JSON string, or quotes/emoji will break.*)
   - Otherwise → Notification "Clipboard is empty"

**Shortcut 2 — "Get from ClipBridge"** (Windows → iPhone), ~9 actions:

1. **Text** — the relay address (same as above)
2. **Text** — the access token
3. **Text** — this device's id, e.g. `iphone`
4. **URL** — `[address]/api/v1/clips?limit=10&exclude_device_id=iphone`
5. **Get Contents of URL** (GET) — header `Authorization` = `Bearer ` + token variable
6. **Get Dictionary from Input** → **Get Value for Key** `clips`
7. **If** `clips` has any value → **Get Item from List** (first item) →
   **Get Value for Key** `content` → **Copy to Clipboard**
   - Otherwise → Notification "No new content"
8. Optional: Notification with `size_bytes` / `device_id`
9. Add both shortcuts to the Home Screen or Action button.

> The `exclude_device_id` filter is what prevents you from copying back the text you just
> sent. A detailed step-by-step guide with screenshots-style examples lives in
> [shortcuts/SEND_TO_CLIPBRIDGE.md](shortcuts/SEND_TO_CLIPBRIDGE.md) and
> [shortcuts/GET_FROM_CLIPBRIDGE.md](shortcuts/GET_FROM_CLIPBRIDGE.md) (Chinese, English
> translation in progress).

## First run, step by step

1. **Start** — double-click `ClipBridge.exe`. The main window opens; the app stays in the
   tray when closed.
2. **Connect your iPhone** — appears automatically on first start. It shows:
   - **Address** — the current LAN address, e.g. `http://192.168.1.14:8787`
   - **Access token** — this install's random token (Show / Copy buttons)
3. **Set up the two shortcuts** on the phone with those two values (section above).
4. **Test both directions**:
   - Copy text on Windows → on the phone tap *Get from ClipBridge* → paste anywhere.
   - Copy text on the phone → tap *Send to ClipBridge* → within a few seconds `Ctrl+V`
     on Windows pastes it.
5. **Optional** — Settings → *Start with Windows* to launch into the tray at login.

The address comes from your PC's current LAN IP. If your router hands out new IPs
(DHCP), re-open *Connection setup…* and update the address in both shortcuts — the token
never changes.

## Daily use

| You want | You do |
| --- | --- |
| Send the PC clipboard to the phone | Copy on Windows → tap **Get from ClipBridge** on the phone |
| Send the phone clipboard to the PC | Copy on iPhone → tap **Send to ClipBridge** → `Ctrl+V` on Windows |
| Pause sync (e.g. during a screen share) | Tray icon → *Pause sync* (Settings → *Resume sync*) |
| Read recent history | Tray → Home → History (last 50 clips, tap to read, *Copy text* to re-copy) |
| Check the current address | Home → *iPhone connection* → *Copy address* |
| Really quit | Tray icon → *Exit ClipBridge* (the relay child exits with it) |

Closing the window only hides it — sync keeps running in the tray.

## How it works

```text
┌────────────────────────── Windows ──────────────────────────┐      ┌─────────┐
│                                                             │      │ iPhone  │
│  ClipBridge.exe (tray, .NET 8, self-contained)              │      │         │
│   ├── clipboard listener → POST /api/v1/clips               │      │Shortcut │
│   ├── poll /api/v1/clips/latest (2 s) → write clipboard     │      │  Send   │
│   └── manages ──► relay\ClipBridgeRelay.exe (PyInstaller)   │      │  Get    │
│                     FastAPI + SQLite, 0.0.0.0:8787  ◄───────┼──────┴─────────┘
│                     latest clip + 50-item history           │   same trusted LAN,
│   Job Object: relay always dies with the client             │   Bearer token auth
└─────────────────────────────────────────────────────────────┘
```

- The **relay** is a local HTTP service (FastAPI + SQLite). It stores the latest clip and
  the last 50 clips. Every `/api/v1/*` endpoint requires the shared bearer token.
- The **client** watches the Windows clipboard (event-driven), uploads new text, and polls
  for incoming text, filtering out its own uploads and anything it just applied.
- The **shortcuts** call the same relay: `POST …/clips/shortcut` sends the phone clipboard;
  `GET …/clips?exclude_device_id=…` fetches the newest text from other devices. The relay
  generates ids, timestamps and the SHA-256 content hash, because Shortcuts cannot compute
  them natively.
- **Loop prevention** (why your own copies don't echo forever): (1) the client marks every
  applied clip and ignores the same content hash for 30 s; (2) poll results exclude the
  device's own uploads (900 s memory); (3) the relay rejects identical re-posts from the
  same device within a 15 s window (`409 duplicate_content_in_window`); (4) clip ids are
  idempotent (`409 duplicate_clip_id`).

Protocol details: [docs/PROTOCOL.md](docs/PROTOCOL.md) · architecture:
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · lifecycle:
[docs/LIFECYCLE.md](docs/LIFECYCLE.md).

## Security & privacy

ClipBridge is designed for personal use across devices you own, on a network you trust.
The full threat model and rationale live in [docs/SECURITY.md](docs/SECURITY.md). The
short version:

- **No cloud, no telemetry, no accounts.** The only network peer is the relay running on
  your own PC. Traffic stays on your LAN.
- **Authentication.** Every API call carries a 256-bit random bearer token (generated
  per install), compared in constant time. Without the token, an attacker on the LAN can
  do nothing but see that the port is open.
- **Transport is plain HTTP inside your LAN.** The token and clipboard text are *not*
  encrypted in transit. This is acceptable on a trusted home/office Wi-Fi and is the
  documented trade-off of this release; never use it on public Wi-Fi, never forward the
  port at the router. HTTPS / VPN-tunnel deployment is a documented, supported
  configuration change for later milestones.
- **Logs are redacted by construction and by test.** Neither the relay nor the client
  ever writes clipboard content or the token to disk — only ids, sizes, 8-char hash
  prefixes and status codes.
- **Storage.** Clipboard text sits in a local SQLite database
  (`%LOCALAPPDATA%\ClipBridge\clipbridge.db`); the token sits in
  `%APPDATA%\ClipBridge\config.json` — both outside the app folder, protected only by
  your Windows account. Treat them like any other personal file.
- **The token is shared** by all your devices (single trust boundary). Anyone who has it
  can read and write your clip history — keep it out of chat, screenshots and repos.
- **Least privilege.** No admin rights, no services, no drivers, no firewall/VPN/proxy
  changes — ClipBridge never modifies your system security or network settings.
- **Child process safety.** The relay runs as a child of the tray app inside a Job Object
  (kill-on-close), configured via environment variables — the token is never on a command
  line (where other local processes could read it).

Vulnerability reports: see [SECURITY.md](SECURITY.md).

## Known issues

1. **Local requests can be hijacked by HTTP proxies in some shells** (open, decision
   deferred). If a Windows process is started from a shell that sets `HTTP_PROXY` /
   `HTTPS_PROXY` and does *not* exclude `127.0.0.1` in `NO_PROXY`, .NET may route
   ClipBridge's loopback requests through that proxy. The proxy does not know your local
   relay, so polling and uploads fail with `relay_404` while health may still look OK.
   - **Impact**: sync stops in that specific shell/session. Real-world daily use with the
     tray app (started from Explorer) is not affected — verified: the daily instance's
     logs show zero `relay_404` while proxies are enabled on the test machine.
   - **Why**: ClipBridge intentionally uses .NET's default proxy handling and does not
     special-case loopback addresses; changing this is a documented open decision
     ([docs/PORTABILITY.md §3.6](docs/PORTABILITY.md)).
   - **Workaround** (choose one, per affected shell): add loopback to `NO_PROXY`
     (`NO_PROXY=localhost,127.0.0.1`), or configure your proxy client to bypass local
     addresses, or launch the app from Explorer/Start Menu instead of that shell.
   - **Risk worth knowing**: a mis-routed request would carry clipboard text and the
     bearer token to the proxy process. That is one more reason to only point ClipBridge
     at relays you control.
   - ClipBridge **never** changes your system proxy, VPN or firewall settings — fixing the
     environment is a user decision, and a future opt-in "force loopback direct" switch
     is the reserved fix (not implemented in this release).

2. **Unsigned binaries** — SmartScreen/Defender may warn on first run. Expected until
   code signing is in place (see the roadmap in the release notes).

3. **`clipbridge.local` (mDNS) is experimental** — some iPhones fail to resolve it.
   Use the plain IP address from *Connection setup…*; mDNS stays off the critical path.

4. **LAN IP changes require a shortcut update** — DHCP renewals or Wi-Fi switches change
   the PC address; update both shortcuts (the token stays valid). The tray shows a notice
   when the address changes.

5. **Port changes when 8787 is busy** — if another program owns the port, ClipBridge
   moves to the next free port and tells you; update the port in both shortcuts.

6. **No offline queue** — if the phone or the PC is unreachable when you send, that clip
   is not queued; send it again after the connection returns (uploads retry 3× with
   backoff before giving up).

7. **Clipboard contention** — if another program holds the Windows clipboard open, the
   client retries a few times and then skips that copy (logged, never crashes).

## Troubleshooting

First run the read-only doctor: `ClipBridge.exe --doctor` (paths, config state, relay
component, port bindability, LAN address, autostart health — prints a token fingerprint,
never the token).

| Symptom | What to check / do |
| --- | --- |
| Phone can't reach `http://<PC-IP>:8787/api/v1/health` (Safari) | Same Wi-Fi? PC IP changed (re-open *Connection setup…*)? Phone off Wi-Fi? AP isolation on the router? |
| Connection refused | Relay not running — start `ClipBridge.exe`; check tray state; `--doctor` |
| Timeout but internet works on the phone | Windows Firewall blocked inbound — allow `ClipBridgeRelay.exe` on **private** networks; confirm the Windows network profile is *Private*, not *Public* |
| Shortcut gets **401** | Token mismatch — re-copy the token from *Connection setup…* (watch for stray spaces/newlines in the shortcut) |
| *Get* returns **404 not_found** | Normal empty state: no *other-device* text exists yet — send one from the PC first |
| *Send* returns **409** | Same text re-sent within the 15 s dedup window — treat as already-sent |
| **413** | Over the 2 MiB per-clip limit — split the text |
| Phone connects to nothing after a VPN/dock change | The auto-picked LAN adapter changed; set the address manually or configure `LanIpOverride` |
| Sync stuck "Offline" | Wait 1–5 min (auto-retry + self-heal), or Settings → *Restart relay*; then check logs |
| "Relay component missing" | The `relay\` folder was not copied with `ClipBridge.exe` — re-extract the full folder |
| Where are the logs? | `%LOCALAPPDATA%\ClipBridge\logs\` (client + relay; ids/sizes/hashes only, never content or tokens) |

## Building from source

Prerequisites: **.NET 8 SDK** and **Python 3.12+** (3.13 tested). Windows PowerShell 5.1
is the tested shell for the scripts.

```powershell
# 1. Relay (dev run, loopback only)
cd services\relay
python -m venv .venv
.\.venv\Scripts\pip install -r requirements.txt
Copy-Item .env.example .env          # set CLIPBRIDGE_TOKEN (required, fail-fast)
.\.venv\Scripts\python run.py        # http://127.0.0.1:8787 (Swagger at /docs)

# 2. Windows client (dev run)
dotnet run --project apps\windows\src\ClipBridge.Win

# 3. Release artifact (self-contained client + PyInstaller relay + README)
powershell -ExecutionPolicy Bypass -File scripts\build-daily.ps1 -OutputDir artifacts\ClipBridge
```

`scripts/dev-relay.ps1`, `scripts/dev-client.ps1` and `scripts/start-lan-dev.ps1` (LAN
mode with explicit security notice) wrap the dev flows. Environment variables and storage
overrides (`CLIPBRIDGE_HOME`, …) are documented in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

### Tests

```powershell
# Relay unit/integration tests (pytest)
cd services\relay && .\.venv\Scripts\python -m pytest && cd ..\..

# Windows core tests (xunit)
dotnet test apps\windows\tests\ClipBridge.Core.Tests

# Packaged-artifact smoke test (real processes, isolated HOME, no admin)
powershell -ExecutionPolicy Bypass -File scripts\verify-daily.ps1 -Artifact artifacts\ClipBridge

# Cross-environment portability harness (fresh config, Unicode paths, busy port,
# second launch, corrupt config, missing component)
powershell -ExecutionPolicy Bypass -File scripts\verify-portability.ps1 -Artifact artifacts\ClipBridge

# Pre-commit secret scan
powershell -ExecutionPolicy Bypass -File scripts\secret-scan.ps1
```

iPhone end-to-end behaviour on real hardware is verified manually with
[docs/DEVICE_TEST.md](docs/DEVICE_TEST.md) (Chinese).

## Project layout

```text
apps/windows/        C# .NET 8 tray client (ClipBridge.Core + ClipBridge.Win + xunit tests)
services/relay/      Python FastAPI relay (SQLite, PyInstaller-packaged) + pytest suite
shortcuts/           Apple Shortcuts setup guides (Send / Get)
docs/                architecture, protocol, security, development, daily-use, portability
scripts/             build / verify / dev helpers (Windows PowerShell)
tests/e2e/           full-stack end-to-end script (real clipboard + simulated phone flows)
```

## Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for the
build/test checklist. Please keep the security invariants (no tokens/clipboard content in
logs, no new outbound services) and run the test suites before submitting.

## License

Released under the [MIT License](LICENSE) — Copyright (c) 2026 yesuright. Third-party
dependencies remain under their own licenses; see the dependency manifests
(`apps/windows/src/**/*.csproj`, `services/relay/requirements.txt`) for the lists.

---

**Status**: the code, tests and release build are complete and this repository is ready
for its first public release.
