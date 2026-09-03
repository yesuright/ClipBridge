"""Storage unit tests: ordering, idempotency, dedup window, pruning, persistence."""
from __future__ import annotations

import hashlib
import time
import uuid

import pytest

from relay.security import compute_content_hash
from relay.storage import (
    ClipRecord,
    DuplicateClipId,
    DuplicateContentInWindow,
    MemoryClipStore,
    SqliteClipStore,
    utc_now_iso,
)


def make_record(content="storage test", device_id="windows-main",
                clip_id=None, created_at=None):
    return ClipRecord(
        clip_id=clip_id or str(uuid.uuid4()),
        device_id=device_id,
        content_type="text/plain",
        content=content,
        content_hash=compute_content_hash(content),
        created_at=created_at or utc_now_iso(),
        received_at=utc_now_iso(),
        origin="local",
        size_bytes=len(content.encode("utf-8")),
    )


@pytest.mark.parametrize("store_factory", [MemoryClipStore, lambda: SqliteClipStore(":memory:")])
def test_create_latest_recent_order(store_factory):
    store = store_factory()
    try:
        for i in range(5):
            store.create(make_record(content=f"item {i}"), dedup_window_seconds=15)
        assert store.latest().content == "item 4"
        recent = store.recent(3)
        assert [r.content for r in recent] == ["item 4", "item 3", "item 2"]
        assert store.get(recent[1].clip_id).content == "item 3"
    finally:
        store.close()


# ------------------------------------------------------------------ M4.1 regressions


def _record_at(content, clip_id, device_id, received_at):
    return ClipRecord(
        clip_id=clip_id,
        device_id=device_id,
        content_type="text/plain",
        content=content,
        content_hash=compute_content_hash(content),
        created_at=received_at,
        received_at=received_at,
        origin="local",
        size_bytes=len(content.encode("utf-8")),
    )


def test_same_timestamp_tie_orders_by_insertion_desc():
    """Two clips with the SAME received_at (same millisecond): the later insert wins
    (rowid DESC tiebreaker), for the global latest, the exclusion filter AND recent."""
    store = SqliteClipStore(":memory:")
    try:
        ts = "2026-01-01T00:00:00.000Z"
        older = _record_at("tie-older", "id-tie-older", "windows-main", ts)
        newer = _record_at("tie-newer", "id-tie-newer", "iphone", ts)
        store.create(older, dedup_window_seconds=1)
        store.create(newer, dedup_window_seconds=1)

        assert store.latest().clip_id == "id-tie-newer"            # rowid DESC
        assert store.latest_excluding("iphone").clip_id == "id-tie-older"
        assert store.latest_excluding("windows-main").clip_id == "id-tie-newer"
        assert [r.clip_id for r in store.recent(10)] == ["id-tie-newer", "id-tie-older"]
        assert [r.clip_id for r in store.recent_excluding(10, "iphone")] == ["id-tie-older"]
    finally:
        store.close()


def test_memory_store_same_timestamp_tie_matches_sqlite():
    """MemoryClipStore must keep the same tie semantics as SQLite (newest insert)."""
    store = MemoryClipStore(history_limit=50)
    try:
        ts = "2026-01-01T00:00:00.000Z"
        store.create(_record_at("tie-older", "id-a", "windows-main", ts), dedup_window_seconds=1)
        store.create(_record_at("tie-newer", "id-b", "iphone", ts), dedup_window_seconds=1)
        assert store.latest().clip_id == "id-b"
        assert store.latest_excluding("iphone").clip_id == "id-a"
        assert [r.clip_id for r in store.recent(10)] == ["id-b", "id-a"]
    finally:
        store.close()


@pytest.mark.parametrize("store_factory", [MemoryClipStore, lambda: SqliteClipStore(":memory:")])
def test_duplicate_clip_id_rejected(store_factory):
    store = store_factory()
    try:
        rec = make_record()
        store.create(rec, dedup_window_seconds=15)
        with pytest.raises(DuplicateClipId):
            store.create(rec, dedup_window_seconds=15)
    finally:
        store.close()


@pytest.mark.parametrize("store_factory", [MemoryClipStore, lambda: SqliteClipStore(":memory:")])
def test_dedup_window_same_device(store_factory):
    store = store_factory()
    try:
        store.create(make_record(content="dedup me"), dedup_window_seconds=15)
        with pytest.raises(DuplicateContentInWindow):
            store.create(make_record(content="dedup me"), dedup_window_seconds=15)
        # different device with identical content is allowed
        store.create(make_record(content="dedup me", device_id="iphone"),
                     dedup_window_seconds=15)
    finally:
        store.close()


@pytest.mark.parametrize("store_factory", [MemoryClipStore, lambda: SqliteClipStore(":memory:")])
def test_dedup_window_expires(store_factory):
    store = store_factory()
    try:
        store.create(make_record(content="old"), dedup_window_seconds=0.1)
        time.sleep(0.15)
        # window 0.1: a second identical post after expiry is a real new event
        store.create(make_record(content="old"), dedup_window_seconds=0.1)
        assert len(store.recent(10)) == 2
    finally:
        store.close()


def test_prune_keeps_newest_history_limit():
    store = MemoryClipStore(history_limit=5)
    for i in range(10):
        store.create(make_record(content=f"n{i}"), dedup_window_seconds=15)
    recent = store.recent(100)
    assert [r.content for r in recent] == ["n9", "n8", "n7", "n6", "n5"]
    assert store.get(recent[-1].clip_id) is not None
    # pruned record is gone
    assert len(store.recent(100)) == 5


def test_sqlite_persistence(tmp_path):
    db = tmp_path / "clipbridge.db"
    store = SqliteClipStore(str(db), history_limit=50)
    rec = make_record(content="persisted text")
    store.create(rec, dedup_window_seconds=15)
    store.close()

    store2 = SqliteClipStore(str(db), history_limit=50)
    try:
        got = store2.get(rec.clip_id)
        assert got is not None
        assert got.content == "persisted text"
        assert got.content_hash == rec.content_hash
        assert store2.latest().clip_id == rec.clip_id
    finally:
        store2.close()


def test_sqlite_utf8_roundtrip(tmp_path):
    db = tmp_path / "clipbridge.db"
    store = SqliteClipStore(str(db))
    try:
        content = "中文测试 🐱🚀 \r\n tab\t \"quote\" \\backslash"
        rec = make_record(content=content)
        store.create(rec, dedup_window_seconds=15)
        assert store.get(rec.clip_id).content == content
    finally:
        store.close()


def test_hash_agreement_between_storage_and_security():
    content = "hash agreement 测试"
    rec = make_record(content=content)
    assert rec.content_hash == hashlib.sha256(content.encode("utf-8")).hexdigest()
