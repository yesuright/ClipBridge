"""Authentication tests: every /api/v1 route must require a valid bearer token."""
from __future__ import annotations

from .conftest import TOKEN, auth_headers, make_clip


def test_health_is_open(client):
    r = client.get("/api/v1/health")
    assert r.status_code == 200
    assert r.json()["status"] == "ok"


def test_create_requires_auth(client):
    r = client.post("/api/v1/clips", json=make_clip())
    assert r.status_code == 401
    assert r.json()["error"] == "unauthorized"


def test_create_with_wrong_token(client):
    r = client.post("/api/v1/clips", json=make_clip(),
                    headers=auth_headers("wrong-token"))
    assert r.status_code == 401


def test_create_with_valid_token(client):
    r = client.post("/api/v1/clips", json=make_clip(), headers=auth_headers(TOKEN))
    assert r.status_code == 201


def test_latest_requires_auth(client):
    r = client.get("/api/v1/clips/latest")
    assert r.status_code == 401


def test_recent_requires_auth(client):
    r = client.get("/api/v1/clips")
    assert r.status_code == 401


def test_token_not_leaked_in_response(client):
    r = client.post("/api/v1/clips", json=make_clip(), headers=auth_headers("bad"))
    assert TOKEN not in r.text
    assert "bad" not in r.text
