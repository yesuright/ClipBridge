"""Security primitives: hashing and constant-time token comparison."""
from __future__ import annotations

import hashlib
import hmac


def compute_content_hash(content: str) -> str:
    """SHA-256 of the UTF-8 bytes of the content, lowercase hex. The protocol hash."""
    return hashlib.sha256(content.encode("utf-8")).hexdigest()


def hash_prefix(content_hash: str, n: int = 8) -> str:
    """Short, safe log representation of a hash (never the full content)."""
    return content_hash[:n]


def constant_time_equals(a: str, b: str) -> bool:
    """Constant-time string comparison for tokens — avoids timing side channels."""
    return hmac.compare_digest(a.encode("utf-8"), b.encode("utf-8"))


def is_bearer_token_valid(header_value: str | None, expected: str | None) -> bool:
    """Validate an `Authorization: Bearer <token>` header value."""
    if expected is None:
        return False
    if not header_value or not header_value.startswith("Bearer "):
        return False
    supplied = header_value[len("Bearer "):]
    return constant_time_equals(supplied, expected)
