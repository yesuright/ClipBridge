"""Request/response schemas and validation for the ClipBridge protocol."""
from __future__ import annotations

import re
import uuid
from datetime import datetime, timezone

from pydantic import BaseModel, field_validator

TEXT_PLAIN = "text/plain"
DEVICE_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
CLIP_ID_RE = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
ORIGIN_RE = re.compile(r"^[A-Za-z0-9._-]{0,64}$")


class ApiError(Exception):
    """Uniform API error: `{"error": <code>, ...extra}` with an HTTP status."""

    def __init__(self, status: int, error: str, **extra) -> None:
        super().__init__(error)
        self.status = status
        self.error = error
        self.extra = extra


class ClipCreate(BaseModel):
    clip_id: str
    device_id: str
    content_type: str = TEXT_PLAIN
    content: str
    content_hash: str | None = None
    created_at: datetime | None = None
    origin: str | None = None

    @field_validator("clip_id")
    @classmethod
    def _clip_id(cls, value: str) -> str:
        if not isinstance(value, str) or not CLIP_ID_RE.match(value):
            raise ValueError("clip_id must be a UUID string")
        return str(uuid.UUID(value)).lower()

    @field_validator("device_id")
    @classmethod
    def _device_id(cls, value: str) -> str:
        if not isinstance(value, str) or not DEVICE_ID_RE.match(value):
            raise ValueError(
                "device_id must be 1-64 chars of [A-Za-z0-9_-], starting with alphanumeric"
            )
        return value

    @field_validator("content_type")
    @classmethod
    def _content_type(cls, value: str) -> str:
        if value != TEXT_PLAIN:
            raise ValueError("only text/plain is supported in Milestone 1")
        return value

    @field_validator("origin")
    @classmethod
    def _origin(cls, value: str | None) -> str:
        if value is None:
            return "local"
        if not isinstance(value, str) or not ORIGIN_RE.match(value):
            raise ValueError("origin must be 0-64 chars of [A-Za-z0-9._-]")
        return value

    @field_validator("created_at")
    @classmethod
    def _created_at(cls, value: datetime | None) -> datetime | None:
        if value is None:
            return None
        if value.tzinfo is None:
            raise ValueError("created_at must include a timezone (UTC ISO 8601)")
        return value.astimezone(timezone.utc)


def canonical_iso(value: datetime) -> str:
    """Fixed-width UTC ISO 8601 with milliseconds and `Z`, e.g. 2025-06-01T12:00:00.123Z."""
    return value.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
