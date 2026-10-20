"""ASGI guard: optional bearer auth, request-size cap, request id and one structured access-log line per request."""

from __future__ import annotations

import hmac
import re
import time
import uuid
from typing import Any

from starlette.datastructures import Headers
from starlette.exceptions import HTTPException as StarletteHTTPException
from starlette.types import ASGIApp, Message, Receive, Scope, Send

from .errors import error_response
from .logging_setup import fields, get_logger

log = get_logger("http")
_SAFE_ID = re.compile(r"^[A-Za-z0-9._-]{1,64}$")
OPEN_PATHS = {"/healthz"}
BODY_OVERHEAD = 1024 * 1024  # multipart framing + small form fields on top of the file limit


class PayloadTooLarge(StarletteHTTPException):
    """Raised from `receive` while the body streams in (an HTTPException: FastAPI re-raises those unchanged)."""

    def __init__(self, limit: int) -> None:
        super().__init__(413, f"upload exceeds the {(limit - BODY_OVERHEAD) // (1024 * 1024)} MB limit")


class GuardMiddleware:
    def __init__(self, app: ASGIApp, token: str | None, max_upload_bytes: int) -> None:
        self.app = app
        self._token = token.encode() if token else None
        self._max_body = max_upload_bytes + BODY_OVERHEAD

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return

        started = time.perf_counter()
        headers = Headers(scope=scope)
        rid = headers.get("x-request-id", "")
        rid = rid if _SAFE_ID.match(rid) else uuid.uuid4().hex
        state: dict[str, Any] = {"status": 0, "bytes_in": 0}

        async def send_wrapper(message: Message) -> None:
            if message["type"] == "http.response.start":
                state["status"] = message["status"]
                message.setdefault("headers", [])
                message["headers"] = [*message["headers"], (b"x-request-id", rid.encode())]
            await send(message)

        async def counting_receive() -> Message:
            message = await receive()
            if message["type"] == "http.request":
                state["bytes_in"] += len(message.get("body", b""))
                if state["bytes_in"] > self._max_body:
                    raise PayloadTooLarge(self._max_body)
            return message

        path = scope["path"]
        try:
            if self._token is not None and path not in OPEN_PATHS and not self._authorized(headers):
                resp = error_response(401, "unauthorized", "missing or invalid bearer token")
                resp.headers["WWW-Authenticate"] = "Bearer"
                await resp(scope, receive, send_wrapper)
                return
            declared = headers.get("content-length")
            if declared is not None and declared.isdigit() and int(declared) > self._max_body:
                await _too_large(self._max_body)(scope, receive, send_wrapper)
                return
            try:
                await self.app(scope, counting_receive, send_wrapper)
            except PayloadTooLarge:
                if state["status"] == 0:
                    await _too_large(self._max_body)(scope, receive, send_wrapper)
        finally:
            (log.debug if path in OPEN_PATHS else log.info)(
                "request",
                extra=fields(
                    rid=rid,
                    method=scope["method"],
                    path=path,
                    status=state["status"],
                    ms=round((time.perf_counter() - started) * 1000, 1),
                    bytes_in=state["bytes_in"],
                ),
            )

    def _authorized(self, headers: Headers) -> bool:
        value = headers.get("authorization", "")
        scheme, _, supplied = value.partition(" ")
        if scheme.lower() != "bearer" or self._token is None:
            return False
        return hmac.compare_digest(supplied.strip().encode(), self._token)


def _too_large(limit: int) -> Any:
    mb = (limit - BODY_OVERHEAD) // (1024 * 1024)
    return error_response(413, "payload_too_large", f"upload exceeds the {mb} MB limit")
