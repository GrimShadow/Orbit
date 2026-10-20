"""Language identification with `langdetect` (Apache-2.0 port of Google's language-detection; pure Python, ~1 MB)."""

from __future__ import annotations

import threading

from langdetect import DetectorFactory, detect_langs
from langdetect.lang_detect_exception import LangDetectException

DetectorFactory.seed = 0  # deterministic results

_lock = threading.Lock()
_NORMALISE = {"zh-cn": "zh", "zh-tw": "zh"}
MAX_CHARS = 20_000


def detect(text: str) -> tuple[str, float]:
    """Return (ISO 639-1 code, confidence 0..1). ('und', 0.0) when nothing can be determined."""
    sample = text[:MAX_CHARS]
    try:
        with _lock:
            ranked = detect_langs(sample)
    except LangDetectException:
        return "und", 0.0
    if not ranked:
        return "und", 0.0
    top = ranked[0]
    return _NORMALISE.get(top.lang, top.lang), round(float(top.prob), 4)
