# Changelog

All notable changes to ClipBridge are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). While the
project pre-dates its first public release, sections are keyed by date and the component
versions they shipped with (`apps/windows` client / `services/relay`).

## [Unreleased]

## [1.1.0] — 2026-10-31 (client 1.1.0 / relay 1.0.0)

### Fixed
- **Connection setup was effectively unreachable from the main window.** The Settings
  page hid the action inside a 260px muted `Authentication: Configured · Connection
  setup` label whose `AutoEllipsis` cut the call to action out of view at 100/125% DPI,
  and the Home page had no entry at all — users concluded the dialog could only be
  reached via PowerShell. The gray label is a plain status line again, and real
  **Connection setup** buttons now sit on the Home *iPhone connection* card and in the
  Settings *Data & diagnostics* row. Home, Settings and the existing tray entry
  (`Connection setup…`) open the same read-only dialog; opening it never regenerates
  the token.
- The *Connect your iPhone* dialog rendered at its 96-DPI size on 125/150% monitors
  (cramped fields, clipped Copy buttons). The dialog now scales its fixed pixels and
  client size with the monitor DPI; the 100% rendering is unchanged.

### Changed
- Windows client and test suite migrated from .NET 8 to **.NET 10 LTS** (`net10.0` /
  `net10.0-windows`). `global.json` now pins SDK **10.0.402** (`rollForward: latestFeature`)
  and `scripts/build-daily.ps1` enforces a **10.0.13** runtime floor, read back from the
  published `runtimeconfig.json` / `hostpolicy.dll` as before. Build and dev scripts probe
  candidate SDKs against `global.json` instead of preferring `%USERPROFILE%\.dotnet`, so a
  machine carrying both SDKs builds with .NET 10 deterministically. Product behaviour,
  protocol, storage and configuration formats are unchanged; the packaged relay (Python)
  is untouched.
- UI regression tests cover the new connection-setup entry points: real buttons on
  both pages firing the shared `ShowSetup` callback, entries usable without a LAN
  address, no masked token text in the main window, the dialog keeping the token
  masked by default and never rewriting the config, and the touched cards staying
  overlap-free at minimum window size
  (`apps/windows/tests/ClipBridge.Core.Tests/UI/ConnectionSetupTests.cs`).

## [1.0.0] — 2026-10-28 (client 1.0.0 / relay 1.0.0)

### Added
- First public open-source release: MIT license, public GitHub repository, packaged self-contained Windows build.
- Real-device acceptance (iPhone 13 Pro): bidirectional sync, 108 KB long text, Unicode/emoji, loop prevention, Wi-Fi reconnect recovery, relay restart recovery.

### Changed
- Product version aligned to 1.0.0 across the Windows client (was 0.4.0) and relay (was 0.2.0).
- Windows package now embeds the .NET 8.0.33 runtime (`Microsoft.NETCore.App` and
  `Microsoft.WindowsDesktop.App`) instead of 8.0.11. `global.json` pins SDK 8.0.427 and
  `scripts/build-daily.ps1` verifies the published package and fails if it would ship an
  older runtime.
- `scripts/build-daily.ps1` maps build paths to a neutral `/clipbridge` prefix
  (`-p:PathMap`) and no longer ships `*.pdb` files in artifacts, so distributed binaries
  do not embed the maintainer's Windows account name or folder layout.
- `README.md` rewritten for a public audience (English); the previous content moved to
  `README.zh-CN.md`. Added `CONTRIBUTING.md`, `SECURITY.md` (policy) and this changelog.
- Developer-specific strings (account name, machine paths) removed from tracked tests,
  scripts and docs; local agent working folders are excluded via local-only git config.

## [2026-10-27] — Portability hardening (client 0.4.0 / relay 0.2.0)

### Added
- First-run self-provisioning on an unfamiliar machine: CSPRNG 256-bit token, unique
  device id and mDNS hostname, atomic config write, *Connect your iPhone* setup dialog —
  no manual JSON editing, no admin rights.
- Port-occupied resilience: pre-bind probe, runtime fallback to the next free port,
  persisted with tray notice; busy-port is a clear failure with a dedicated relay exit
  code instead of a hang.
- Self-healing relay lifecycle (previous fix extended): unexpected exits recover with
  bounded restarts, escalating self-heal cooldown, no restart storms, no orphans
  (Job Object kill-on-close).
- LAN address selection ranked by default gateway / adapter type (VPN and virtual
  adapters excluded), optional `LanIpOverride`, per-adapter enumeration fault isolation.
- Stale-autostart repair after the install folder moves (only fixes the dead entry,
  never hijacks another live install).
- `--doctor` read-only diagnostics; `relay_component_missing` explicit state; health
  check now validates the relay's response payload, not just any 200.
- Cross-environment verification harness (`scripts/verify-portability.ps1`: fresh
  profiles, Unicode/space paths, busy port, second launch, corrupt config, missing
  component) and the portability audit document (`docs/PORTABILITY.md`).

### Fixed
- Corrupt config no longer silently loses the token: backup first
  (`config.corrupt-<timestamp>.json`), then regenerate, and tell the user where the
  backup is; unreadable configs are never overwritten.
- A hand-edited `RelayUrl` without scheme, a URL/port mismatch or an invalid device id
  normalize instead of crashing startup or failing every upload with 422.
- Packaged relay defaults its database/logs to the writable per-user directory instead of
  the (possibly read-only) install folder.
- A legacy `services/relay/.env` token is imported only inside a real source checkout —
  a distributed build can never pick up a developer's token from a nearby folder.

## [2026-09-10] — Relay crash recovery (client 0.4.0 / relay 0.2.0)

### Fixed
- The relay recovers after an unexpected exit (auto-restart with bounded budget and
  escalating self-heal cooldowns; permanent-cap retry instead of giving up).

## [2026-09-04] — First-sync reliability (client 0.4.0 / relay 0.2.0)

### Fixed
- First clipboard sync after launch: readiness is published on relay health transitions,
  so the engine no longer misses the first upload/poll window on cold start.

## [2026-09-03] — Native UI + daily-use productization (client 0.4.0 / relay 0.2.0)

### Added
- Native Windows UI: Home / History / Settings pages, System/Light/Dark themes, history
  reader with safe copy, original multi-size icon, PerMonitorV2 DPI.
- Tray productization: single instance (mutex + named pipe), relay child managed inside a
  Job Object (kill-on-close), optional per-user start-with-windows (`HKCU Run`, off by
  default), `--status` / `--exit` / `--doctor` CLI surface, tray notices for LAN changes.
- Relay packaged with PyInstaller (`relay\ClipBridgeRelay.exe`) — end users need no
  Python; client published self-contained — no .NET install.
- Authenticated `POST /api/v1/admin/shutdown` for graceful shutdown.
- Bounded upload retries with backoff; four-layer clipboard loop prevention documented
  and tested.

## [2026-09-02] — iOS Shortcuts sync (client 0.1.0 / relay 0.2.0)

### Added
- Apple Shortcuts integration: simplified `POST /api/v1/clips/shortcut` where the relay
  generates `clip_id` / `content_hash` / `created_at` (Shortcuts cannot compute them
  natively); `exclude_device_id` filtering on `GET /api/v1/clips/latest` and
  `GET /api/v1/clips`; setup guides under `shortcuts/`.

## [2026-09-02] — MVP (client 0.1.0 / relay 0.1.0)

### Added
- Windows clipboard watcher + polling sync engine (C#/.NET 8, event-driven listener) and
  a local FastAPI + SQLite relay with bearer-token auth, 2 MiB UTF-8 text limit,
  50-item history, SHA-256 content hashing and a same-device dedup window.
- Full-Unicode round-trip support (emoji, CJK, quotes, backslashes, tabs, CRLF).
- End-to-end test harness driving the real Windows clipboard and simulating phone flows;
  secret-scan pre-commit check; log redaction enforced and tested from day one.
