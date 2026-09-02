"""Shared fixtures for relay tests."""
from __future__ import annotations

import hashlib
import sys
import uuid
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from relay.app import create_app  # noqa: E402
from relay.config import Settings  # noqa: E402
from relay.storage import MemoryClipStore  # noqa: E402

TOKEN = "test-token-0123456789abcdef0123456789abcdef"


def make_settings(**overrides) -> Settings:
    defaults = dict(
        host="127.0.0.1",
        port=8787,
        token=TOKEN,
        db_path=":memory:",
        max_content_bytes=2 * 1024 * 1024,
        max_body_bytes=16 * 1024 * 1024,
        history_limit=50,
        dedup_window_seconds=15.0,
        log_level="WARNING",
        log_dir="data/logs",
    )
    defaults.update(overrides)
    return Settings(**defaults)


@pytest.fixture
def settings():
    return make_settings()


@pytest.fixture
def store():
    return MemoryClipStore(history_limit=50)


@pytest.fixture
def client(settings, store):
    app = create_app(settings, store=store)
    with TestClient(app) as c:
        yield c


def auth_headers(token: str = TOKEN) -> dict:
    return {"Authorization": f"Bearer {token}"}


def make_clip(content: str = "hello world", device_id: str = "windows-main",
              clip_id: str | None = None, **overrides) -> dict:
    body = {
        "clip_id": clip_id or str(uuid.uuid4()),
        "device_id": device_id,
        "content_type": "text/plain",
        "content": content,
        "content_hash": hashlib.sha256(content.encode("utf-8")).hexdigest(),
        "created_at": "2025-06-01T12:00:00.000Z",
        "origin": "local",
    }
    body.update(overrides)
    return body


def sha256_hex(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()
