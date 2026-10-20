"""OCR via rapidocr-onnxruntime (PaddleOCR PP-OCRv4 detection + recognition models, packaged for ONNX Runtime)."""

from __future__ import annotations

import threading
from typing import Any

import numpy as np
from PIL import Image

MAX_SIDE = 4096  # bound memory on huge scans; RapidOCR itself downsizes to 2000 px for detection
SUPPORTED_NOTE = (
    "The bundled PP-OCRv4 models read Latin-script text (English and most European languages, digits, punctuation) "
    "and Simplified Chinese. Other scripts (Devanagari, Tamil, Telugu, Arabic, ...) are not recognised."
)
# Language hints that the bundled models can serve (ISO 639-1). Anything else still runs but gets a warning.
SUPPORTED_LANGS = {
    "en", "de", "fr", "es", "it", "pt", "nl", "sv", "da", "no", "nb", "fi", "pl", "cs", "sk", "sl", "hr", "ro",
    "hu", "tr", "id", "ms", "vi", "ca", "eu", "gl", "et", "lv", "lt", "zh", "latin", "ch",
}  # fmt: skip


class Ocr:
    name = "rapidocr-pp-ocrv4"

    def __init__(self, threads: int) -> None:
        from rapidocr_onnxruntime import RapidOCR  # imported lazily: pulls in cv2

        # limit_type=max: never upscale small images. The default (min side >= 736) enlarges banners and posters so much
        # that the detector splits big text at every word gap.
        self._engine = RapidOCR(
            intra_op_num_threads=threads,
            inter_op_num_threads=1,
            det_limit_type="max",
            det_limit_side_len=1600,
        )
        self._lock = threading.Lock()

    def run(self, img: Image.Image) -> list[dict[str, Any]]:
        if max(img.size) > MAX_SIDE:
            img = img.copy()
            img.thumbnail((MAX_SIDE, MAX_SIDE), Image.Resampling.LANCZOS)
        arr = np.asarray(img.convert("RGB"))[:, :, ::-1]  # RapidOCR expects BGR (OpenCV order)
        with self._lock:
            result, _ = self._engine(np.ascontiguousarray(arr), unclip_ratio=2.0)  # 2.0 keeps word gaps in one line
        lines: list[dict[str, Any]] = []
        for box, text, score in result or []:
            lines.append(
                {
                    "text": str(text),
                    "score": round(float(score), 4),
                    "box": [[round(float(x), 1), round(float(y), 1)] for x, y in box],
                }
            )
        return lines
