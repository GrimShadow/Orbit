"""Runtime settings, read from environment variables only (AI_* prefix)."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


def _int(name: str, default: int, lo: int, hi: int) -> int:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        value = int(raw)
    except ValueError as exc:
        raise SystemExit(f"{name} must be an integer, got {raw!r}") from exc
    return max(lo, min(hi, value))


def _float(name: str, default: float, lo: float, hi: float) -> float:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        value = float(raw)
    except ValueError as exc:
        raise SystemExit(f"{name} must be a number, got {raw!r}") from exc
    return max(lo, min(hi, value))


def _bool(name: str, default: bool) -> bool:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


def _cpu_count() -> int:
    try:
        return len(os.sched_getaffinity(0))
    except AttributeError:  # not on Linux
        return os.cpu_count() or 1


@dataclass(frozen=True)
class Settings:
    token: str | None = None
    max_upload_bytes: int = 25 * 1024 * 1024
    max_concurrency: int = 2
    queue_timeout_s: float = 60.0
    retry_after_s: int = 5
    models_dir: Path = Path("/models")
    tmp_dir: Path = Path("/tmp")
    whisper_model: str = "base"
    whisper_beam_size: int = 1
    warm_whisper: bool = False
    threads: int = 2
    max_audio_seconds: int = 7200
    default_audio_seconds: int = 3600
    ffmpeg_timeout_s: int = 900
    max_image_pixels: int = 100_000_000
    tag_threshold_custom: float = 0.12  # default `threshold` when the caller supplies `labels`
    tag_threshold_default_labels: float = 0.03  # ... when the bundled ~1,200-label vocabulary is used (see ADR-0034)

    @classmethod
    def from_env(cls) -> Settings:
        concurrency = _int("AI_MAX_CONCURRENCY", 2, 1, 64)
        token = os.environ.get("AI_SIDECAR_TOKEN", "").strip() or None
        return cls(
            token=token,
            max_upload_bytes=_int("AI_MAX_UPLOAD_MB", 25, 1, 1024) * 1024 * 1024,
            max_concurrency=concurrency,
            queue_timeout_s=_float("AI_QUEUE_TIMEOUT_SECONDS", 60.0, 0.0, 3600.0),
            retry_after_s=_int("AI_RETRY_AFTER_SECONDS", 5, 1, 600),
            models_dir=Path(os.environ.get("AI_MODELS_DIR", "/models")),
            tmp_dir=Path(os.environ.get("AI_TMP_DIR", "/tmp")),
            whisper_model=os.environ.get("AI_WHISPER_MODEL", "base").strip() or "base",
            whisper_beam_size=_int("AI_WHISPER_BEAM_SIZE", 1, 1, 10),
            warm_whisper=_bool("AI_WARM_WHISPER", False),
            threads=_int("AI_THREADS", max(1, _cpu_count() // concurrency), 1, 64),
            max_audio_seconds=_int("AI_MAX_AUDIO_SECONDS", 7200, 1, 86400),
            default_audio_seconds=_int("AI_DEFAULT_AUDIO_SECONDS", 3600, 1, 86400),
            ffmpeg_timeout_s=_int("AI_FFMPEG_TIMEOUT_SECONDS", 900, 1, 7200),
            tag_threshold_custom=_float("AI_TAG_THRESHOLD", 0.12, 0.0, 1.0),
            tag_threshold_default_labels=_float("AI_TAG_THRESHOLD_DEFAULT_LABELS", 0.03, 0.0, 1.0),
            max_image_pixels=_int("AI_MAX_IMAGE_PIXELS", 100_000_000, 1_000_000, 1_000_000_000),
        )
