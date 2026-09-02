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
