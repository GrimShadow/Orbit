"""multilingual-e5-small (int8 ONNX): 384-d sentence embeddings for document / transcript text."""

from __future__ import annotations

from pathlib import Path

import numpy as np
from tokenizers import Tokenizer

from .imaging import l2_normalize
from .ort import session

MAX_TOKENS = 512
BATCH = 16
DIM = 384


class TextEmbedder:
    name = "multilingual-e5-small"
    dim = DIM

    def __init__(self, models_dir: Path, threads: int) -> None:
        base = models_dir / "text"
        self._sess = session(base / "model.onnx", threads)
        self._tok = Tokenizer.from_file(str(base / "tokenizer.json"))
        self._tok.enable_truncation(max_length=MAX_TOKENS)
        self._names = {i.name for i in self._sess.get_inputs()}

    def embed(self, texts: list[str], role: str) -> np.ndarray:
        # E5 was trained with these prefixes; queries and passages live in the same space.
        prefix = "query: " if role == "query" else "passage: "
        chunks = []
        for start in range(0, len(texts), BATCH):
            enc = self._tok.encode_batch([prefix + t for t in texts[start : start + BATCH]])
            width = max(len(e.ids) for e in enc)
            ids = np.full((len(enc), width), 1, dtype=np.int64)  # <pad> id = 1
            mask = np.zeros((len(enc), width), dtype=np.int64)
            for i, e in enumerate(enc):
                ids[i, : len(e.ids)] = e.ids
                mask[i, : len(e.ids)] = 1
            feeds = {"input_ids": ids, "attention_mask": mask}
            if "token_type_ids" in self._names:
                feeds["token_type_ids"] = np.zeros_like(ids)
            hidden = np.asarray(self._sess.run(["last_hidden_state"], feeds)[0], dtype=np.float32)
            m = mask[..., None].astype(np.float32)
            pooled = (hidden * m).sum(axis=1) / np.maximum(m.sum(axis=1), 1e-9)  # mean pooling over real tokens
            chunks.append(pooled)
        return l2_normalize(np.concatenate(chunks, axis=0))
