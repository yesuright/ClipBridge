"""Tests for requester-exclusion on the read endpoints (Milestone 2).

`GET /api/v1/clips/latest?exclude_device_id=iphone` (and the same param on
`GET /api/v1/clips`) lets a device fetch the newest clip that it did NOT
produce itself, without any per-device server state. Exclusion is purely a
read-side filter: it must never affect create/dedup semantics.
"""
from __future__ import annotations

from .conftest import TOKEN, auth_headers, make_clip, sha256_hex


def _post(client, content, device):
    return client.post("/api/v1/clips", json=make_clip(content=content, device_id=device),
                       headers=auth_headers())


def test_latest_excluding_returns_newest_from_other_devices(client):
    _post(client, "win-1", "windows-main")
    _post(client, "ip-1", "iphone")
    _post(client, "pad-1", "ipad")
    _post(client, "ip-2", "iphone")

    r = client.get("/api/v1/clips/latest?exclude_device_id=iphone", headers=auth_headers())
    assert r.status_code == 200
    data = r.json()
    assert data["device_id"] == "ipad"          # newest non-iphone record
    assert data["content"] == "pad-1"

    r2 = client.get("/api/v1/clips/latest?exclude_device_id=ipad", headers=auth_headers())
    assert r2.json()["device_id"] == "iphone"   # newest non-ipad record
    assert r2.json()["content"] == "ip-2"

    r3 = client.get("/api/v1/clips/latest", headers=auth_headers())
    assert r3.json()["device_id"] == "iphone"   # no exclusion: global newest unchanged
    assert r3.json()["content"] == "ip-2"


def test_latest_excluding_device_with_no_records_returns_global_latest(client):
    _post(client, "win-1", "windows-main")
    _post(client, "ip-1", "iphone")
    r = client.get("/api/v1/clips/latest?exclude_device_id=ipad", headers=auth_headers())
    assert r.status_code == 200
    assert r.json()["device_id"] == "iphone"


def test_latest_excluding_no_other_device_records_404(client):
    _post(client, "ip-1", "iphone")
    _post(client, "ip-2", "iphone")
    r = client.get("/api/v1/clips/latest?exclude_device_id=iphone", headers=auth_headers())
    assert r.status_code == 404
    assert r.json()["error"] == "not_found"


def test_latest_excluding_empty_store_still_404(client):
    r = client.get("/api/v1/clips/latest?exclude_device_id=iphone", headers=auth_headers())
    assert r.status_code == 404


def test_latest_excluding_invalid_device_id_422(client):
    r = client.get("/api/v1/clips/latest?exclude_device_id=bad%20id%21", headers=auth_headers())
    assert r.status_code == 422
    assert r.json()["error"] == "invalid_payload"


def test_recent_excluding_filters_and_counts(client):
    for dev, n in (("windows-main", 2), ("iphone", 3)):
        for i in range(n):
            _post(client, f"{dev}-{i}", dev)
    r = client.get("/api/v1/clips?limit=20&exclude_device_id=iphone", headers=auth_headers())
    assert r.status_code == 200
    data = r.json()
    assert data["count"] == 2
    assert all(c["device_id"] == "windows-main" for c in data["clips"])

    r2 = client.get("/api/v1/clips?limit=1&exclude_device_id=windows-main", headers=auth_headers())
    assert r2.json()["count"] == 1
    assert r2.json()["clips"][0]["device_id"] == "iphone"


def test_recent_excluding_empty_result_returns_empty_list(client):
    _post(client, "ip-1", "iphone")
    r = client.get("/api/v1/clips?limit=10&exclude_device_id=iphone", headers=auth_headers())
    assert r.status_code == 200
    assert r.json() == {"count": 0, "clips": []}


def test_excluding_empty_param_is_invalid(client):
    # an explicitly provided but empty exclusion is a client bug -> 422 (strict rule)
    _post(client, "ip-1", "iphone")
    r = client.get("/api/v1/clips/latest?exclude_device_id=", headers=auth_headers())
    assert r.status_code == 422
    assert r.json()["error"] == "invalid_payload"


def test_exclusion_is_per_request_no_server_state(client):
    _post(client, "ip-1", "iphone")
    _post(client, "win-1", "windows-main")
    r = client.get("/api/v1/clips/latest?exclude_device_id=iphone", headers=auth_headers())
    assert r.json()["device_id"] == "windows-main"
    # follow-up global request is unaffected by the previous excl_filter
    r2 = client.get("/api/v1/clips/latest", headers=auth_headers())
    assert r2.json()["device_id"] == "windows-main"


def test_exclusion_does_not_break_dedup_window(client):
    """POST-side dedup must still consider the device's own previous records,
    even though read-side exclusion hides them from that device."""
    body = make_clip(content="dedup still applies", device_id="iphone")
    assert client.post("/api/v1/clips", json=body, headers=auth_headers()).status_code == 201
    r = client.post("/api/v1/clips", json=make_clip(content="dedup still applies",
                                                    device_id="iphone"),
                    headers=auth_headers())
    assert r.status_code == 409
    assert r.json()["error"] == "duplicate_content_in_window"


def test_exclusion_requires_auth(client):
    _post(client, "win-1", "windows-main")
    r = client.get("/api/v1/clips/latest?exclude_device_id=iphone")
    assert r.status_code == 401
    r2 = client.get("/api/v1/clips?limit=10&exclude_device_id=iphone")
    assert r2.status_code == 401


def test_exclusion_unicode_device_ids_not_supported_by_design(client):
    # device ids are [A-Za-z0-9_-]; unicode ids are rejected on create AND exclude
    r = client.get("/api/v1/clips/latest?exclude_device_id=%E6%89%8B%E6%9C%BA", headers=auth_headers())
    assert r.status_code == 422


def test_exclusion_returns_canonical_records_with_valid_hash(client):
    _post(client, "win", "windows-main")
    r = client.get("/api/v1/clips/latest?exclude_device_id=iphone", headers=auth_headers())
    data = r.json()
    assert data["content_hash"] == sha256_hex(data["content"])
    assert data["size_bytes"] == len(data["content"].encode("utf-8"))
