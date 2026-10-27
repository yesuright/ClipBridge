"""Development entry point: `python run.py` from services/relay.

Refuses to start without CLIPBRIDGE_TOKEN (fail-fast, no default secret).
Runs via `uvicorn.Server` so the app can request a graceful shutdown
(POST /api/v1/admin/shutdown — used by the Milestone 3 tray app).

Portability: a packaged relay may be started from a read-only folder, on a machine where
the configured port is already taken, or with a relative database path. Each of those
failures gets a specific, actionable message and a distinct exit code instead of a
traceback that only the developer can interpret.
"""
from __future__ import annotations

import socket
import sys

import uvicorn

from relay.config import is_frozen, load_settings, resolve_path, user_data_dir
from relay.logging_setup import setup_logging

EXIT_NO_TOKEN = 2
EXIT_PORT_UNAVAILABLE = 3
EXIT_STORAGE_UNAVAILABLE = 4


def _port_available(host: str, port: int) -> bool:
    """Can this process bind host:port right now?

    No SO_REUSEADDR: on Windows it lets a second process "succeed" on a port that is
    already owned, which would turn a clear conflict into a silent one.
    """
    family = socket.AF_INET6 if ":" in host else socket.AF_INET
    try:
        with socket.socket(family, socket.SOCK_STREAM) as sock:
            sock.bind((host, port))
        return True
    except OSError:
        return False


def main() -> int:
    settings = load_settings()

    if not settings.token:
        print(
            "ERROR: CLIPBRIDGE_TOKEN is not set.\n"
            "Set the shared device token before starting the relay, e.g.:\n"
            "  copy .env.example to .env and edit CLIPBRIDGE_TOKEN\n"
            "The ClipBridge Windows app starts the relay with a token automatically."
        )
        return EXIT_NO_TOKEN

    log_dir = resolve_path(settings.log_dir)
    db_path = resolve_path(settings.db_path)
    try:
        setup_logging(log_dir, settings.log_level)
    except OSError as exc:
        print(f"ERROR: cannot write logs to {log_dir}: {exc}")
        fallback = user_data_dir() / "logs"
        print(f"       retrying with {fallback}")
        setup_logging(fallback, settings.log_level)

    if not _port_available(settings.host, settings.port):
        print(
            f"ERROR: cannot listen on {settings.host}:{settings.port} — the port is already in use.\n"
            "  * Close the program that owns that port, or\n"
            "  * set CLIPBRIDGE_PORT to a free port and update the address in the Shortcut.\n"
            "  The ClipBridge Windows app picks a free port automatically."
        )
        return EXIT_PORT_UNAVAILABLE

    from relay.app import create_app  # import after logging is configured

    try:
        app = create_app(settings)
    except Exception as exc:  # noqa: BLE001 - startup must explain, not traceback
        print(f"ERROR: cannot open the clip database at {db_path}: {exc}")
        print("       Check that the folder exists and is writable by this user.")
        return EXIT_STORAGE_UNAVAILABLE

    is_loopback = settings.host in ("127.0.0.1", "::1", "localhost")
    print(f"ClipBridge relay listening on http://{settings.host}:{settings.port} "
          f"(docs at /docs, token required for /api/v1/*)")
    print(f"Database: {db_path}")
    print(f"Logs:     {log_dir}" + ("  [packaged build]" if is_frozen() else ""))
    if not is_loopback:
        print("""
WARNING: LAN MODE — the relay is reachable from other devices on your local network.
  * Bearer token travels over PLAIN HTTP: no transport encryption.
  * Use only on a trusted private network (your own home/office Wi-Fi).
  * Do NOT forward this port on the router, do NOT expose it to the Internet.
  * Production / remote use requires HTTPS or a secure tunnel (e.g. Tailscale).
See docs/SECURITY.md before extending this beyond your own personal LAN.""")
    else:
        print("Listen mode: loopback only (127.0.0.1). "
              "For iPhone/iPad use, set CLIPBRIDGE_BIND_HOST=0.0.0.0 (see docs/DEVICE_TEST.md).")

    config = uvicorn.Config(app, host=settings.host, port=settings.port, log_level="warning")
    server = uvicorn.Server(config)
    app.state.relay_server = server  # enables POST /api/v1/admin/shutdown
    server.run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
