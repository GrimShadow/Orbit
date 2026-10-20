"""FastAPI app: the HTTP contract Orbit's SelfHostedProvider codes against (see docs/adr/0034-ai-sidecar.md)."""

from __future__ import annotations

import json
import tempfile
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Annotated, Literal

import numpy as np
from fastapi import FastAPI, File, Form, Request, UploadFile
from pydantic import BaseModel, Field, StringConstraints

from . import __version__, errors, langid
from .config import Settings
from .errors import ApiError
from .imaging import load_image
from .limits import Limiter
from .logging_setup import configure as configure_logging
from .logging_setup import fields, get_logger
from .middleware import GuardMiddleware
from .ocr import SUPPORTED_LANGS, SUPPORTED_NOTE
from .registry import Registry
from .whisper import SAMPLE_RATE, decode_audio

log = get_logger("app")

MAX_LABELS = 2000
MAX_LABEL_CHARS = 100
READ_CHUNK = 1024 * 1024


class EmbedTextRequest(BaseModel):
    texts: list[Annotated[str, StringConstraints(max_length=4000)]] = Field(min_length=1, max_length=64)
    kind: Literal["clip", "text"]
    role: Literal["query", "passage"] = "passage"


class DetectLanguageRequest(BaseModel):
    text: Annotated[str, StringConstraints(max_length=1_000_000)]


def _vec(arr: np.ndarray) -> list[float]:
    return np.round(arr.astype(np.float64), 7).tolist()


async def _read_upload(file: UploadFile, limit: int) -> bytes:
    chunks: list[bytes] = []
    total = 0
    while chunk := await file.read(READ_CHUNK):
        total += len(chunk)
        if total > limit:
            raise ApiError(413, "payload_too_large", f"upload exceeds the {limit // (1024 * 1024)} MB limit")
        chunks.append(chunk)
    if total == 0:
        raise ApiError(400, "invalid_request", "the uploaded file is empty")
    return b"".join(chunks)


def _lang_param(lang: str | None) -> str | None:
    if lang is None or not lang.strip():
        return None
    code = lang.strip().lower()
    if not (2 <= len(code) <= 8) or not code.replace("-", "").isalpha():
        raise ApiError(400, "invalid_request", "lang must be an ISO language code such as 'en'")
    return code


def create_app(settings: Settings | None = None, registry: Registry | None = None) -> FastAPI:
    configure_logging()
    cfg = settings or Settings.from_env()
    reg = registry or Registry(cfg)

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        app.state.limiter = Limiter(cfg.max_concurrency, cfg.queue_timeout_s, cfg.retry_after_s)
        reg.start_warmup()
        log.info(
            "started",
            extra=fields(
                version=__version__,
                concurrency=cfg.max_concurrency,
                threads=cfg.threads,
                auth="bearer" if cfg.token else "off",
                whisper=cfg.whisper_model,
            ),
        )
        yield
        log.info("shutting down")

    app = FastAPI(title="Orbit AI sidecar", version=__version__, docs_url=None, redoc_url=None, lifespan=lifespan)
    app.add_middleware(GuardMiddleware, token=cfg.token, max_upload_bytes=cfg.max_upload_bytes)
    errors.install(app)

    def limiter(request: Request) -> Limiter:
        lim: Limiter = request.app.state.limiter
        return lim

    # ---------------------------------------------------------------- health
    @app.get("/healthz")
    async def healthz() -> dict:
        return reg.health(__version__)

    # ---------------------------------------------------------------- embeddings
    @app.post("/v1/embed/image")
    async def embed_image(request: Request, file: UploadFile = File(...)) -> dict:  # noqa: B008
        data = await _read_upload(file, cfg.max_upload_bytes)

        def work() -> dict:
            img = load_image(data, cfg.max_image_pixels)
            clip = reg.clip.get()
            return {"model": clip.name, "dim": clip.dim, "embedding": _vec(clip.embed_image(img))}

        return await limiter(request).run(work)

    @app.post("/v1/embed/text")
    async def embed_text(request: Request, body: EmbedTextRequest) -> dict:
        def work() -> dict:
            if body.kind == "clip":
                clip = reg.clip.get()
                emb = clip.embed_texts(body.texts)
                return {"model": clip.name, "dim": clip.dim, "embeddings": [_vec(r) for r in emb]}
            te = reg.text.get()
            emb = te.embed(body.texts, body.role)
            return {"model": te.name, "dim": te.dim, "embeddings": [_vec(r) for r in emb]}

        return await limiter(request).run(work)

    # ---------------------------------------------------------------- tagging
    @app.post("/v1/tag")
    async def tag(
        request: Request,
        file: UploadFile = File(...),  # noqa: B008
        labels: str | None = Form(None),
        top_k: int = Form(12),
        threshold: float | None = Form(None),
    ) -> dict:
        if not 1 <= top_k <= 100:
            raise ApiError(400, "invalid_request", "top_k must be between 1 and 100")
        if threshold is not None and not 0.0 <= threshold <= 1.0:
            raise ApiError(400, "invalid_request", "threshold must be between 0 and 1")
        custom = labels is not None and bool(labels.strip())
        label_list = _parse_labels(labels) if custom and labels is not None else reg.default_labels
        if threshold is None:
            threshold = cfg.tag_threshold_custom if custom else cfg.tag_threshold_default_labels
        min_score = threshold
        data = await _read_upload(file, cfg.max_upload_bytes)

        def work() -> dict:
            img = load_image(data, cfg.max_image_pixels)
            clip = reg.clip.get()
            emb = clip.embed_image(img)
            tags = clip.tag(emb, label_list, top_k, min_score)
            return {"model": clip.name, "tags": [{"label": lbl, "score": round(s, 5)} for lbl, s in tags]}

        return await limiter(request).run(work)

    # ---------------------------------------------------------------- OCR
    @app.post("/v1/ocr")
    async def ocr(
        request: Request,
        file: UploadFile = File(...),  # noqa: B008
        lang: str | None = Form(None),
    ) -> dict:
        hint = _lang_param(lang)
        data = await _read_upload(file, cfg.max_upload_bytes)

        def work() -> dict:
            img = load_image(data, cfg.max_image_pixels)
            engine = reg.ocr.get()
            lines = engine.run(img)
            text = "\n".join(line["text"] for line in lines)
            language = None
            if len(text) >= 20:
                code, conf = langid.detect(text)
                language = code if code != "und" and conf >= 0.7 else None
            result: dict = {"model": engine.name, "language": language, "text": text, "lines": lines}
            if hint is not None and hint.split("-")[0] not in SUPPORTED_LANGS:
                result["warnings"] = [f"lang '{hint}' is not supported by the bundled OCR models. {SUPPORTED_NOTE}"]
            return result

        return await limiter(request).run(work)

    # ---------------------------------------------------------------- speech to text
    @app.post("/v1/transcribe")
    async def transcribe(
        request: Request,
        file: UploadFile = File(...),  # noqa: B008
        lang: str | None = Form(None),
        max_seconds: int = Form(cfg.default_audio_seconds),
    ) -> dict:
        language = _lang_param(lang)
        if not 1 <= max_seconds <= cfg.max_audio_seconds:
            raise ApiError(400, "invalid_request", f"max_seconds must be between 1 and {cfg.max_audio_seconds}")
        data = await _read_upload(file, cfg.max_upload_bytes)

        def work() -> dict:
            model = reg.whisper.get()
            with tempfile.NamedTemporaryFile(dir=cfg.tmp_dir, prefix="audio-", delete=False) as tmp:
                tmp.write(data)
                path = Path(tmp.name)
            try:
                audio = decode_audio(path, max_seconds, cfg.ffmpeg_timeout_s)
            finally:
                path.unlink(missing_ok=True)
            if audio is None:
                return {"model": model.name, "language": None, "duration": 0.0, "segments": []}
            detected, segments = model.transcribe(audio, language)
            return {
                "model": model.name,
                "language": detected,
                "duration": round(audio.size / SAMPLE_RATE, 3),
                "segments": segments,
            }

        return await limiter(request).run(work)

    # ---------------------------------------------------------------- language detection
    @app.post("/v1/detect-language")
    async def detect_language(request: Request, body: DetectLanguageRequest) -> dict:
        def work() -> dict:
            code, confidence = langid.detect(body.text)
            return {"language": code, "confidence": confidence}

        return await limiter(request).run(work)

    return app


def _parse_labels(raw: str) -> list[str]:
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ApiError(400, "invalid_request", "labels must be a JSON array of strings") from exc
    if not isinstance(value, list) or not all(isinstance(v, str) for v in value):
        raise ApiError(400, "invalid_request", "labels must be a JSON array of strings")
    cleaned: list[str] = []
    seen: set[str] = set()
    for item in value:
        label = " ".join(item.split())
        if not label:
            continue
        if len(label) > MAX_LABEL_CHARS:
            raise ApiError(400, "invalid_request", f"each label must be at most {MAX_LABEL_CHARS} characters")
        if label not in seen:
            seen.add(label)
            cleaned.append(label)
    if not cleaned:
        raise ApiError(400, "invalid_request", "labels must contain at least one non-empty string")
    if len(cleaned) > MAX_LABELS:
        raise ApiError(400, "invalid_request", f"at most {MAX_LABELS} labels are allowed")
    return cleaned


app = create_app()  # `uvicorn app.main:app`; tests call create_app() with their own settings
