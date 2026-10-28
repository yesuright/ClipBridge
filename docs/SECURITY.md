# ClipBridge Security

This is a personal tool whose payloads can contain prompts, code, logs, private text.
The security baseline below describes v1.0.0 as implemented; deeper measures (E2E
encryption) are deliberate future work and are **not** implemented yet.

## 1. Threat model

In scope (what we protect against):

| Threat | Mitigation |
| --- | --- |
| Random/unauthenticated clients flooding or reading the relay | Shared secret bearer token; **all** `/api/v1` endpoints except `/api/v1/health` (liveness fields only, no user data) enforce it; constant-time comparison |
| Clipboard content leaking into logs | Logs contain only `clip_id`, `device_id`, size, hash prefix (8 chars), status — never content, never tokens; enforced by convention + tests |
| Secrets committed to Git | `.env`/`.env.*` gitignored (except `.env.example`); `scripts/secret-scan.ps1` pre-commit check |
| Huge payloads exhausting memory | Content limit 2 MiB (UTF-8 bytes) + HTTP body guard 16 MiB |
| Corrupt/tampered clips on the wire | Server recomputes SHA-256; client verifies hash before applying to clipboard |
| Clipboard churn/loops from own writes | Loop-prevention layers (see PROTOCOL.md §7) |

Assumed/declared out of scope:

- **Network attacker**: transport is plain HTTP on a trusted LAN (§4.2). Plaintext Internet
  transport is **not** a supported production mode — production or remote use MUST use
  HTTPS or an equivalent secure tunnel.
- **Relay server compromise**: the relay can read plaintext clip content today. It is trusted
  as an honest-but-curious storage node, not an E2E-protected one.
- **Malicious relay / MITM at HTTPS termination**: future E2E encryption.
- **Device theft**: nothing beyond OS-level protection.
- **Token leakage via shoulder-surfing configs**: the token is a file/env var, same trust as
  the OS account.

## 2. Token handling

- Standalone relay: `CLIPBRIDGE_TOKEN` env var or `services/relay/.env` (gitignored).
  **Fail-fast** when missing (the relay exits before binding) — there is no default secret
  and the relay never generates a token itself; `.env.example` shows how to create one
  (`python -c "import secrets; print(secrets.token_hex(32))"`).
- Packaged Windows client: on first start it **provisions its own access token
  automatically** — 256 bits from the OS CSPRNG (`ConfigBootstrap.cs`, `TokenGenerator.cs`),
  together with a device id and an mDNS hostname, written atomically to
  `%APPDATA%\ClipBridge\config.json` (gitignored — outside the repo by construction). Later
  starts restore those values unchanged; an existing valid config is never re-tokenized. A
  corrupt config is backed up and then replaced. The client refuses to run only when its
  configuration cannot be read or written — it never destroys a token it cannot see.
  `CLIPBRIDGE_DEVICE_TOKEN` (environment) overrides the stored token for one launch and is
  never persisted.
- Not allowed: hardcoded tokens in source, tokens in Git, tokens in logs, tokens in URLs
  (headers only, so they never appear in access logs/path lines).
- The supplied token in a failed auth attempt is never echoed back in the 401 body.

## 3. Logging & redaction

Relay (`relay/logging_setup.py`) and client (`FileClientLogger`) share the same rule:

> Never log the clipboard body; never log the token.

Safe fields: `clip_id`, `device_id`, `size`, `hash=$…first 8 hex…`, status code, event name.
Defense in depth: the client logger replaces `\r`/`\n` in every message so a buggy message
cannot smuggle multiline content into the log file. Log redaction is **tested**
(`tests/test_logging_redaction.py` on the relay side; log-content assertions in C# tests).

Dev note: relay access lines are uvicorn's default (method/path/status) — paths contain no
tokens because auth is header-based and clip content is in the body, never the URL.

## 4. Network: localhost vs LAN vs HTTPS

### 4.1 Two run modes, two defaults

ClipBridge's relay runs in one of two modes, with intentionally different defaults:

- **Standalone relay (development: `python run.py` in `services/relay/`)** — binds
  `127.0.0.1:8787` by default (`CLIPBRIDGE_BIND_HOST=127.0.0.1`; the early
  `CLIPBRIDGE_HOST` alias still works). Reachable from this machine only. In this mode
  loopback is the ONLY default — LAN exposure requires an explicit configuration change.
- **Managed relay (the packaged Windows client)** — the tray client starts the relay as a
  child process and sets `CLIPBRIDGE_BIND_HOST=0.0.0.0` for it (`RelayProcess.cs`), so
  iPhone/iPad Shortcuts on the same LAN can reach it. This is the shipped product's
  intended default: `0.0.0.0:8787`, or the next free port when 8787 is busy (persisted
  and logged; a tray notice appears when this happens while the app is running). By
  default the client itself talks to the relay over loopback (`http://127.0.0.1:<port>`;
  an explicit `CLIPBRIDGE_RELAY_URL` override can point it elsewhere).

Both modes enforce the same bearer-token auth and the same LAN warnings (§4.2).

### 4.2 LAN plain HTTP — trusted local-network, personal-use mode ONLY

iPhone/iPad Shortcuts need to reach the PC, so the managed relay binds `0.0.0.0` by design
and a standalone relay can be switched to `CLIPBRIDGE_BIND_HOST=0.0.0.0` (or a specific
private LAN interface). Positioning:

- The bearer token **travels unencrypted over plain HTTP** on your LAN. It is not
  transport-encrypted in any way. Anyone on the same network who can sniff frames
  (or who can reach the port) can read it and every clipboard text you sync.
- **Not suitable for untrusted Wi-Fi** (airport, hotel, café, shared networks) and
  **never suitable for the public Internet**.
- **Do NOT do router port forwarding or DMZ for this port.** Do NOT expose it outbound.
- Production or any remote usage MUST use HTTPS or an equivalent secure tunnel
  (TLS-terminating reverse proxy, VPN such as Tailscale/WireGuard, or SSH tunnel).
  The protocol is transport-agnostic: a `https://` base URL is a config change only.
- This LAN mode is **not** the final security story — it exists so a few personal
  devices can sync on one trusted Wi-Fi. See §7 for the E2E-encryption direction.
- The relay prints a prominent warning when it starts bound to a non-loopback
  address; the LAN helper scripts repeat it and never print the token.

### 4.3 The relay itself never modifies your network

ClipBridge does **not** create firewall rules, disable the firewall, touch Defender,
change router/Wi-Fi settings, or do port forwarding — by design. The LAN scripts
(`scripts/show-lan-setup.ps1`, `scripts/start-lan-dev.ps1`) only *detect* and *tell*
you, and print the exact manual step (as Administrator) if a rule is needed. If the
Windows firewall blocks the iPhone/iPad, the status is `MANUAL NETWORK STEP
REQUIRED` — the operator decides, not the tool.

```text
NOT SUPPORTED: plain-HTTP LAN is a local-network convenience, not an Internet model.
```

## 5. Clipboard sensitivity

- Only text/plain is read or written; images/files/HTML are ignored by design (no accidental
  exfil of rich content).
- The client writes remote clips into the system clipboard **immediately** — treat the clipboard
  as sensitive; do not run this tool on shared machines.
- Long prompts are stored server-side in plaintext (SQLite file). The DB is local-only;
  restrict file permissions / protect the disk as you would any personal data.

## 6. Known limitations

- Shared token for all devices — no per-device keys; anyone with the token can read/write all
  history. (Personal use, single trust boundary.)
- No rate limiting beyond the dedup window; no per-user quotas (personal relay).
- No audit trail/lockout for brute-forced auth attempts (token is high-entropy, so this is
  acceptable at 3 devices; the constant-time compare removes timing side channels).
- The relay stores plaintext clipboard content and it is not E2E encrypted.
- `origin`/`device_id` are client-declared strings — not authenticated identity.
- Windows clipboard is world-readable by design of the OS; any process can read what we write.
- LAN plain HTTP: no transport encryption (see §4.2) — acceptable only on a trusted
  private network, never the Internet.
- `GET /clips/latest?exclude_device_id=` trusts the caller to send the correct own
  device id — it is an ergonomic filter, not an identity check. A malicious client
  with the token can pass any id; the shared-token model already implies that trust.

## 7. Future direction: E2E encryption (design reserved, not implemented)

- Content would be encrypted client-side (e.g. XChaCha20-Poly1305 or AES-GCM) before POST;
  a new content type (`application/x-clipbridge-e2e`) with `key id` metadata.
- Relay would store opaque blobs; it keeps `clip_id`, `device_id`, sizes, hashes (of ciphertext)
  for routing/dedup but learns nothing about plaintext.
- Key distribution for 3 personal devices: manual export of a shared base key via Shortcuts /
  config file, or a simple PAKE pairing flow. This is **future** work — deliberately
  not implemented now to avoid a half-baked crypto scheme; today's plaintext LAN transport
  is documented and restricted to a trusted network.

## 8. Secret scanning

`scripts/secret-scan.ps1` checks the working tree for PEM blocks, `token=`/`password=`-style
assignments with long values, and inline `Bearer <long-token>` literals. Run it before commit:
`powershell -ExecutionPolicy Bypass -File scripts/secret-scan.ps1` (it exits non-zero on hits).

## 9. The shipped product (v1.0.0)

- **Artifact (no installer, no signing)**: the release ships as a self-contained archive
  (`ClipBridge-v1.0.0-win-x64.zip`: tray client, PyInstaller relay, documentation). It is
  unsigned — Windows SmartScreen may warn on first run ("More info → Run anyway").
  Signing is deliberate future work; do not bypass security warnings on untrusted copies.
- **Secret handling is unchanged**: the token still lives in
  `%APPDATA%\ClipBridge\config.json` (OS-user protected; never in the repo, never in logs).
  The tray passes it to the relay child **via the child's environment** (same trust level as
  the OS account; the environment of another process is not readable by other programs
  without admin). It is never put on a command line (visible via WMI to the same user),
  never logged.
- **mDNS advertisement**: the app announces `clipbridge.local` over UDP 5353 on the LAN and
  answers A-record queries for that name. It reveals ONLY the LAN hostname/address — the
  same information visible in `ipconfig`; no secrets, no clipboard data, no other hosts.
  Any device on the LAN may resolve it — same trust boundary as LAN mode itself
  (trusted private network only; see §4.2). The responder is in-app: no Bonjour/third-party
  daemon is installed, and it stops when the app exits. The name is only a convenience
  address — the API's protection (bearer token, plain-HTTP-LAN caveats) is unchanged.
- **`POST /api/v1/admin/shutdown`**: an authenticated internal stop switch for graceful
  shutdown by the tray client. It requires the same shared token — anyone who
  can already read/write clips can also stop the relay. No anonymous access, no effect on
  the stored data.
- **Startup registration**: user-level `HKCU\...\Run` only (no admin, reversible, per-user).
  Default OFF; explicit opt-in via tray menu or CLI.
- **Relay child lifecycle**: the relay runs as a child of the tray app with a Job Object
  (kill-on-close) — the OS guarantees no orphan process survives the client, even on a
  client crash. In managed mode the relay listens on `0.0.0.0:8787` (or the next free
  port; see §4.1) — trusted private network only (see §4.2). Do NOT forward 8787 at the
  router.

