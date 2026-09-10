# ClipBridge Process & Lifecycle (Milestone 3)

## 1. Process model (daily-use artifact)

```text
Windows login (Startup: HKCU Run, user-level, optional)
   └─ ClipBridge.exe                    (tray client, .NET 8 self-contained, GUI subsystem)
        ├─ ClipBridgeRelay.exe          (packaged relay, PyInstaller onedir, child process)
        │     └─ uvloop/lifespan        (runs in-process inside ClipBridgeRelay.exe)
        └─ (in-proc) Sync engine, clipboard listener, mDNS responder, network monitor
```

- **Two processes only.** No console/PowerShell, no separate Python installation at runtime,
  no service host. The relay is always a child of the tray client in the daily artifact.
- **Development mode** is unchanged: relay and client can run separately from the repo
  (`scripts/dev-relay.ps1` + `scripts/dev-client.ps1`, or `start-lan-dev.ps1`). When no
  packaged relay sits next to the client exe, the client detects it and runs in
  "external relay" mode (it still syncs; it just does not manage the relay).

## 2. Parent/child lifecycle guarantees

| Mechanism | Effect |
| --- | --- |
| **Job Object (kill-on-close)** | The OS ends the relay child automatically whenever the tray client exits — normal exit *or* crash. This is the no-orphan guarantee. |
| Graceful shutdown channel | `POST /api/v1/admin/shutdown` (Bearer-token guarded) lets the relay drain and exit cleanly so SQLite closes properly on *normal* exit. |
| Bounded restart policy | A relay crash triggers up to **3 restarts** with exponential backoff (1 s / 2 s / 4 s). A stable run of ≥ 60 s resets the budget. Rapid crash loops stop at 4 attempts (`Failed(restart_limit)`, logged as `relay_restart_exhausted`). |
| Exhaustion is never terminal | After the budget is exhausted the manager waits a cool-down and starts a **fresh budget** by itself: 30 s → 60 s → 120 s → 300 s, then capped at 300 s (`relay_recovery_attempt round=N`). A transient failure therefore heals with no user action, while a permanently broken relay retries at most once per 5 minutes — never a restart storm. Reaching `Running` clears the escalation. |
| Port 8787 conflict | Detected before start (loopback TCP probe + health check). `Failed(port_in_use)` — never a silent port switch, because the iPhone Shortcut pins 8787. |
| Single instance | Per-user named mutex + named pipe. Second launch signals the running instance (tray balloon) and exits. Never two relays, two listeners, two binds. |

## 3. Startup sequence

```text
ClipBridge.exe
  1. parse CLI (--status / --exit / --autostart-* / --tray-autostart)
  2. single-instance guard: second instance → signal → exit
  3. load config (%APPDATA%\ClipBridge\config.json; env > file > defaults)
     - missing token → try legacy migration from services\relay\.env → else first-run dialog
  4. rotating logger at %LOCALAPPDATA%\ClipBridge\logs\client.log
  5. start components:
     - relay manager: health-wait loopback (≤ 20 s) → Running / Failed
     - sync engine: 2 s polling + clipboard listener
     - network monitor: event-driven (NetworkChange) — first evaluation now
     - mDNS responder: probe (conflict?) → announce clipbridge.local
  6. tray icon appears; status model publishes Running
```

## 4. Shutdown sequence (tray → Exit)

```text
set App=Stopping
  1. sync engine stops (polling cancelled, listener detached, caches flushed)
  2. relay: POST /api/v1/admin/shutdown → wait ≤ 5 s → hard kill if needed
  3. relay manager disposed → Job Object closed (kill-on-close, final guarantee)
  4. mDNS goodbye (TTL=0), network monitor unsubscribed
  5. tray icon removed, logs flushed, mutex released
```

## 5. Crash scenarios

| Scenario | Behaviour |
| --- | --- |
| Relay child crashes | Client keeps running; restart with backoff (≤ 3 attempts); log lines `relay_exited pid=… code=… uptime_ms=… exited_at=…` / `relay_restarting attempt=N`; state `Restarting` → `Running` or `Failed`. |
| Relay in restart storm | 4th consecutive crash → `Failed(restart_limit)` + `relay_restart_exhausted attempts=… last_exit_code=… last_error=… next_retry_in_ms=…`, one tray warning balloon per failure episode, **Restart Relay** menu item enabled. The manager then retries on the escalating cool-down (`relay_recovery_attempt`) until the relay runs again, so the app can never stay `Offline` forever. |
| Relay child alive but never healthy | `relay_failed reason=startup_timeout … listening=<bool> exited=<bool>` records whether the child ever bound the port. `exited=false` + `listening=false` after several rounds means something on the machine is intercepting loopback traffic to 8787 (local firewall/VPN/proxy), **not** that the relay failed to start. |
| Client crashes (hard kill) | Job Object closes → OS kills the relay child. No orphan, port released. |
| Port 8787 taken by another app | `Failed(port_in_use)` + log; user acts (close the other app), tray **Restart Relay** retries. |
| Unknown remote relay on healthy port | Treated as external relay (Running) and logged — sync continues against it. |

## 6. Sleep / wake

- `WM_POWERBROADCAST` (resume) triggers a network re-check + immediate sync — no restart
  of the app, no restart of the relay. The relay socket survives sleep on Windows; after
  resume the client re-establishes everything within one poll cycle.
- No tight loops: poll interval stays fixed; network monitor is event-driven.

### Manual sleep/wake checklist (machine-level test, ~1 min)

1. Copy text on Windows (confirm `clip_uploaded` in the log).
2. Sleep the PC (Start → Power → Sleep). Wait ~30 s for full sleep.
3. Wake the PC. Within ~5 s the tray icon is back; no error balloon about the relay.
4. Send a clip from the iPhone (Send to ClipBridge) → it lands in the Windows clipboard.
5. Copy text on Windows → it appears on the iPhone (Get from ClipBridge).
6. Check the log: expect `resume_from_sleep detected …`, then normal
   `clip_applied` / `clip_uploaded` lines — no restarts of either process.

## 7. Network change handling

- `NetworkChange.NetworkAddressChanged` / `NetworkAvailabilityChanged` (event-driven,
  never per-second enumeration).
- On change: LAN address re-resolved (physical adapter heuristics, VPN/virtual excluded),
  mDNS re-announced with the new IP, status updated (`LanAvailable` / `NoLan` / `IpChanged`),
  a `CheckNow` is triggered so sync resumes without manual action.
- If the *primary LAN URL changed*, the tray balloons the new URL (Shortcut may need the
  update) and logs `network_ip_changed old=… new=…` (URLs only — no secrets).

## 8. Status model

All state flows through `ClipBridge.Core.Status.AppStatusModel` (immutable snapshots +
`Changed` event): `AppState`, `RelayState` (Starting/Running/Restarting/Failed/Stopped/Disabled),
`SyncState` (Active/Paused/Offline), `NetworkState`, `MdnsState`, LAN URL, last-sync info,
startup flag. The tray renders the snapshot; a future UI consumes the same model —
it never parses log strings.

## 9. Data locations

| Kind | Path |
| --- | --- |
| Config + secrets (single file) | `%APPDATA%\ClipBridge\config.json` (unchanged from M2 — existing tokens keep working) |
| Client log (rotating, ≤ 6 MB) | `%LOCALAPPDATA%\ClipBridge\logs\client.log(.1…5)` |
| Relay log / relay console output | `%LOCALAPPDATA%\ClipBridge\logs\relay.log`, `relay-console.log` |
| SQLite history DB | `%LOCALAPPDATA%\ClipBridge\clipbridge.db` |
| Legacy repo files | untouched: `services\relay\.env` and old `data\` remain for development use |
