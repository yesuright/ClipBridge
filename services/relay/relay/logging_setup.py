"""Logging setup for the relay.

Hard rule: **never log clipboard content and never log tokens.**

Allowed fields only: clip_id, device_id, content size, hash prefix, timestamps, status codes.
"""
from __future__ import annotations

import logging
import logging.handlers
import os
from pathlib import Path

from .security import hash_prefix

FORMAT = "%(asctime)s %(levelname)s %(name)s %(message)s"


def setup_logging(log_dir: str | Path, level: str = "INFO") -> None:
    log_dir = Path(log_dir)
    log_dir.mkdir(parents=True, exist_ok=True)
    root = logging.getLogger()
    root.setLevel(level.upper())

    console = logging.StreamHandler()
    console.setFormatter(logging.Formatter(FORMAT))
    root.addHandler(console)

    file_handler = logging.handlers.RotatingFileHandler(
        log_dir / "relay.log", maxBytes=5 * 1024 * 1024, backupCount=2, encoding="utf-8"
    )
    file_handler.setFormatter(logging.Formatter(FORMAT))
    root.addHandler(file_handler)


def log_clip_created(logger: logging.Logger, *, clip_id: str, device_id: str,
                     size_bytes: int, content_hash: str, origin: str) -> None:
    """Single safe log line for a stored clip — no content, no token."""
    logger.info(
        "clip_created clip_id=%s device=%s size=%d hash=%s origin=%s status=201",
        clip_id, device_id, size_bytes, hash_prefix(content_hash), origin,
    )


def log_clip_rejected(logger: logging.Logger, *, status: int, error: str,
                      device_id: str | None = None, size_bytes: int | None = None,
                      content_hash: str | None = None) -> None:
    parts = [f"clip_rejected status={status} error={error}"]
    if device_id is not None:
        parts.append(f"device={device_id}")
    if size_bytes is not None:
        parts.append(f"size={size_bytes}")
    if content_hash is not None:
        parts.append(f"hash={hash_prefix(content_hash)}")
    logger.info(" ".join(parts))
