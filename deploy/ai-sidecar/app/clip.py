"""CLIP ViT-B/32 (ONNX): image tower, text tower, BPE tokenizer. Shared 512-d space."""

from __future__ import annotations

import hashlib
import threading
from collections import OrderedDict
from pathlib import Path

import numpy as np
from PIL import Image
from tokenizers import Tokenizer

from .imaging import clip_preprocess, l2_normalize
from .ort import session

CONTEXT_LENGTH = 77
EOS_ID = 49407  # <|endoftext|>, also CLIP's pad id
LOGIT_SCALE = 100.0  # exp(logit_scale) of the released OpenAI ViT-B/32 weights
PROMPT = "a photo of {}."
DIM = 512
TEXT_BATCH = 64


class Clip:
    dim = DIM

    def __init__(self, models_dir: Path, threads: int) -> None:
        base = models_dir / "clip"
        variant = "fp32"
        marker = models_dir / "CLIP_VARIANT"
        if marker.exists():
            variant = marker.read_text().strip() or "fp32"
        self.name = "clip-vit-base-patch32" + ("" if variant == "fp32" else f"-{variant}")
        self._vision = session(base / "vision_model.onnx", threads)
        self._text = session(base / "text_model.onnx", threads)
        self._tok = Tokenizer.from_file(str(base / "tokenizer.json"))
        self._tok.enable_truncation(max_length=CONTEXT_LENGTH)
        self._label_cache: OrderedDict[str, np.ndarray] = OrderedDict()
        self._cache_lock = threading.Lock()

    def embed_image(self, img: Image.Image) -> np.ndarray:
        pixels = clip_preprocess(img)
        out = self._vision.run(["image_embeds"], {"pixel_values": pixels})[0]
        return l2_normalize(np.asarray(out, dtype=np.float32))[0]

    def tokenize(self, texts: list[str]) -> np.ndarray:
        encodings = self._tok.encode_batch(texts)
        width = max(len(e.ids) for e in encodings)
        ids = np.full((len(texts), width), EOS_ID, dtype=np.int64)
        for i, e in enumerate(encodings):
            ids[i, : len(e.ids)] = e.ids
        return ids

    def embed_texts(self, texts: list[str]) -> np.ndarray:
        if not texts:
            return np.zeros((0, DIM), dtype=np.float32)
        chunks = []
        for start in range(0, len(texts), TEXT_BATCH):
            ids = self.tokenize(texts[start : start + TEXT_BATCH])
            out = self._text.run(["text_embeds"], {"input_ids": ids})[0]
            chunks.append(np.asarray(out, dtype=np.float32))
        return l2_normalize(np.concatenate(chunks, axis=0))

    def label_embeddings(self, labels: list[str]) -> np.ndarray:
        """Prompt-templated label embeddings, cached per label set (LRU of 16 sets)."""
        key = hashlib.sha256(("\n".join(labels) + "\x00" + PROMPT).encode()).hexdigest()
        with self._cache_lock:
            hit = self._label_cache.get(key)
            if hit is not None:
                self._label_cache.move_to_end(key)
                return hit
        emb = self.embed_texts([PROMPT.format(label) for label in labels])
        with self._cache_lock:
            self._label_cache[key] = emb
            while len(self._label_cache) > 16:
                self._label_cache.popitem(last=False)
        return emb

    def tag(
        self, image_embedding: np.ndarray, labels: list[str], top_k: int, threshold: float
    ) -> list[tuple[str, float]]:
        """Zero-shot: softmax over the label set of (logit scale * cosine)."""
        label_emb = self.label_embeddings(labels)
        logits = LOGIT_SCALE * (label_emb @ image_embedding)
        logits = logits - logits.max()
        probs = np.exp(logits)
        probs = probs / probs.sum()
        order = np.argsort(-probs)[:top_k]
        return [(labels[i], float(probs[i])) for i in order if probs[i] >= threshold]
