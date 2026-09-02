"""Milestone 3: internal admin/shutdown endpoint — additive, auth-guarded."""
from __future__ import annotations

from tests.conftest import TOKEN, auth_headers, client


def test_shutdown_requires_auth(client):
    resp = client.post("/api/v1/admin/shutdown")
    assert resp.status_code == 401
    assert resp.json()["error"] == "unauthorized"


def test_shutdown_without_controlling_server_is_503(client):
    """Safe behaviour when the app runs in-process (tests): never kills anything."""
    resp = client.post("/api/v1/admin/shutdown", headers=auth_headers(TOKEN))
    assert resp.status_code == 503
    assert resp.json()["error"] == "shutdown_not_capable"


def test_shutdown_does_not_touch_public_api(client):
    """The endpoint is additive: health and clips endpoints behave exactly as before."""
    assert client.get("/api/v1/health").json()["status"] == "ok"
    resp = client.get("/api/v1/clips/latest", headers=auth_headers(TOKEN))
    # empty store -> the normal 404 not_found, unchanged by the shutdown endpoint
    assert resp.status_code == 404
