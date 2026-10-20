# Orbit AI sidecar

Self-hosted, CPU-only AI for Orbit (decision D10, ADR-0034). Nothing leaves the machine. FastAPI + ONNX Runtime + CTranslate2, no PyTorch.

| Capability | Model |
|---|---|
| Image / text embeddings (shared 512-d), zero-shot tags | CLIP ViT-B/32 (ONNX) |
| Document / transcript text embeddings (384-d, multilingual) | multilingual-e5-small (int8 ONNX) |
| OCR (Latin script) | RapidOCR / PaddleOCR PP-OCRv4 |
| Speech to text with timestamps | faster-whisper `base` (int8) + FFmpeg + VAD |
| Language detection | langdetect |

## Run

```bash
make up-ai                      # builds the image (needs internet once), starts it on http://localhost:18090, waits until healthy
curl localhost:18090/healthz    # status "loading" for about 25 s after start, then "ok"
make down                       # stops everything, including this service
```

Restart only this service: `docker compose -f deploy/docker-compose.yml --profile ai up -d --wait ai-sidecar`
(add `--build` after code changes). Logs: `docker logs -f dam-ai-sidecar-1`.

Settings (environment, see `app/config.py`): `AI_SIDECAR_TOKEN` (bearer token, empty = no auth), `AI_WHISPER_MODEL` (`tiny|base|small`),
`AI_MAX_CONCURRENCY` (2), `AI_QUEUE_TIMEOUT_SECONDS` (60), `AI_MAX_UPLOAD_MB` (25), `AI_THREADS` (cores / concurrency),
`AI_WARM_WHISPER` (false), `AI_TAG_THRESHOLD` (0.12), `AI_TAG_THRESHOLD_DEFAULT_LABELS` (0.03), `AI_PORT` (8080).

## Build options

```bash
docker build -t orbit-ai-sidecar deploy/ai-sidecar \
  --build-arg CLIP_VARIANT=fp32 \
  --build-arg WHISPER_MODELS=base \
  --build-arg HF_ENDPOINT=https://huggingface.co
```

`CLIP_VARIANT`: `fp32` (default) or `int8` (330 MB smaller, slightly less accurate). `WHISPER_MODELS`: `tiny`, `base`, `small`, comma separated
(pick one at run time with `AI_WHISPER_MODEL`). `HF_ENDPOINT`: a mirror, if the build host cannot reach huggingface.co.

Models are fetched from pinned Hugging Face revisions and verified against `models.lock.json` (SHA-256). For an air-gapped build,
lay the files out as `scripts/fetch_models.py` documents and pass `--build-context modelseed=/path/to/models`; files whose hash matches are
kept and nothing is downloaded. The same Dockerfile builds `linux/arm64` and `linux/amd64` (`--platform`).

## API (all JSON; `Authorization: Bearer <token>` on everything except `/healthz` when a token is set)

```bash
curl -F file=@photo.jpg localhost:18090/v1/embed/image
curl -H 'content-type: application/json' -d '{"kind":"clip","texts":["a red SUV on a beach"]}' localhost:18090/v1/embed/text
curl -H 'content-type: application/json' -d '{"kind":"text","role":"query","texts":["how do I reset my password?"]}' localhost:18090/v1/embed/text
curl -F file=@photo.jpg -F top_k=8 localhost:18090/v1/tag                         # optional: -F 'labels=["suv","sedan"]' -F threshold=0.1
curl -F file=@scan.png localhost:18090/v1/ocr
curl -F file=@talk.m4a -F lang=en localhost:18090/v1/transcribe                   # optional max_seconds (default 3600)
curl -H 'content-type: application/json' -d '{"text":"Das ist ein Satz."}' localhost:18090/v1/detect-language
```

The full contract, error codes and limits are in `docs/adr/0034-ai-sidecar.md`. Errors are `{ "error": "<code>", "message": "..." }`;
503 `busy` carries `Retry-After`. `GET /openapi.json` describes the request shapes.

## Tests

```bash
make test-ai
# or: docker build --target test -t orbit-ai-sidecar-test deploy/ai-sidecar && docker run --rm --memory 4g orbit-ai-sidecar-test
```

Runs `ruff`, `ruff format --check`, `mypy` and `pytest` inside the test image (real models, about 1 minute): preprocessing (shapes,
normalisation, determinism), embeddings (a red square is closer to "red" than "blue", text-to-image retrieval over generated images,
77-token truncation, multilingual e5), tagging, OCR of rendered text, transcription of `espeak-ng` speech (also mp3/m4a/ogg), silence,
auth, size limits, error shapes, no leaked internals or content in logs, concurrency cap and `busy`. Fixtures are generated in code.

## Layout

`app/` service code (`main.py` routes, `clip.py`, `textembed.py`, `ocr.py`, `whisper.py`, `langid.py`, `registry.py` lazy loading and
health, `middleware.py` auth and size cap, `limits.py`), `app/data/default_labels.txt` default tag vocabulary, `scripts/` (model fetcher,
healthcheck), `models.lock.json`, `tests/`.
