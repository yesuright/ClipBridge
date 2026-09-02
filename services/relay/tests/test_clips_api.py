"""Core clip API tests: create, latest, history, validation, duplicates."""
from __future__ import annotations

import hashlib
import time
import uuid

from .conftest import TOKEN, auth_headers, make_clip, make_settings, sha256_hex
from relay.app import create_app
from relay.storage import MemoryClipStore


def test_create_clip_returns_normalized_record(client):
    content = "Hello from ClipBridge"
    body = make_clip(content=content, device_id="windows-main")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 201
    data = r.json()
    assert data["clip_id"] == body["clip_id"]
    assert data["device_id"] == "windows-main"
    assert data["content_type"] == "text/plain"
    assert data["content"] == content
    assert data["content_hash"] == sha256_hex(content)
    assert data["size_bytes"] == len(content.encode("utf-8"))
    assert data["created_at"] == "2025-06-01T12:00:00.000Z"
    assert data["received_at"].endswith("Z")
    assert data["origin"] == "local"


def test_created_at_defaults_to_server_time_and_normalizes_timezone(client):
    body = make_clip(created_at="2025-06-01T14:00:00+02:00")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 201
    assert r.json()["created_at"].startswith("2025-06-01T12:00:00")


def test_get_latest(client):
    c1 = make_clip(content="first")
    c2 = make_clip(content="second", device_id="iphone")
    client.post("/api/v1/clips", json=c1, headers=auth_headers())
    client.post("/api/v1/clips", json=c2, headers=auth_headers())
    r = client.get("/api/v1/clips/latest", headers=auth_headers())
    assert r.status_code == 200
    assert r.json()["clip_id"] == c2["clip_id"]
    assert r.json()["device_id"] == "iphone"


def test_latest_404_when_empty(client):
    r = client.get("/api/v1/clips/latest", headers=auth_headers())
    assert r.status_code == 404
    assert r.json()["error"] == "not_found"


def test_get_by_id(client):
    body = make_clip(content="find me")
    client.post("/api/v1/clips", json=body, headers=auth_headers())
    r = client.get(f"/api/v1/clips/{body['clip_id']}", headers=auth_headers())
    assert r.status_code == 200
    assert r.json()["content"] == "find me"


def test_get_by_id_404(client):
    r = client.get(f"/api/v1/clips/{uuid.uuid4()}", headers=auth_headers())
    assert r.status_code == 404


def test_history_newest_first_and_pruned_to_limit(client, settings, store):
    settings = make_settings(history_limit=50)
    app = create_app(settings, store=MemoryClipStore(history_limit=50))
    from fastapi.testclient import TestClient

    with TestClient(app) as c:
        for i in range(60):
            body = make_clip(content=f"clip number {i:03d}", device_id=f"dev-{i % 3}")
            rr = c.post("/api/v1/clips", json=body, headers=auth_headers())
            assert rr.status_code == 201, rr.text
        r = c.get("/api/v1/clips?limit=200", headers=auth_headers())
        assert r.status_code == 200
        data = r.json()
        assert data["count"] == 50
        # newest first
        assert data["clips"][0]["content"] == "clip number 059"
        assert data["clips"][-1]["content"] == "clip number 010"
        ids = {c["clip_id"] for c in data["clips"]}
        assert len(ids) == 50


def test_history_limit_param(client):
    for i in range(7):
        client.post("/api/v1/clips", json=make_clip(content=f"item {i}"),
                    headers=auth_headers())
    r = client.get("/api/v1/clips?limit=3", headers=auth_headers())
    assert r.status_code == 200
    assert r.json()["count"] == 3
    assert [c["content"] for c in r.json()["clips"]] == ["item 6", "item 5", "item 4"]

    r = client.get("/api/v1/clips?limit=0", headers=auth_headers())
    assert r.status_code == 200
    assert r.json()["count"] == 1  # clamped to minimum 1

    r = client.get("/api/v1/clips?limit=99999", headers=auth_headers())
    assert r.status_code == 200
    assert r.json()["count"] == 7  # clamped to maximum 200


def test_invalid_clip_id_422(client):
    body = make_clip(clip_id="not-a-uuid")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 422
    assert r.json()["error"] == "invalid_payload"


def test_invalid_device_id_422(client):
    body = make_clip(device_id="bad id with spaces!!")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 422


def test_unsupported_content_type_415(client):
    body = make_clip(content="<b>hi</b>", content_type="text/html")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 415
    assert r.json()["error"] == "unsupported_content_type"


def test_hash_mismatch_422(client):
    body = make_clip(content="actual content", content_hash="0" * 64)
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 422
    assert r.json()["error"] == "hash_mismatch"


def test_missing_content_422(client):
    body = make_clip()
    del body["content"]
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 422


def test_naive_created_at_422(client):
    body = make_clip(created_at="2025-06-01T12:00:00")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 422


def test_invalid_json_400(client):
    r = client.post("/api/v1/clips", content="{not json",
                    headers={**auth_headers(), "Content-Type": "application/json"})
    assert r.status_code == 400
    assert r.json()["error"] == "invalid_json"


def test_body_too_large_413_via_content_length(client):
    r = client.post("/api/v1/clips", json=make_clip(content="x"),
                    headers={**auth_headers(), "Content-Length": str(17 * 1024 * 1024)})
    assert r.status_code == 413
    assert r.json()["error"] == "body_too_large"


def test_duplicate_clip_id_409_idempotency(client):
    body = make_clip(content="same id twice")
    assert client.post("/api/v1/clips", json=body, headers=auth_headers()).status_code == 201
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 409
    assert r.json()["error"] == "duplicate_clip_id"
    assert r.json()["clip_id"] == body["clip_id"]


def test_duplicate_content_same_device_within_window_409(client):
    c1 = make_clip(content="repeatable text", device_id="iphone")
    c2 = make_clip(content="repeatable text", device_id="iphone")
    assert client.post("/api/v1/clips", json=c1, headers=auth_headers()).status_code == 201
    r = client.post("/api/v1/clips", json=c2, headers=auth_headers())
    assert r.status_code == 409
    assert r.json()["error"] == "duplicate_content_in_window"
    assert r.json()["clip_id"] == c1["clip_id"]


def test_duplicate_content_different_device_allowed(client):
    c1 = make_clip(content="same text, other device", device_id="windows-main")
    c2 = make_clip(content="same text, other device", device_id="iphone")
    assert client.post("/api/v1/clips", json=c1, headers=auth_headers()).status_code == 201
    r = client.post("/api/v1/clips", json=c2, headers=auth_headers())
    assert r.status_code == 201


def test_duplicate_content_allowed_after_window(settings, store):
    settings = make_settings(dedup_window_seconds=0.3)
    app = create_app(settings, store=MemoryClipStore())
    from fastapi.testclient import TestClient

    with TestClient(app) as c:
        c1 = make_clip(content="same text after window", device_id="windows-main")
        c2 = make_clip(content="same text after window", device_id="windows-main")
        assert c.post("/api/v1/clips", json=c1, headers=auth_headers()).status_code == 201
        time.sleep(0.35)
        r = c.post("/api/v1/clips", json=c2, headers=auth_headers())
        assert r.status_code == 201  # deliberate re-copy is a real new event


def test_multi_device_isolation_and_identification(client):
    posted = []
    for dev in ("windows-main", "iphone", "ipad"):
        for i in range(2):
            body = make_clip(content=f"{dev}-{i}", device_id=dev)
            posted.append(body)
            assert client.post("/api/v1/clips", json=body,
                               headers=auth_headers()).status_code == 201
    r = client.get("/api/v1/clips?limit=200", headers=auth_headers())
    devices = {c["device_id"] for c in r.json()["clips"]}
    assert devices == {"windows-main", "iphone", "ipad"}
    # source device is preserved per record
    assert r.json()["clips"][0]["device_id"] == "ipad"


def test_origin_defaults_to_local(client):
    body = make_clip(content="no origin given")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 201
    assert r.json()["origin"] == "local"


def test_origin_custom(client):
    body = make_clip(content="from a shortcut", origin="shortcut")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 201
    assert r.json()["origin"] == "shortcut"
