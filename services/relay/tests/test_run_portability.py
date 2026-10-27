"""Startup portability of the relay entry point.

A relay that cannot bind, or cannot open its database, must say so in one readable line
and exit with a specific code — the tray app and a human both need to tell these apart.
"""
from __future__ import annotations

import socket

import pytest


def _free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def test_port_available_true_for_an_unused_port():
    import run

    assert run._port_available("127.0.0.1", _free_port()) is True


def test_port_available_false_while_bound():
    import run

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        sock.listen(1)
        port = sock.getsockname()[1]
        assert run._port_available("127.0.0.1", port) is False


def test_main_refuses_to_start_without_a_token(monkeypatch):
    import run

    monkeypatch.delenv("CLIPBRIDGE_TOKEN", raising=False)
    monkeypatch.setattr(run, "load_settings", lambda: _settings(token=None))
    assert run.main() == run.EXIT_NO_TOKEN


def test_main_reports_busy_port_with_a_specific_exit_code(monkeypatch, tmp_path):
    import run

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        sock.listen(1)
        port = sock.getsockname()[1]

        monkeypatch.setattr(
            run, "load_settings",
            lambda: _settings(token="t" * 32, port=port, log_dir=str(tmp_path / "logs")),
        )
        assert run.main() == run.EXIT_PORT_UNAVAILABLE


def _settings(*, token="token", port=None, log_dir="data/logs"):
    from relay.config import Settings

    return Settings(
        host="127.0.0.1",
        port=port or _free_port(),
        token=token,
        db_path=":memory:",
        log_dir=log_dir,
    )


@pytest.mark.parametrize("host", ["127.0.0.1", "0.0.0.0"])
def test_port_check_works_for_loopback_and_all_interfaces(host):
    import run

    assert run._port_available(host, _free_port()) is True
