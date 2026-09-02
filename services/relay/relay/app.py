"""ClipBridge Relay FastAPI application.

Endpoints (all /api/v1/* except /health require `Authorization: Bearer <token>`):

- GET  /api/v1/health                     -> {"status": "ok"}            (no auth)
- POST /api/v1/clips                      -> 201 ClipOut | 400/401/409/413/415/422
- GET  /api/v1/clips/latest               -> 200 ClipOut | 404 | 401
- GET  /api/v1/clips?limit=N              -> 200 {"clips": [ClipOut], "count": N} | 401
- GET  /api/v1/clips/{clip_id}            -> 200 ClipOut | 404 | 401
"""
from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import ValidationError

from . import __version__
from .config import Settings, load_settings, resolve_path
from .logging_setup import log_clip_created, log_clip_rejected
from .models import ApiError, ClipCreate, TEXT_PLAIN, canonical_iso
from .security import compute_content_hash, is_bearer_token_valid
from .storage import (
    ClipRecord,
    ClipStore,
    DuplicateClipId,
    DuplicateContentInWindow,
    SqliteClipStore,
    utc_now_iso,
)

logger = logging.getLogger("clipbridge.relay")


def _safe_validation_errors(errors: list[dict]) -> list[dict]:
    """pydantic error dicts may carry non-serializable objects (ValueError etc.);
    reduce them to loc/msg/type so they can always be JSON-encoded."""
    return [
        {
            "loc": [str(part) for part in e.get("loc", [])],
            "msg": e.get("msg"),
            "type": e.get("type"),
        }
        for e in errors
    ]


def create_app(settings: Settings | None = None, store: ClipStore | None = None) -> FastAPI:
    settings = settings or load_settings()
    owned_store = store is None
    resolved_db_path = str(resolve_path(settings.db_path)) if owned_store else ""

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        app.state.store = (
            store if store is not None
            else SqliteClipStore(resolved_db_path, history_limit=settings.history_limit)
        )
        app.state.settings = settings
        yield
        if owned_store:
            app.state.store.close()

    app = FastAPI(
        title="ClipBridge Relay",
        version=__version__,
        lifespan=lifespan,
        docs_url="/docs",
        redoc_url=None,
    )

    # ---------------------------------------------------------------- middleware
    @app.middleware("http")
    async def body_size_guard(request: Request, call_next):
        content_length = request.headers.get("content-length")
        if content_length is not None and content_length.isdigit():
            if int(content_length) > settings.max_body_bytes:
                return JSONResponse(
                    status_code=413,
                    content={"error": "body_too_large", "max_bytes": settings.max_body_bytes},
                )
        return await call_next(request)

    # ---------------------------------------------------------------- exception handlers
    @app.exception_handler(ApiError)
    async def _api_error(_req: Request, exc: ApiError):
        return JSONResponse(status_code=exc.status, content={"error": exc.error, **exc.extra})

    @app.exception_handler(RequestValidationError)
    async def _validation_error(_req: Request, exc: RequestValidationError):
        return JSONResponse(
            status_code=422,
            content={"error": "invalid_payload", "detail": _safe_validation_errors(exc.errors())},
        )

    @app.exception_handler(Exception)
    async def _unexpected(_req: Request, exc: Exception):
        logger.exception("unhandled error: %s", type(exc).__name__)
        return JSONResponse(status_code=500, content={"error": "internal_error"})

    # ---------------------------------------------------------------- auth dependency
    def require_auth(request: Request) -> None:
        settings: Settings = request.app.state.settings
        if not is_bearer_token_valid(request.headers.get("authorization"), settings.token):
            raise ApiError(401, "unauthorized")

    Auth = Depends(require_auth)

    # ---------------------------------------------------------------- routes
    @app.get("/")
    def root():
        return {"service": "clipbridge-relay", "version": __version__,
                "docs": "/docs", "api": "/api/v1"}

    @app.get("/api/v1/health")
    def health():
        return {"status": "ok", "service": "clipbridge-relay", "version": __version__}

    @app.post("/api/v1/clips", status_code=201)
    async def create_clip(request: Request, _: None = Auth):
        settings: Settings = request.app.state.settings
        try:
            raw = await request.json()
        except Exception:
            raise ApiError(400, "invalid_json")

        if not isinstance(raw, dict):
            raise ApiError(400, "invalid_payload", detail="body must be a JSON object")

        # Distinct 415 for non-text content (explicit check before schema validation).
        if raw.get("content_type") not in (None, TEXT_PLAIN):
            raise ApiError(415, "unsupported_content_type", supported=[TEXT_PLAIN])

        try:
            payload = ClipCreate.model_validate(raw)
        except ValidationError as exc:
            raise ApiError(422, "invalid_payload", detail=_safe_validation_errors(exc.errors()))

        # The authoritative size limit is the UTF-8 byte length of the content.
        size_bytes = len(payload.content.encode("utf-8"))
        if size_bytes > settings.max_content_bytes:
            raise ApiError(
                413, "content_too_large",
                max_bytes=settings.max_content_bytes, actual_bytes=size_bytes,
            )

        computed_hash = compute_content_hash(payload.content)
        if payload.content_hash is not None and payload.content_hash.lower() != computed_hash:
            raise ApiError(422, "hash_mismatch",
                           provided=payload.content_hash, computed=computed_hash)

        created_at = canonical_iso(payload.created_at) if payload.created_at else utc_now_iso()
        received_at = utc_now_iso()

        record = ClipRecord(
            clip_id=payload.clip_id,
            device_id=payload.device_id,
            content_type=payload.content_type,
            content=payload.content,
            content_hash=computed_hash,
            created_at=created_at,
            received_at=received_at,
            origin=payload.origin,
            size_bytes=size_bytes,
        )

        store: ClipStore = request.app.state.store
        try:
            store.create(record, dedup_window_seconds=settings.dedup_window_seconds)
        except DuplicateClipId as exc:
            log_clip_rejected(logger, status=409, error="duplicate_clip_id",
                              device_id=record.device_id, size_bytes=size_bytes,
                              content_hash=computed_hash)
            raise ApiError(409, "duplicate_clip_id", clip_id=exc.clip_id)
        except DuplicateContentInWindow as exc:
            log_clip_rejected(logger, status=409, error="duplicate_content_in_window",
                              device_id=record.device_id, size_bytes=size_bytes,
                              content_hash=computed_hash)
            raise ApiError(409, "duplicate_content_in_window", clip_id=exc.clip_id)

        log_clip_created(logger, clip_id=record.clip_id, device_id=record.device_id,
                         size_bytes=size_bytes, content_hash=computed_hash,
                         origin=record.origin)
        return record.to_dict()

    @app.get("/api/v1/clips/latest")
    def latest_clip(request: Request, _: None = Auth):
        store: ClipStore = request.app.state.store
        record = store.latest()
        if record is None:
            raise ApiError(404, "not_found")
        return record.to_dict()

    @app.get("/api/v1/clips")
    def recent_clips(request: Request, limit: int = 50, _: None = Auth):
        store: ClipStore = request.app.state.store
        limit = max(1, min(limit, 200))
        clips = store.recent(limit)
        return {"count": len(clips), "clips": [c.to_dict() for c in clips]}

    @app.get("/api/v1/clips/{clip_id}")
    def get_clip(request: Request, clip_id: str, _: None = Auth):
        store: ClipStore = request.app.state.store
        record = store.get(clip_id)
        if record is None:
            raise ApiError(404, "not_found")
        return record.to_dict()

    return app
