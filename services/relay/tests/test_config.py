"""Configuration tests: CLIPBRIDGE_BIND_HOST precedence (Milestone 2)."""
from __future__ import annotations

import pytest

from relay.config import load_settings


@pytest.fixture(autouse=True)
def _clean_env(monkeypatch):
    for name in ("CLIPBRIDGE_BIND_HOST", "CLIPBRIDGE_HOST", "CLIPBRIDGE_PORT", "CLIPBRIDGE_TOKEN"):
        monkeypatch.delenv(name, raising=False)
    # ensure a project-local .env never leaks into these tests
    import relay.config as cfg

    monkeypatch.setattr(cfg, "_load_dotenv", lambda path: None)
    yield


def test_default_bind_host_is_loopback(monkeypatch):
    settings = load_settings()
    assert settings.host == "127.0.0.1"


def test_bind_host_is_canonical_config(monkeypatch):
    monkeypatch.setenv("CLIPBRIDGE_BIND_HOST", "0.0.0.0")
    settings = load_settings()
    assert settings.host == "0.0.0.0"


def test_bind_host_wins_over_legacy_host_alias(monkeypatch):
    monkeypatch.setenv("CLIPBRIDGE_BIND_HOST", "0.0.0.0")
    monkeypatch.setenv("CLIPBRIDGE_HOST", "127.0.0.1")
    settings = load_settings()
    assert settings.host == "0.0.0.0"


def test_legacy_host_alias_still_works(monkeypatch):
    monkeypatch.setenv("CLIPBRIDGE_HOST", "192.168.1.50")
    settings = load_settings()
    assert settings.host == "192.168.1.50"


def test_empty_bind_host_falls_back_to_loopback(monkeypatch):
    monkeypatch.setenv("CLIPBRIDGE_BIND_HOST", "")
    settings = load_settings()
    assert settings.host == "127.0.0.1"
