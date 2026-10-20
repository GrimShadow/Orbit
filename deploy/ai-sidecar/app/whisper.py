"""Speech-to-text: ffmpeg decode -> faster-whisper (CTranslate2, int8, CPU) with VAD."""

from __future__ import annotations

import subprocess
import threading
from pathlib import Path
from typing import Any

import numpy as np

from .errors import ApiError

SAMPLE_RATE = 16_000


def decode_audio(path: Path, max_seconds: int, timeout_s: int) -> np.ndarray | None:
    """Decode any container ffmpeg understands to mono 16 kHz float32. None when the file has no audio stream."""
    cmd = [
        "ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1",
        "-i", str(path), "-map", "0:a:0", "-vn", "-sn", "-dn",
        "-t", str(max_seconds), "-ac", "1", "-ar", str(SAMPLE_RATE), "-f", "f32le", "pipe:1",
    ]  # fmt: skip
    try:
        proc = subprocess.run(cmd, capture_output=True, timeout=timeout_s, check=False)  # noqa: S603
    except subprocess.TimeoutExpired as exc:
        raise ApiError(415, "unsupported_media", "decoding the audio timed out") from exc
    if proc.returncode != 0:
        err = proc.stderr.decode("utf-8", "replace")
        if "matches no streams" in err or "Stream map" in err:
            return None
        raise ApiError(415, "unsupported_media", "could not decode the audio")
    audio = np.frombuffer(proc.stdout, dtype=np.float32)
    return audio if audio.size else None


class Whisper:
    def __init__(self, model_dir: Path, name: str, threads: int, beam_size: int) -> None:
        from faster_whisper import WhisperModel  # lazy: heavy import

        self.name = name
        self._beam = beam_size
        self._model = WhisperModel(
            str(model_dir), device="cpu", compute_type="int8", cpu_threads=threads, num_workers=1, local_files_only=True
        )
        self._lock = threading.Lock()  # one transcription at a time: bounds memory, CT2 is not re-entrant per model

    def transcribe(self, audio: np.ndarray, lang: str | None) -> tuple[str | None, list[dict[str, Any]]]:
        with self._lock:
            try:
                segments, info = self._model.transcribe(
                    audio,
                    language=lang,
                    beam_size=self._beam,
                    vad_filter=True,
                    vad_parameters={"min_silence_duration_ms": 500},
                    condition_on_previous_text=False,
                )
                out = [
                    {"startMs": round(s.start * 1000), "endMs": round(s.end * 1000), "text": s.text.strip()}
                    for s in segments
                    if s.text.strip()
                ]
            except ValueError as exc:  # e.g. unsupported language code
                raise ApiError(400, "invalid_request", str(exc)) from exc
        language = lang or (info.language if out else None)
        return language, out
