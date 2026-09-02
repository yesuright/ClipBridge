"""Tests for the Shortcut-optimized create endpoint (Milestone 2).

POST /api/v1/clips/shortcut accepts only `device_id` + `content` (plus optional
content_type/origin) and lets the server generate clip_id / content_hash /
created_at — so an Apple Shortcut needs only ~5 actions and no third-party app.
The stored record must be protocol-identical to the canonical POST /api/v1/clips.
"""
from __future__ import annotations

import re
import uuid

from .conftest import TOKEN, auth_headers, sha256_hex

UUID_RE = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
)


def test_shortcut_create_requires_only_device_and_content(client):
    r = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "iphone", "content": "Hello from iPhone"},
        headers=auth_headers(),
    )
    assert r.status_code == 201
    data = r.json()
    assert UUID_RE.match(data["clip_id"]), "server must generate a valid UUID clip_id"
    assert data["device_id"] == "iphone"
    assert data["content_type"] == "text/plain"
    assert data["content"] == "Hello from iPhone"
    assert data["content_hash"] == sha256_hex("Hello from iPhone")
    assert data["size_bytes"] == len("Hello from iPhone".encode("utf-8"))
    assert data["origin"] == "shortcut"
    assert data["created_at"].endswith("Z")
    assert data["received_at"].endswith("Z")


def test_shortcut_create_custom_origin(client):
    r = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "ipad", "content": "x", "origin": "quickpaste"},
        headers=auth_headers(),
    )
    assert r.status_code == 201
    assert r.json()["origin"] == "quickpaste"


def test_shortcut_create_ignores_client_clip_id(client):
    # A shortcut that still sends clip_id must not break: the server generates its own.
    r = client.post(
        "/api/v1/clips/shortcut",
        json={
            "device_id": "iphone",
            "content": "payload",
            "clip_id": "00000000-0000-4000-8000-000000000001",
        },
        headers=auth_headers(),
    )
    assert r.status_code == 201
    assert r.json()["clip_id"] != "00000000-0000-4000-8000-000000000001"


def test_shortcut_create_missing_device_id_422(client):
    r = client.post("/api/v1/clips/shortcut", json={"content": "x"}, headers=auth_headers())
    assert r.status_code == 422
    assert r.json()["error"] == "invalid_payload"


def test_shortcut_create_invalid_device_id_422(client):
    r = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "bad id!", "content": "x"},
        headers=auth_headers(),
    )
    assert r.status_code == 422


def test_shortcut_create_unicode_roundtrip(client):
    content = "你好，跨设备剪贴板 🐱🚀\n第二行 Markdown **bold** `code`"
    r = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "iphone", "content": content},
        headers=auth_headers(),
    )
    assert r.status_code == 201
    assert r.json()["content"] == content
    assert r.json()["content_hash"] == sha256_hex(content)
    assert r.json()["size_bytes"] == len(content.encode("utf-8"))


def test_shortcut_create_size_limits(client):
    near = "a" * (2 * 1024 * 1024 - 1)
    r = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "iphone", "content": near},
        headers=auth_headers(),
    )
    assert r.status_code == 201
    assert r.json()["size_bytes"] == len(near.encode("utf-8"))

    over = "a" * (2 * 1024 * 1024 + 1)
    r2 = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "iphone", "content": over},
        headers=auth_headers(),
    )
    assert r2.status_code == 413
    assert r2.json()["error"] == "content_too_large"


def test_shortcut_create_content_type_415(client):
    r = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "iphone", "content": "<b>hi</b>", "content_type": "text/html"},
        headers=auth_headers(),
    )
    assert r.status_code == 415
    assert r.json()["error"] == "unsupported_content_type"


def test_shortcut_create_invalid_json_400(client):
    r = client.post(
        "/api/v1/clips/shortcut",
        content="{not json",
        headers={**auth_headers(), "Content-Type": "application/json"},
    )
    assert r.status_code == 400
    assert r.json()["error"] == "invalid_json"


def test_shortcut_create_requires_auth(client):
    r = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "iphone", "content": "x"},
    )
    assert r.status_code == 401
    r2 = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "iphone", "content": "x"},
        headers=auth_headers("wrong-token"),
    )
    assert r2.status_code == 401


def test_shortcut_create_dedup_window_still_applies(client):
    body = {"device_id": "iphone", "content": "same text in window"}
    assert client.post("/api/v1/clips/shortcut", json=body, headers=auth_headers()).status_code == 201
    r = client.post("/api/v1/clips/shortcut", json=body, headers=auth_headers())
    assert r.status_code == 409
    assert r.json()["error"] == "duplicate_content_in_window"


def test_shortcut_create_same_content_different_device_allowed(client):
    assert client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "iphone", "content": "shared"},
        headers=auth_headers(),
    ).status_code == 201
    r = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "ipad", "content": "shared"},
        headers=auth_headers(),
    )
    assert r.status_code == 201


def test_shortcut_record_is_visible_in_history_and_latest(client):
    r = client.post(
        "/api/v1/clips/shortcut",
        json={"device_id": "iphone", "content": "from shortcut"},
        headers=auth_headers(),
    )
    clip_id = r.json()["clip_id"]
    history = client.get("/api/v1/clips?limit=10", headers=auth_headers()).json()
    assert history["count"] == 1
    assert history["clips"][0]["clip_id"] == clip_id
    assert history["clips"][0]["origin"] == "shortcut"
    latest = client.get("/api/v1/clips/latest", headers=auth_headers())
    assert latest.status_code == 200
    assert latest.json()["clip_id"] == clip_id


def test_shortcut_create_generates_unique_ids(client):
    ids = set()
    for i in range(5):
        r = client.post(
            "/api/v1/clips/shortcut",
            json={"device_id": "iphone", "content": f"unique {i}"},
            headers=auth_headers(),
        )
        ids.add(r.json()["clip_id"])
    assert len(ids) == 5
    for cid in ids:
        assert uuid.UUID(cid)


def test_shortcut_create_body_must_be_object(client):
    r = client.post(
        "/api/v1/clips/shortcut",
        json=["not", "an", "object"],
        headers=auth_headers(),
    )
    assert r.status_code == 400
    assert r.json()["error"] == "invalid_payload"
