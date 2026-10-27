"""Configuration tests: CLIPBRIDGE_BIND_HOST precedence (Milestone 2)."""
from __future__ import annotations

import pytest

from relay.config import load_settings


@pytest.fixture(autouse=True)
def _clean_env(monkeypatch):
    for name in (
        "CLIPBRIDGE_BIND_HOST",
        "CLIPBRIDGE_HOST",
        "CLIPBRIDGE_PORT",
        "CLIPBRIDGE_TOKEN",
        "CLIPBRIDGE_DB_PATH",
        "CLIPBRIDGE_LOG_DIR",
    ):
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


# --------------------------------------------------------------------- portability


def test_source_defaults_stay_repo_local(monkeypatch):
    import relay.config as cfg

    monkeypatch.setattr(cfg, "is_frozen", lambda: False)
    settings = cfg.load_settings()
    assert settings.db_path == "data/clipbridge.db"
    assert settings.log_dir == "data/logs"


def test_packaged_defaults_use_the_user_data_dir(monkeypatch, tmp_path):
    import relay.config as cfg

    monkeypatch.setattr(cfg, "is_frozen", lambda: True)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    settings = cfg.load_settings()

    assert str(tmp_path) in settings.db_path
    assert settings.db_path.endswith("clipbridge.db")
    assert "ClipBridge" in settings.db_path
    assert str(tmp_path) in settings.log_dir


def test_packaged_relative_paths_resolve_to_user_data(monkeypatch, tmp_path):
    import relay.config as cfg

    monkeypatch.setattr(cfg, "is_frozen", lambda: True)
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    resolved = cfg.resolve_path("data/clipbridge.db")

    assert resolved.is_absolute()
    assert str(tmp_path) in str(resolved)
    assert "_internal" not in str(resolved)


def test_explicit_paths_always_win(monkeypatch, tmp_path):
    import relay.config as cfg

    monkeypatch.setattr(cfg, "is_frozen", lambda: True)
    monkeypatch.setenv("CLIPBRIDGE_DB_PATH", str(tmp_path / "custom" / "x.db"))
    monkeypatch.setenv("CLIPBRIDGE_LOG_DIR", str(tmp_path / "custom-logs"))
    settings = cfg.load_settings()

    assert settings.db_path == str(tmp_path / "custom" / "x.db")
    assert settings.log_dir == str(tmp_path / "custom-logs")
    assert cfg.resolve_path(settings.db_path) == tmp_path / "custom" / "x.db"
