#!/usr/bin/env python3
"""Download the pinned model files into a directory, verifying size and SHA-256.

Used at IMAGE BUILD time only (the runtime image has no network need). Standard library only.

    fetch_models.py --out /models --clip-variant fp32 --whisper base,tiny

Layout produced (what app.config expects):
    <out>/clip/{vision_model.onnx,text_model.onnx,tokenizer.json}
    <out>/text/{model.onnx,tokenizer.json}
    <out>/whisper-<size>/{config.json,model.bin,tokenizer.json,vocabulary.txt}

If a file already exists with the right hash it is kept (so an air-gapped build can pre-seed <out>).
Set HF_ENDPOINT to use a mirror.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import time
import urllib.request
from pathlib import Path

LOCK = Path(__file__).resolve().parent.parent / "models.lock.json"


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return h.hexdigest()


def fetch(url: str, dest: Path, sha256: str, size: int) -> None:
    if dest.exists() and dest.stat().st_size == size and sha256_of(dest) == sha256:
        print(f"keep   {dest} (hash ok)")
        return
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(dest.suffix + ".part")
    last: Exception | None = None
    for attempt in range(1, 5):
        try:
            print(f"fetch  {url} -> {dest} (attempt {attempt})", flush=True)
            req = urllib.request.Request(url, headers={"User-Agent": "orbit-ai-sidecar-build"})
            # nosemgrep: python.lang.security.audit.dynamic-urllib-use-detected.dynamic-urllib-use-detected -- build-time only; urls come from models.lock.json and the result is verified against a pinned SHA-256
            with urllib.request.urlopen(req, timeout=120) as r, tmp.open("wb") as out:
                shutil.copyfileobj(r, out, 1 << 20)
            got = sha256_of(tmp)
            if got != sha256 or tmp.stat().st_size != size:
                raise RuntimeError(f"checksum mismatch for {url}: expected {sha256}, got {got}")
            tmp.replace(dest)
            return
        except Exception as exc:
            last = exc
            tmp.unlink(missing_ok=True)
            time.sleep(2 * attempt)
    raise SystemExit(f"FAILED to fetch {url}: {last}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True, type=Path)
    ap.add_argument("--clip-variant", choices=["fp32", "int8"], default="fp32")
    ap.add_argument("--whisper", default="base", help="comma separated: tiny,base,small")
    args = ap.parse_args()

    lock = json.loads(LOCK.read_text())
    endpoint = os.environ.get("HF_ENDPOINT", "https://huggingface.co").rstrip("/")

    def url(entry: dict, name: str) -> str:
        return f"{endpoint}/{entry['repo']}/resolve/{entry['revision']}/{name}"

    def get(key: str, remote: str, local: Path) -> None:
        entry = lock[key]
        meta = entry["files"][remote]
        fetch(url(entry, remote), local, meta["sha256"], meta["size"])

    suffix = "" if args.clip_variant == "fp32" else "_int8"
    out: Path = args.out
    get("clip", f"onnx/vision_model{suffix}.onnx", out / "clip" / "vision_model.onnx")
    get("clip", f"onnx/text_model{suffix}.onnx", out / "clip" / "text_model.onnx")
    get("clip", "tokenizer.json", out / "clip" / "tokenizer.json")
    get("text", "onnx/model_int8.onnx", out / "text" / "model.onnx")
    get("text", "tokenizer.json", out / "text" / "tokenizer.json")
    for size in [s.strip() for s in args.whisper.split(",") if s.strip()]:
        key = f"whisper-{size}"
        if key not in lock:
            sys.exit(f"unknown whisper size {size!r}; known: tiny, base, small")
        for name in lock[key]["files"]:
            get(key, name, out / key / name)
    (out / "CLIP_VARIANT").write_text(args.clip_variant + "\n")
    print("models ready")


if __name__ == "__main__":
    main()
