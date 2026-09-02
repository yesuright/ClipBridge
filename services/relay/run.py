"""Development entry point: `python run.py` from services/relay.

Refuses to start without CLIPBRIDGE_TOKEN (fail-fast, no default secret).
"""
from __future__ import annotations

import sys

import uvicorn

from relay.config import resolve_path, load_settings
from relay.logging_setup import setup_logging


def main() -> int:
    settings = load_settings()

    if not settings.token:
        print(
            "ERROR: CLIPBRIDGE_TOKEN is not set.\n"
            "Set the shared device token before starting the relay, e.g.:\n"
            "  copy .env.example to .env and edit CLIPBRIDGE_TOKEN"
        )
        return 2

    setup_logging(resolve_path(settings.log_dir), settings.log_level)
    from relay.app import create_app  # import after logging is configured

    app = create_app(settings)
    print(f"ClipBridge relay listening on http://{settings.host}:{settings.port} "
          f"(docs at /docs, token required for /api/v1/*)")
    uvicorn.run(app, host=settings.host, port=settings.port, log_level="warning")
    return 0


if __name__ == "__main__":
    sys.exit(main())
