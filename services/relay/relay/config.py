"""Configuration and .env loading for the ClipBridge relay.

Rules:
- Environment variables always win over `.env`.
- The service refuses to start without a token (fail-fast, no default secret).
"""
from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

SERVICE_DIR = Path(__file__).resolve().parent.parent  # services/relay


def _load_dotenv(path: Path) -> None:
    """Minimal .env loader (no external dependency). Never overrides real env vars."""
    if not path.exists():
        return
    with open(path, "r", encoding="utf-8-sig") as fh:
        for raw in fh:
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            key = key.strip()
            value = value.strip().strip("\"'")
            os.environ.setdefault(key, value)


def _int_env(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    return int(raw)


def _float_env(name: str, default: float) -> float:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    return float(raw)


@dataclass(frozen=True)
class Settings:
    host: str = "127.0.0.1"
    port: int = 8787
    token: str | None = None
    db_path: str = "data/clipbridge.db"
    max_content_bytes: int = 2 * 1024 * 1024  # 2 MiB UTF-8 text
    max_body_bytes: int = 16 * 1024 * 1024  # JSON escape overhead guard
    history_limit: int = 50
    dedup_window_seconds: float = 15.0
    log_level: str = "INFO"
    log_dir: str = "data/logs"


def load_settings() -> Settings:
    _load_dotenv(SERVICE_DIR / ".env")
    return Settings(
        host=os.environ.get("CLIPBRIDGE_HOST", "127.0.0.1"),
        port=_int_env("CLIPBRIDGE_PORT", 8787),
        token=os.environ.get("CLIPBRIDGE_TOKEN") or None,
        db_path=os.environ.get("CLIPBRIDGE_DB_PATH", "data/clipbridge.db"),
        max_content_bytes=_int_env("CLIPBRIDGE_MAX_CONTENT_BYTES", 2 * 1024 * 1024),
        max_body_bytes=_int_env("CLIPBRIDGE_MAX_BODY_BYTES", 16 * 1024 * 1024),
        history_limit=_int_env("CLIPBRIDGE_HISTORY_LIMIT", 50),
        dedup_window_seconds=_float_env("CLIPBRIDGE_DEDUP_WINDOW_SECONDS", 15.0),
        log_level=os.environ.get("CLIPBRIDGE_LOG_LEVEL", "INFO"),
        log_dir=os.environ.get("CLIPBRIDGE_LOG_DIR", "data/logs"),
    )


def resolve_path(relative: str) -> Path:
    """Resolve a configured path: absolute stays absolute, relative is rooted at services/relay."""
    p = Path(relative)
    return p if p.is_absolute() else SERVICE_DIR / p
