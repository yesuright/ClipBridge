"""Log redaction: clipboard content and tokens must never appear in logs."""
from __future__ import annotations

import logging

from .conftest import TOKEN, auth_headers, make_clip

SECRET_SENTINEL = "TOP-SECRET-CLIP-CONTENT-9f3b1c-勿动"


def test_clip_content_not_logged(caplog):
    from relay.app import create_app
    from relay.config import Settings
    from relay.storage import MemoryClipStore

    settings = Settings(
        token=TOKEN, db_path=":memory:", max_content_bytes=2 * 1024 * 1024,
        max_body_bytes=16 * 1024 * 1024, history_limit=50,
        dedup_window_seconds=15.0, log_level="INFO", log_dir="data/logs",
    )
    app = create_app(settings, store=MemoryClipStore())

    import io

    stream = io.StringIO()
    handler = logging.StreamHandler(stream)
    handler.setFormatter(logging.Formatter("%(message)s"))
    logging.getLogger().addHandler(handler)

    with caplog.at_level(logging.INFO, logger="clipbridge.relay"):
        from fastapi.testclient import TestClient

        with TestClient(app) as client:
            body = make_clip(content=SECRET_SENTINEL + " and a long prompt body...",
                             device_id="windows-main")
            r = client.post("/api/v1/clips", json=body, headers=auth_headers())
            assert r.status_code == 201
            # unauth attempts also logged without secrets
            client.post("/api/v1/clips", json=make_clip(content=SECRET_SENTINEL))
            client.get("/api/v1/clips/latest", headers=auth_headers("hunter2"))

    text = stream.getvalue()

    assert SECRET_SENTINEL not in text, "clip content leaked into logs"
    assert TOKEN not in text, "token leaked into logs"
    assert "hunter2" not in text, "supplied wrong token leaked into logs"
    # safe metadata IS logged
    assert "clip_id=" in text
    assert "size=" in text
    assert "hash=" in text
    assert "device=" in text


def test_get_requests_not_logging_content(caplog):
    from relay.app import create_app
    from relay.config import Settings
    from relay.storage import MemoryClipStore

    settings = Settings(
        token=TOKEN, db_path=":memory:", max_content_bytes=2 * 1024 * 1024,
        max_body_bytes=16 * 1024 * 1024, history_limit=50,
        dedup_window_seconds=15.0, log_level="INFO", log_dir="data/logs",
    )
    app = create_app(settings, store=MemoryClipStore())
    import io

    stream = io.StringIO()
    handler = logging.StreamHandler(stream)
    handler.setFormatter(logging.Formatter("%(message)s"))
    logging.getLogger().addHandler(handler)

    with caplog.at_level(logging.INFO):
        from fastapi.testclient import TestClient

        with TestClient(app) as client:
            body = make_clip(content="log-probe-content-xyz")
            client.post("/api/v1/clips", json=body, headers=auth_headers())
            client.get("/api/v1/clips/latest", headers=auth_headers())

    assert "log-probe-content-xyz" not in stream.getvalue()
