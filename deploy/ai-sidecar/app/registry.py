"""Model registry: lazy, thread-safe loading, background warm-up and the /healthz view."""

from __future__ import annotations

import threading
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any, Generic, TypeVar

import numpy as np
from PIL import Image

from .clip import Clip
from .config import Settings
from .errors import ApiError
from .logging_setup import fields, get_logger
from .ocr import Ocr
from .textembed import TextEmbedder
from .whisper import Whisper

log = get_logger("registry")
SAMPLE_WARM = 16_000  # one second of silence to initialise the whisper graph
T = TypeVar("T")


class Slot(Generic[T]):
    """One model. `get()` loads it on first use; concurrent callers wait for the same load."""

    def __init__(self, key: str, loader: Callable[[], T]) -> None:
        self.key = key
        self._loader = loader
        self._lock = threading.Lock()
        self._obj: T | None = None
        self.error: str | None = None

    @property
    def loaded(self) -> bool:
        return self._obj is not None

    def get(self) -> T:
        if self._obj is not None:
            return self._obj
        with self._lock:
            if self._obj is None:
                started = time.perf_counter()
                try:
                    self._obj = self._loader()
                except ApiError:
                    raise
                except Exception as exc:
                    self.error = f"{type(exc).__name__}: {exc}"
                    log.error("model load failed", extra=fields(model=self.key, error=self.error))
                    raise ApiError(503, "model_unavailable", f"the {self.key} model could not be loaded") from exc
                self.error = None
                log.info("model loaded", extra=fields(model=self.key, seconds=round(time.perf_counter() - started, 2)))
            return self._obj


class Registry:
    def __init__(self, settings: Settings) -> None:
        self.settings = settings
        self.clip: Slot[Clip] = Slot("clip", lambda: Clip(settings.models_dir, settings.threads))
        self.text: Slot[TextEmbedder] = Slot("text", lambda: TextEmbedder(settings.models_dir, settings.threads))
        self.ocr: Slot[Ocr] = Slot("ocr", lambda: Ocr(settings.threads))
        self.whisper: Slot[Whisper] = Slot("whisper", self._load_whisper)
        self.default_labels: list[str] = load_default_labels()
        self._warm_thread: threading.Thread | None = None
        self._warm_done = threading.Event()

    # -- whisper ---------------------------------------------------------------------------------
    @property
    def whisper_name(self) -> str:
        return "faster-whisper-" + Path(self.settings.whisper_model).name

    def _load_whisper(self) -> Whisper:
        configured = self.settings.whisper_model
        path = Path(configured) if "/" in configured else self.settings.models_dir / f"whisper-{configured}"
        if not (path / "model.bin").exists():
            raise ApiError(
                503,
                "model_unavailable",
                f"whisper model '{configured}' is not baked into this image "
                "(rebuild with --build-arg WHISPER_MODELS=<sizes> or set AI_WHISPER_MODEL)",
            )
        return Whisper(path, self.whisper_name, self.settings.threads, self.settings.whisper_beam_size)

    # -- warm-up ---------------------------------------------------------------------------------
    def start_warmup(self) -> None:
        if self._warm_thread is not None:
            return
        self._warm_thread = threading.Thread(target=self._warm, name="model-warmup", daemon=True)
        self._warm_thread.start()

    def wait_warm(self, timeout: float | None = None) -> bool:
        return self._warm_done.wait(timeout)

    def _warm(self) -> None:
        started = time.perf_counter()
        try:
            for slot in (self.clip, self.text, self.ocr):
                try:
                    slot.get()
                except ApiError:
                    continue  # recorded in slot.error; health reports "degraded"
            if self.clip.loaded:
                clip = self.clip.get()
                clip.embed_image(Image.new("RGB", (64, 64), (128, 128, 128)))
                clip.label_embeddings(self.default_labels)  # the default tag vocabulary, so the first /v1/tag is fast
            if self.text.loaded:
                self.text.get().embed(["warm-up"], "passage")
            if self.ocr.loaded:
                self.ocr.get().run(Image.new("RGB", (96, 48), (255, 255, 255)))
            if self.settings.warm_whisper:
                try:
                    w = self.whisper.get()
                    w.transcribe(np.zeros(SAMPLE_WARM, dtype=np.float32), None)
                except ApiError:
                    pass
        except Exception as exc:
            log.error("warm-up failed", extra=fields(error=f"{type(exc).__name__}: {exc}"))
        finally:
            self._warm_done.set()
            log.info("warm-up finished", extra=fields(seconds=round(time.perf_counter() - started, 2)))

    # -- health ----------------------------------------------------------------------------------
    def health(self, version: str) -> dict[str, Any]:
        warm_slots: list[Slot[Any]] = [self.clip, self.text, self.ocr]
        if self.settings.warm_whisper:
            warm_slots.append(self.whisper)
        if not self._warm_done.is_set():
            status = "loading"
        elif all(s.loaded for s in warm_slots):
            status = "ok"
        else:
            status = "degraded"
        clip_name = self.clip.get().name if self.clip.loaded else "clip-vit-base-patch32"
        return {
            "status": status,
            "models": {
                "clip": {"name": clip_name, "dim": 512, "loaded": self.clip.loaded},
                "text": {"name": TextEmbedder.name, "dim": 384, "loaded": self.text.loaded},
                "whisper": {"name": self.whisper_name, "loaded": self.whisper.loaded},
                "ocr": {"name": Ocr.name, "loaded": self.ocr.loaded},
            },
            "version": version,
        }


def load_default_labels() -> list[str]:
    path = Path(__file__).parent / "data" / "default_labels.txt"
    labels: list[str] = []
    seen: set[str] = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        label = line.strip()
        if label and not label.startswith("#") and label not in seen:
            seen.add(label)
            labels.append(label)
    return labels
