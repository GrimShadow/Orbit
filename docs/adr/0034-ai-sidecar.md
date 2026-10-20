# ADR-0034: Self-hosted AI sidecar

## Status
Accepted (Step 2.1, sidecar). The .NET `SelfHostedProvider` that calls it is described in the Step 2.1 backend notes.

## Context
Decision D10 says AI sits behind `IAiProvider` with a cloud implementation and a self-hosted one that gives "the same features in all
hosting models". Many Orbit customers cannot send images, documents or recordings to a third party. The self-hosted provider therefore needs
a service that runs next to the platform, on a laptop or a CI box (Apple Silicon `linux/arm64` with 8 GB for Docker, and `linux/amd64`),
without a GPU and without network access at run time. It must offer, through one small HTTP contract: CLIP image and text embeddings in
one shared space (so "type a sentence, find the picture" works), zero-shot tagging, OCR, speech-to-text with timestamps, text embeddings
for document and transcript similarity, and language detection.

## Decision
A single Python (FastAPI) container, `deploy/ai-sidecar`, built from one multi-arch Dockerfile. No PyTorch: ONNX Runtime for the neural
models, CTranslate2 for Whisper. Models are downloaded **at image build time** from pinned Hugging Face revisions and verified against
SHA-256 values in `deploy/ai-sidecar/models.lock.json`; the running container needs no internet. Compose service `ai-sidecar` in the `ai`
(and `all`) profile, host port 18090 (`make up-ai`). It is stateless: no database, no volume, read-only root filesystem, a 512 MB tmpfs.

### Models, licences, sizes

| Role | Model (pinned revision) | Licence | Size in image |
|---|---|---|---|
| Image and text embeddings (512-d), zero-shot tags | OpenAI CLIP ViT-B/32 as ONNX, `Xenova/clip-vit-base-patch32@d15189d` (vision fp32 352 MB, text fp32 254 MB, BPE tokenizer 2 MB). `--build-arg CLIP_VARIANT=int8` swaps in the int8 pair (89 + 64 MB). | MIT (OpenAI CLIP). The Xenova repo is a format conversion and declares no licence of its own. | 608 MB (fp32) |
| Document / transcript text embeddings (384-d) | `intfloat/multilingual-e5-small`, int8 ONNX, `Xenova/multilingual-e5-small@761b726` (118 MB + 17 MB tokenizer) | MIT | 135 MB |
| Speech to text | `Systran/faster-whisper-base@ebe41f7` (CTranslate2, run as int8). `tiny` (76 MB) and `small` (484 MB) are pinned too: `--build-arg WHISPER_MODELS=tiny,base,small`, choose at run time with `AI_WHISPER_MODEL`. | MIT (OpenAI Whisper) | 148 MB |
| OCR | `rapidocr-onnxruntime` 1.4.4 with its bundled PaddleOCR PP-OCRv4 detection + recognition and the angle classifier (16 MB inside the wheel) | Apache-2.0 (RapidOCR and PaddleOCR) | 16 MB |
| Language detection | `langdetect` 1.0.9 (pure Python port of Google's language-detection, about 1 MB of profiles) | Apache-2.0 | 1 MB |

Final image: about 1.9 GB uncompressed on arm64 (2.2 GB on amd64): 0.9 GB models, 0.5 GB Python wheels, 0.4 GB Debian `ffmpeg`, rest base.
Python dependencies are pinned to exact versions in `requirements*.txt`. FFmpeg (Debian) is run as a separate process, not linked.

**Why `langdetect`.** The language detector must cover the Indian languages the platform is configured for (hi, mr, ta, te, bn, gu, kn,
ml, pa, ur and more: 55 languages in all), be permissively licensed, tiny, and install on arm64 and amd64 without compiling. `lingua` is
accurate but hundreds of MB; fastText's `lid.176` model is CC-BY-SA and the `fasttext` package needs a C++ build; `pycld3` needs protobuf
tooling. `langdetect` is seeded for deterministic output. Known weakness: very short text, and Hindi versus Marathi (both Devanagari) can
be confused. Callers should treat `confidence < 0.8` as "unsure".

**Why `rapidocr-onnxruntime`.** The specification names PaddleOCR. RapidOCR is the same PP-OCRv4 models exported to ONNX and run on
ONNX Runtime, so the image needs neither PaddlePaddle (a very large dependency) nor a second inference stack. It is installed `--no-deps`
with `opencv-python-headless` so no GUI libraries are pulled in. The detector is configured not to upscale small images and to merge
words into lines (`det_limit_type=max`, `unclip_ratio=2.0`); the library defaults split large poster text at every word gap.

## Contract (v1, stable)
All responses are JSON. `Authorization: Bearer <token>` is required on every route except `/healthz` when `AI_SIDECAR_TOKEN` is set
(compared in constant time; 401 otherwise). Errors are `{ "error": "<code>", "message": "..." }`, never a stack trace and never content from
the request. Every response carries `X-Request-Id`.

| Route | Request | Response |
|---|---|---|
| `GET /healthz` | none | `{status: ok\|loading\|degraded, models: {clip:{name,dim,loaded}, text:{name,dim,loaded}, whisper:{name,loaded}, ocr:{name,loaded}}, version}`, always 200. `loading` until clip, text and OCR are warm (about 25 s after start); `degraded` if one failed to load. Whisper is loaded on first use. |
| `POST /v1/embed/image` | multipart `file` (jpeg, png, webp, gif first frame; also bmp, tiff) | `{model, dim: 512, embedding}`, L2-normalised |
| `POST /v1/embed/text` | `{texts: [<=64 strings, each <=4000 chars], kind: "clip"\|"text", role?: "query"\|"passage"}` | `{model, dim, embeddings}`, L2-normalised. `clip`: 512-d, same space as images, truncated to 77 tokens. `text`: 384-d e5, `role` (default `passage`) selects the `query: ` / `passage: ` prefix, truncated to 512 tokens. |
| `POST /v1/tag` | multipart `file`, optional `labels` (JSON array, at most 2000 labels of at most 100 chars), `top_k` (default 12, max 100), `threshold` | `{model, tags: [{label, score}]}`, score = softmax over the label set of 100 x cosine, sorted descending. Label embeddings are cached per label set. |
| `POST /v1/ocr` | multipart `file`, optional `lang` | `{model, language, text, lines: [{text, score, box: [[x,y]x4]}]}`. `language` is detected from the text (null under 20 characters or low confidence). `warnings: [..]` is added when `lang` names a script the models cannot read. |
| `POST /v1/transcribe` | multipart `file` (anything FFmpeg decodes), optional `lang` (ISO), `max_seconds` (default 3600, max 7200) | `{model, language, duration, segments: [{startMs, endMs, text}]}`. VAD is on: silence gives `segments: []`. A video with no audio track gives `duration: 0, segments: []`. |
| `POST /v1/detect-language` | `{text}` | `{language, confidence}`; `"und"` with 0 when undeterminable (no letters). Chinese is `zh`. |

Error codes: `invalid_request` 400, `unauthorized` 401, `not_found` 404, `method_not_allowed` 405, `payload_too_large` 413 (upload over 25 MB),
`image_too_large` 413 (over 100 megapixels), `unsupported_media` 415 (cannot decode), `busy` 503 with `Retry-After`, `model_unavailable`
503 (model missing or failed to load, for example a Whisper size not baked into the image), `internal_error` 500.

**Default tag threshold.** The bundled vocabulary has about 1,200 labels (`app/data/default_labels.txt`, generic visual concepts, no brand or
customer names). Softmax over that many overlapping labels (city, downtown, skyline, ...) spreads probability so thinly that the specified
default of 0.12 returned no tags at all for many real photographs. When the caller sends no `labels` the default threshold is therefore
0.03 (env `AI_TAG_THRESHOLD_DEFAULT_LABELS`); with caller-supplied labels it stays 0.12 (`AI_TAG_THRESHOLD`). An explicit `threshold` always wins.
Tags are suggestions (D10): the consumer decides what to store.

### Limits and operations
- **Concurrency.** A global semaphore (`AI_MAX_CONCURRENCY`, default 2) bounds work. Extra requests queue up to `AI_QUEUE_TIMEOUT_SECONDS`
  (60), then get 503 `busy` with `Retry-After`. CPU threads per request default to `cores / concurrency`. Callers should retry on 503.
- **Memory.** About 1.2 GB after warm-up, 1.9 GB once Whisper has loaded, about 2.2 GB peak under a mixed burst; the compose limit is 4 GB.
- **Upload limit** 25 MB (`AI_MAX_UPLOAD_MB`), enforced while the body streams in, so an oversized chunked upload is cut off early.
- **Security.** Runs as uid 10001, read-only root, writable `/tmp` only. The logs are one JSON object per line and contain method, path,
  status, duration and byte counts, never request bodies, text, image or audio. SIGTERM is graceful (in-flight requests finish, 30 s cap).
- **Not horizontally clever.** One container does everything; to scale, run more containers behind a load balancer (stateless).

### Measured on the development machine
Apple Silicon, Docker with 8 CPUs, 4 threads per request, models warm, through the published port: image embedding 240-300 ms (2-3 megapixel
JPEG), tag 280 ms, OCR 90 ms for a text banner and about 1.4 s for a 2 megapixel photo, clip text 12 ms, e5 text 5 ms, language detection 3 ms,
transcription about 0.2x real time (11 s of speech in 2.3-3 s, so one hour of audio is roughly 12-15 minutes), 64 clip texts 0.8 s.

## Known limits
- **CLIP ViT-B/32** reads 77 tokens (about 60 words) of English; longer text is silently truncated and non-English queries work poorly.
  It sees a 224 px centre crop, so small objects at the edges are missed. It is weak at counting, reading text in pictures and fine
  distinctions (the sample laptop photo tags as "business card"). Zero-shot tags are suggestions, not facts.
- **OCR scripts.** The bundled PP-OCRv4 models read Latin script (English and most European languages), digits and Simplified Chinese.
  Devanagari (hi, mr), Tamil, Telugu, Arabic and the other scripts are **not** supported and come back empty or as garbage. Follow-up:
  PaddleOCR publishes per-script recognition models (`devanagari`, `ta`, `te`, `arabic`, ...) that can be added as extra ONNX files and
  selected by `lang`. They need an ONNX export and a dictionary each, so they are a separate step, not a blocker for Step 2.1.
- **Whisper `base`** (74 M parameters) is good on clear English and weaker on accents, noise, music and the Indian languages; it can
  hallucinate on near-silence (VAD is on to limit this). `small` is clearly better and roughly 2.5x slower; `AI_WHISPER_MODEL=small` after
  building with `WHISPER_MODELS=base,small`. The default decodes greedily (`AI_WHISPER_BEAM_SIZE=1`).
- **Formats.** HEIC/HEIF and RAW images are not decoded (convert to a JPEG rendition first). Animated GIF/WebP use frame 0.
- **Languages for text embeddings.** e5-small covers about 100 languages but is a small model; treat similarity scores as relative.

## Consequences
- `SelfHostedProvider` depends only on this HTTP contract. Replacing a model (for example CLIP ViT-L/14, 768-d) changes `dim` and
  `model`, so every stored vector must record the `model` string that produced it and a model change means a re-embed.
- The text-vector size is **384** (e5-small), not the 1024 mentioned in Step 1.7 of the build plan; the OpenSearch mapping for text vectors
  must use the `dim` the sidecar reports.
- **CI.** The sidecar is not built or scanned by the CI image matrix yet (the image is about 2 GB and downloads about 1 GB of models). To add
  it: append `deploy/ai-sidecar` to the matrix with the same Trivy/Grype step as the other images, build only `--target runtime`, cache the
  `models` stage, and run `make test-ai` (or `docker build --target test`) as a separate nightly job. The pinned Python versions and the
  base image `python:3.12-slim-bookworm` should be refreshed on the usual dependency cadence.
- Third-party licences are all permissive (MIT, Apache-2.0, BSD, HPND). OpenCV and PyAV wheels bundle FFmpeg libraries under LGPL;
  Debian's `ffmpeg` package contains GPL parts and is invoked only as a subprocess.
