"""Error type + JSON error bodies: { "error": "<code>", "message": "..." }. Never carries a stack trace."""

from __future__ import annotations

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

from .logging_setup import get_logger

log = get_logger("errors")


class ApiError(Exception):
    def __init__(
        self,
        status: int,
        code: str,
        message: str,
        headers: dict[str, str] | None = None,
    ) -> None:
        super().__init__(message)
        self.status = status
        self.code = code
        self.message = message
        self.headers = headers or {}


def error_response(status: int, code: str, message: str, headers: dict[str, str] | None = None) -> JSONResponse:
    return JSONResponse({"error": code, "message": message}, status_code=status, headers=headers)


_HTTP_CODES = {
    400: "invalid_request",
    401: "unauthorized",
    404: "not_found",
    405: "method_not_allowed",
    413: "payload_too_large",
    415: "unsupported_media",
}


def install(app: FastAPI) -> None:
    @app.exception_handler(ApiError)
    async def _api_error(_: Request, exc: ApiError) -> JSONResponse:
        return error_response(exc.status, exc.code, exc.message, exc.headers)

    @app.exception_handler(StarletteHTTPException)
    async def _http_error(_: Request, exc: StarletteHTTPException) -> JSONResponse:
        code = _HTTP_CODES.get(exc.status_code, "error")
        return error_response(exc.status_code, code, str(exc.detail), dict(exc.headers or {}))

    @app.exception_handler(RequestValidationError)
    async def _validation_error(_: Request, exc: RequestValidationError) -> JSONResponse:
        # Only field locations and messages: never echo submitted values (they may be user content).
        parts: list[str] = []
        for err in exc.errors()[:5]:
            loc = ".".join(str(p) for p in err.get("loc", ()) if p not in ("body", "query"))
            parts.append(f"{loc or 'request'}: {err.get('msg', 'invalid')}")
        return error_response(400, "invalid_request", "; ".join(parts) or "invalid request")

    @app.exception_handler(Exception)
    async def _unhandled(request: Request, exc: Exception) -> JSONResponse:
        log.error(
            "unhandled error",
            exc_info=(type(exc), exc, exc.__traceback__),
            extra={"fields": {"path": request.url.path, "error_type": type(exc).__name__}},
        )
        return error_response(500, "internal_error", "internal server error")
