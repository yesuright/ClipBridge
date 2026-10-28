# Security Policy

ClipBridge is a local-network clipboard sync tool. This page explains how to report
vulnerabilities and summarizes the security model. The full threat model, design
rationale and boundary conditions live in [docs/SECURITY.md](docs/SECURITY.md).

## Supported versions

| Version | Supported |
| --- | --- |
| latest release / main branch | yes |
| older tags | no |

## Reporting a vulnerability

Please do **not** open a public issue for security problems.

Use GitHub's *Report a vulnerability* (Security → Advisories → **Report a vulnerability**)
for this repository. Include: affected component (client / relay / shortcuts), the
scenario (local user, LAN peer, malicious relay…), and a reproduction or PoC. You will
get an acknowledgement and, for accepted issues, a fix timeline; credit is given unless
you prefer otherwise.

## Security model in one minute

- **Auth**: every `/api/v1/*` endpoint requires a 256-bit random bearer token generated
  per install, compared in constant time. The only unauthenticated endpoints are
  `/api/v1/health` (a status string, no user data), the root `/` and the Swagger UI at
  `/docs` (API shape only).
- **Transport**: plain HTTP on your LAN. The token and clipboard text are not encrypted
  in transit. This is the documented trade-off of the current release — trusted private
  networks only, never the public internet, never router port-forwarding.
- **Logs**: the relay and the client never write clipboard content or tokens to disk
  (enforced by convention and covered by tests); only ids, sizes, 8-char hash prefixes
  and status codes appear.
- **Process model**: the relay is a child of the tray client inside a Job Object
  (kill-on-close); it receives the token via environment variables, never via command
  line or config files inside the install folder.
- **System changes**: ClipBridge never modifies firewall, proxy or VPN settings and never
  forwards ports.
- **No telemetry**: neither component ships an analytics, crash-reporting or telemetry SDK,
  and the only outbound HTTP requests go to the relay address you configured (loopback or
  your own LAN). There is no update check, no usage reporting and no third-party endpoint.
- **Data at rest**: clipboard text is stored unencrypted in a local SQLite file under the
  user's profile; the token sits in the user's config file. Both rely on standard
  Windows account isolation.
- **Out of scope (current release)**: end-to-end encryption, HTTPS transport, per-device
  keys, malicious-relay protection. These are documented future work in
  [docs/SECURITY.md](docs/SECURITY.md).

## Known limitations worth reviewing before deploying

- The token is shared by all devices (single trust boundary).
- The relay stores plaintext clips; disk/file permissions are the OS account's.
- No rate limiting beyond the dedup window; no lockout on repeated bad tokens (the token
  is high-entropy, and comparisons are constant-time).
- Binaries are not code-signed yet.
