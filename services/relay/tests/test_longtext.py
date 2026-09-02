"""Long-text boundary tests: 1 KB / 10 KB / 100 KB / 1 MB / 2 MiB boundary behavior."""
from __future__ import annotations

from .conftest import auth_headers, make_clip, sha256_hex

MIB = 1024 * 1024
MAX = 2 * MIB  # 2097152 bytes


def _filler(byte_count: int) -> str:
    """Deterministic ASCII filler of exactly `byte_count` UTF-8 bytes."""
    line = "The quick brown fox jumps over the lazy dog. 0123456789\n"
    reps = byte_count // len(line)
    remainder = byte_count % len(line)
    return line * reps + line[:remainder]


def _assert_roundtrip(client, content: str, label: str):
    body = make_clip(content=content, device_id="windows-main")
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 201, f"{label}: {r.text[:300]}"
    data = r.json()
    assert data["size_bytes"] == len(content.encode("utf-8")), label
    assert data["content_hash"] == sha256_hex(content), label

    latest = client.get("/api/v1/clips/latest", headers=auth_headers())
    assert latest.status_code == 200
    got = latest.json()["content"]
    assert len(got) == len(content), f"{label}: truncated length {len(got)} != {len(content)}"
    assert got == content, f"{label}: round-trip mismatch"


def test_1kb(client):
    _assert_roundtrip(client, _filler(1024), "1KB")


def test_10kb(client):
    _assert_roundtrip(client, _filler(10 * 1024), "10KB")


def test_100kb(client):
    _assert_roundtrip(client, _filler(100 * 1024), "100KB")


def test_1mb(client):
    _assert_roundtrip(client, _filler(MIB), "1MB")


def test_2mib_exact_boundary(client):
    _assert_roundtrip(client, _filler(MAX), "2MiB exact")


def test_over_2mib_rejected_413(client):
    body = make_clip(content=_filler(MAX + 1))
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 413
    data = r.json()
    assert data["error"] == "content_too_large"
    assert data["max_bytes"] == MAX
    assert data["actual_bytes"] == MAX + 1


def test_2mib_newline_heavy_not_cut_by_json_escaping(client):
    """1 MiB of newlines becomes ~2 MiB of JSON (escaped); content must survive intact."""
    content = "\n" * MIB
    body = make_clip(content=content)
    r = client.post("/api/v1/clips", json=body, headers=auth_headers())
    assert r.status_code == 201, r.text[:300]
    got = client.get("/api/v1/clips/latest", headers=auth_headers()).json()
    assert len(got["content"]) == MIB
    assert got["content"] == content


def test_2mib_with_quotes_and_backslashes(client):
    """JSON-escape-heavy content near the limit must round-trip byte-identically."""
    chunk = 'quote" backslash\\ tab\t newline\n' * 4096  # ~31 bytes * 4096 = ~127KB
    reps = MAX // len(chunk)
    content = (chunk * reps) + chunk[: MAX % len(chunk)]
    assert len(content.encode("utf-8")) == MAX
    _assert_roundtrip(client, content, "2MiB escape-heavy")
