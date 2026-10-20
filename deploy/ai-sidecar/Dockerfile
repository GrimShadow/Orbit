# syntax=docker/dockerfile:1.7
# Orbit AI sidecar: CPU-only, ONNX Runtime / CTranslate2, no PyTorch. One Dockerfile for linux/arm64 and linux/amd64.
#
#   docker build -t orbit-ai-sidecar .                                   # runtime image (default target)
#   docker build --target test -t orbit-ai-sidecar-test . && docker run --rm orbit-ai-sidecar-test
#
# Build arguments
#   CLIP_VARIANT    fp32 (default, best quality) | int8 (about 330 MB smaller, a little less accurate)
#   WHISPER_MODELS  comma separated sizes to bake in: tiny | base | small   (default: base)
#   HF_ENDPOINT     Hugging Face mirror, if huggingface.co is not reachable from the build host
# Air-gapped builds: pre-seed the models directory (layout in scripts/fetch_models.py) and pass
#   --build-context modelseed=/path/to/models   (files with a matching SHA-256 are kept, nothing is downloaded).
# Model versions and checksums are pinned in models.lock.json.

ARG PYTHON_IMAGE=python:3.12-slim-bookworm

# ---------------------------------------------------------------- models (cached independently of code)
FROM scratch AS modelseed

FROM ${PYTHON_IMAGE} AS models
ARG CLIP_VARIANT=fp32
ARG WHISPER_MODELS=base
ARG HF_ENDPOINT=https://huggingface.co
ENV HF_ENDPOINT=${HF_ENDPOINT}
WORKDIR /build
COPY --from=modelseed / /models/
COPY models.lock.json ./
COPY scripts/fetch_models.py scripts/
RUN python scripts/fetch_models.py --out /models --clip-variant "${CLIP_VARIANT}" --whisper "${WHISPER_MODELS}"

# ---------------------------------------------------------------- python dependencies
FROM ${PYTHON_IMAGE} AS deps
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
RUN python -m venv /opt/venv
ENV PATH=/opt/venv/bin:$PATH
COPY requirements.txt requirements-ocr.txt ./
RUN pip install -r requirements.txt && pip install --no-deps -r requirements-ocr.txt

# ---------------------------------------------------------------- shared base
FROM ${PYTHON_IMAGE} AS base
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libgomp1 \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10001 --no-create-home --shell /usr/sbin/nologin app
COPY --from=deps /opt/venv /opt/venv
COPY --from=models /models /models
WORKDIR /srv
COPY app ./app
COPY scripts/healthcheck.py ./scripts/healthcheck.py
RUN python -m compileall -q app
ENV PATH=/opt/venv/bin:$PATH \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    HF_HUB_OFFLINE=1 \
    HOME=/tmp \
    AI_MODELS_DIR=/models \
    AI_TMP_DIR=/tmp \
    AI_PORT=8080

# ---------------------------------------------------------------- test target
FROM base AS test
RUN apt-get update \
    && apt-get install -y --no-install-recommends espeak-ng \
    && rm -rf /var/lib/apt/lists/*
COPY requirements-test.txt pyproject.toml ./
RUN pip install --no-cache-dir -r requirements-test.txt
COPY tests ./tests
ENV RUFF_CACHE_DIR=/tmp/ruff MYPY_CACHE_DIR=/tmp/mypy
USER 10001
CMD ["sh", "-c", "ruff check . && ruff format --check . && mypy app && pytest"]

# ---------------------------------------------------------------- runtime (last stage = default target)
FROM base AS runtime
USER 10001
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=90s --retries=12 \
    CMD ["python", "/srv/scripts/healthcheck.py"]
STOPSIGNAL SIGTERM
CMD ["python", "-m", "app"]
