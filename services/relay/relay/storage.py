"""Clip storage abstraction.

Milestone 1 ships two implementations:
- SqliteClipStore  — default, zero-infra persistent storage (stdlib sqlite3)
- MemoryClipStore  — for tests and quick experiments

Both implement the same semantics:
- clip_id is the primary key (POST idempotency)
- history is ordered by received_at (server clock), newest first, rowid as tiebreaker
- duplicate identical content from the same device inside the dedup window is rejected
- history is pruned to the configured limit (newest N kept)
"""
from __future__ import annotations

import abc
import sqlite3
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone


class DuplicateClipId(Exception):
    """A clip with this clip_id already exists."""

    def __init__(self, clip_id: str) -> None:
        super().__init__(f"duplicate clip_id: {clip_id}")
        self.clip_id = clip_id


class DuplicateContentInWindow(Exception):
    """Same device posted identical content inside the dedup window (loop defense)."""

    def __init__(self, clip_id: str) -> None:
        super().__init__(f"duplicate content in dedup window, existing clip_id: {clip_id}")
        self.clip_id = clip_id


def utc_now_iso() -> str:
    """Server-side UTC timestamp, fixed-width ISO 8601 with milliseconds and Z suffix."""
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


@dataclass(frozen=True)
class ClipRecord:
    clip_id: str
    device_id: str
    content_type: str
    content: str
    content_hash: str
    created_at: str
    received_at: str
    origin: str
    size_bytes: int

    def to_dict(self) -> dict:
        return {
            "clip_id": self.clip_id,
            "device_id": self.device_id,
            "content_type": self.content_type,
            "content": self.content,
            "content_hash": self.content_hash,
            "created_at": self.created_at,
            "received_at": self.received_at,
            "origin": self.origin,
            "size_bytes": self.size_bytes,
        }


@dataclass
class _Record:
    rowid: int
    record: ClipRecord


class ClipStore(abc.ABC):
    """Storage contract used by the API layer."""

    @abc.abstractmethod
    def create(self, record: ClipRecord, *, dedup_window_seconds: float) -> ClipRecord:
        """Insert a record. Raises DuplicateClipId / DuplicateContentInWindow."""

    @abc.abstractmethod
    def get(self, clip_id: str) -> ClipRecord | None: ...

    @abc.abstractmethod
    def latest(self) -> ClipRecord | None: ...

    @abc.abstractmethod
    def latest_excluding(self, device_id: str) -> ClipRecord | None:
        """Newest record whose source is NOT the given device. None when none exists."""

    @abc.abstractmethod
    def recent(self, limit: int) -> list[ClipRecord]: ...

    @abc.abstractmethod
    def recent_excluding(self, limit: int, device_id: str) -> list[ClipRecord]:
        """Newest-first records whose source is NOT the given device (limit clamped by caller)."""

    @abc.abstractmethod
    def close(self) -> None: ...


class MemoryClipStore(ClipStore):
    """In-memory store with identical semantics to the SQLite store."""

    def __init__(self, history_limit: int = 50) -> None:
        self._records: list[_Record] = []
        self._by_id: dict[str, ClipRecord] = {}
        self._history_limit = history_limit

    def create(self, record: ClipRecord, *, dedup_window_seconds: float) -> ClipRecord:
        if record.clip_id in self._by_id:
            raise DuplicateClipId(record.clip_id)
        self._check_dedup_window(record, dedup_window_seconds)
        rowid = len(self._records) + 1
        self._records.append(_Record(rowid=rowid, record=record))
        self._by_id[record.clip_id] = record
        self._prune_locked()
        return record

    def _prune_locked(self) -> None:
        while len(self._records) > self._history_limit:
            removed = self._records.pop(0)
            self._by_id.pop(removed.record.clip_id, None)

    def _check_dedup_window(self, record: ClipRecord, window_seconds: float) -> None:
        from datetime import timedelta

        cutoff = datetime.now(timezone.utc) - timedelta(seconds=window_seconds)
        for _idx, item in enumerate(self._records):
            if item.record.device_id != record.device_id:
                continue
            if item.record.content_hash != record.content_hash:
                continue
            if _isotime(item.record.received_at) and _isotime(item.record.received_at) > cutoff:
                raise DuplicateContentInWindow(item.record.clip_id)

    def get(self, clip_id: str) -> ClipRecord | None:
        return self._by_id.get(clip_id)

    def latest(self) -> ClipRecord | None:
        return self._records[-1].record if self._records else None

    def latest_excluding(self, device_id: str) -> ClipRecord | None:
        for item in reversed(self._records):
            if item.record.device_id != device_id:
                return item.record
        return None

    def recent(self, limit: int) -> list[ClipRecord]:
        return [item.record for item in self._records[-limit:]][::-1]

    def recent_excluding(self, limit: int, device_id: str) -> list[ClipRecord]:
        result: list[ClipRecord] = []
        for item in reversed(self._records):
            if item.record.device_id == device_id:
                continue
            result.append(item.record)
            if len(result) >= limit:
                break
        return result

    def close(self) -> None:
        self._records.clear()
        self._by_id.clear()


def _isotime(value: str) -> datetime | None:
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


class SqliteClipStore(ClipStore):
    """SQLite-backed store. Single connection guarded by a lock: plenty for a personal relay."""

    _SCHEMA = """
    CREATE TABLE IF NOT EXISTS clips (
        clip_id      TEXT PRIMARY KEY,
        device_id    TEXT NOT NULL,
        content_type TEXT NOT NULL,
        content      TEXT NOT NULL,
        content_hash TEXT NOT NULL,
        created_at   TEXT NOT NULL,
        received_at  TEXT NOT NULL,
        origin       TEXT NOT NULL DEFAULT 'local',
        size_bytes   INTEGER NOT NULL
    );
    CREATE INDEX IF NOT EXISTS idx_clips_received ON clips(received_at DESC);
    CREATE INDEX IF NOT EXISTS idx_clips_dedup ON clips(device_id, content_hash, received_at);
    """

    def __init__(self, path: str, history_limit: int = 50) -> None:
        import os

        if path != ":memory:":
            parent = os.path.dirname(path)
            if parent:
                os.makedirs(parent, exist_ok=True)
        self._conn = sqlite3.connect(path, check_same_thread=False)
        self._conn.row_factory = sqlite3.Row
        self._lock = threading.RLock()
        self._history_limit = history_limit
        with self._lock:
            self._conn.execute("PRAGMA journal_mode=WAL")
            self._conn.execute("PRAGMA synchronous=NORMAL")
            self._conn.execute("PRAGMA busy_timeout=5000")
            self._conn.executescript(self._SCHEMA)
            self._conn.commit()

    def create(self, record: ClipRecord, *, dedup_window_seconds: float) -> ClipRecord:
        from datetime import timedelta

        with self._lock:
            cur = self._conn.execute("SELECT 1 FROM clips WHERE clip_id = ?", (record.clip_id,))
            if cur.fetchone():
                raise DuplicateClipId(record.clip_id)
            cutoff = datetime.now(timezone.utc) - timedelta(seconds=dedup_window_seconds)
            cur = self._conn.execute(
                "SELECT clip_id FROM clips "
                "WHERE device_id = ? AND content_hash = ? AND received_at > ? "
                "ORDER BY received_at DESC, rowid DESC LIMIT 1",
                (record.device_id, record.content_hash, cutoff.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"),
            )
            row = cur.fetchone()
            if row:
                raise DuplicateContentInWindow(row["clip_id"])
            self._conn.execute(
                "INSERT INTO clips (clip_id, device_id, content_type, content, content_hash,"
                " created_at, received_at, origin, size_bytes) VALUES (?,?,?,?,?,?,?,?,?)",
                (
                    record.clip_id,
                    record.device_id,
                    record.content_type,
                    record.content,
                    record.content_hash,
                    record.created_at,
                    record.received_at,
                    record.origin,
                    record.size_bytes,
                ),
            )
            self._prune_locked()
            self._conn.commit()
        return record

    def _prune_locked(self) -> None:
        self._conn.execute(
            "DELETE FROM clips WHERE clip_id IN ("
            "  SELECT clip_id FROM clips"
            "  ORDER BY received_at DESC, rowid DESC"
            "  LIMIT -1 OFFSET ?"
            ")",
            (self._history_limit,),
        )

    def get(self, clip_id: str) -> ClipRecord | None:
        with self._lock:
            row = self._conn.execute("SELECT * FROM clips WHERE clip_id = ?", (clip_id,)).fetchone()
        return self._row_to_record(row) if row else None

    def latest(self) -> ClipRecord | None:
        with self._lock:
            row = self._conn.execute(
                "SELECT * FROM clips ORDER BY received_at DESC, rowid DESC LIMIT 1"
            ).fetchone()
        return self._row_to_record(row) if row else None

    def latest_excluding(self, device_id: str) -> ClipRecord | None:
        with self._lock:
            row = self._conn.execute(
                "SELECT * FROM clips WHERE device_id != ? "
                "ORDER BY received_at DESC, rowid DESC LIMIT 1",
                (device_id,),
            ).fetchone()
        return self._row_to_record(row) if row else None

    def recent(self, limit: int) -> list[ClipRecord]:
        with self._lock:
            rows = self._conn.execute(
                "SELECT * FROM clips ORDER BY received_at DESC, rowid DESC LIMIT ?", (limit,)
            ).fetchall()
        return [self._row_to_record(r) for r in rows]

    def recent_excluding(self, limit: int, device_id: str) -> list[ClipRecord]:
        with self._lock:
            rows = self._conn.execute(
                "SELECT * FROM clips WHERE device_id != ? "
                "ORDER BY received_at DESC, rowid DESC LIMIT ?",
                (device_id, limit),
            ).fetchall()
        return [self._row_to_record(r) for r in rows]

    def prune(self, keep: int) -> int:
        """Delete everything beyond the newest `keep` records. Returns deleted count."""
        with self._lock:
            cur = self._conn.execute(
                "DELETE FROM clips WHERE clip_id NOT IN ("
                "  SELECT clip_id FROM clips ORDER BY received_at DESC, rowid DESC LIMIT ?"
                ")",
                (keep,),
            )
            self._conn.commit()
            return cur.rowcount

    def close(self) -> None:
        with self._lock:
            self._conn.close()

    @staticmethod
    def _row_to_record(row: sqlite3.Row) -> ClipRecord:
        return ClipRecord(
            clip_id=row["clip_id"],
            device_id=row["device_id"],
            content_type=row["content_type"],
            content=row["content"],
            content_hash=row["content_hash"],
            created_at=row["created_at"],
            received_at=row["received_at"],
            origin=row["origin"],
            size_bytes=row["size_bytes"],
        )
