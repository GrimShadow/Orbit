from __future__ import annotations

import asyncio
import logging
import threading
import time

import pytest
from app.config import Settings
from app.errors import ApiError
from app.limits import Limiter
from app.registry import Registry
from fastapi.testclient import TestClient

from .helpers import encode, solid

PNG = encode(solid((200, 10, 10), (64, 64)))


def embed(c: TestClient, **kw):
    return c.post("/v1/embed/image", files={"file": ("x.png", PNG, "image/png")}, **kw)


# ----------------------------------------------------------------------------- health
def test_healthz_reports_models_and_version(client: TestClient) -> None:
    r = client.get("/healthz")
    assert r.status_code == 200
    body = r.json()
    assert body["status"] == "ok" and body["version"]
    assert body["models"]["clip"] == {"name": "clip-vit-base-patch32", "dim": 512, "loaded": True}
    assert body["models"]["text"] == {"name": "multilingual-e5-small", "dim": 384, "loaded": True}
    assert body["models"]["ocr"]["loaded"] is True and body["models"]["ocr"]["name"]
    assert set(body["models"]["whisper"]) == {"name", "loaded"}


def test_healthz_loading_then_degraded(base_settings: Settings, tmp_path) -> None:
    broken = Settings(models_dir=tmp_path, tmp_dir=tmp_path)  # no model files at all
    reg = Registry(broken)
    assert reg.health("t")["status"] == "loading"  # warm-up not finished yet
    reg.start_warmup()
    assert reg.wait_warm(60)
    h = reg.health("t")
    assert h["status"] == "degraded" and h["models"]["clip"]["loaded"] is False
    with TestClient(__import__("app.main", fromlist=["create_app"]).create_app(broken, reg)) as c:
        assert c.get("/healthz").status_code == 200  # 200 even when degraded
        r = embed(c)
        assert r.status_code == 503 and r.json()["error"] == "model_unavailable"


# ----------------------------------------------------------------------------- auth
def test_bearer_token_required_on_everything_but_healthz(make_client) -> None:
    c = make_client(token="s3cret-token")
    assert c.get("/healthz").status_code == 200
    for bad in (
        {},
        {"Authorization": "Bearer nope"},
        {"Authorization": "Basic s3cret-token"},
        {"Authorization": "s3cret-token"},
    ):
        r = embed(c, headers=bad)
        assert r.status_code == 401, bad
        assert r.json()["error"] == "unauthorized" and r.headers["www-authenticate"] == "Bearer"
    assert c.post("/v1/embed/text", json={"texts": ["a"], "kind": "clip"}).status_code == 401
    assert c.get("/nope").status_code == 401  # nothing is revealed to unauthenticated callers
    ok = embed(c, headers={"Authorization": "Bearer s3cret-token"})
    assert ok.status_code == 200 and ok.json()["dim"] == 512


def test_no_token_means_open(client: TestClient) -> None:
    assert embed(client).status_code == 200


# ----------------------------------------------------------------------------- limits and errors
def test_default_upload_limit_is_25_mb() -> None:
    assert Settings().max_upload_bytes == 25 * 1024 * 1024


def test_oversized_upload_is_rejected_with_413(make_client) -> None:
    c = make_client(max_upload_bytes=100_000)
    big = b"\x89PNG" + b"0" * 2_000_000
    r = c.post("/v1/embed/image", files={"file": ("x.png", big, "image/png")})
    assert r.status_code == 413 and r.json()["error"] == "payload_too_large"

    def chunks():  # no Content-Length: the body is counted as it streams in
        yield b'--b\r\nContent-Disposition: form-data; name="file"; filename="x.png"\r\n\r\n'
        for _ in range(40):
            yield b"0" * 100_000
        yield b"\r\n--b--\r\n"

    r2 = c.post("/v1/embed/image", content=chunks(), headers={"Content-Type": "multipart/form-data; boundary=b"})
    assert r2.status_code == 413 and r2.json()["error"] == "payload_too_large"
    assert embed(c).status_code == 200  # the service is fine afterwards


def test_file_limit_applies_to_the_file_not_the_framing(make_client) -> None:
    c = make_client(max_upload_bytes=len(PNG) - 1)
    assert embed(c).status_code == 413


def test_error_shapes(client: TestClient) -> None:
    cases = [
        (client.post("/v1/embed/image"), 400, "invalid_request"),
        (client.post("/v1/embed/image", files={"file": ("x.txt", b"hello", "text/plain")}), 415, "unsupported_media"),
        (client.post("/v1/embed/image", files={"file": ("x.png", b"", "image/png")}), 400, "invalid_request"),
        (client.get("/v1/embed/image"), 405, "method_not_allowed"),
        (client.get("/nothing-here"), 404, "not_found"),
        (
            client.post("/v1/embed/text", content=b"{broken", headers={"content-type": "application/json"}),
            400,
            "invalid_request",
        ),
        (client.post("/v1/detect-language", json={}), 400, "invalid_request"),
    ]
    for resp, status, code in cases:
        assert resp.status_code == status, resp.text
        assert set(resp.json()) == {"error", "message"} and resp.json()["error"] == code


def test_internal_errors_do_not_leak_details(make_client, registry: Registry, monkeypatch: pytest.MonkeyPatch) -> None:
    c = make_client()
    clip = registry.clip.get()

    def boom(*_a, **_k):
        raise RuntimeError("secret internal detail /srv/app/clip.py")

    monkeypatch.setattr(clip, "embed_image", boom)
    r = embed(c)
    assert r.status_code == 500
    assert r.json() == {"error": "internal_error", "message": "internal server error"}
    assert "Traceback" not in r.text and "secret" not in r.text


def test_request_id_header(client: TestClient) -> None:
    assert len(client.get("/healthz").headers["x-request-id"]) >= 16
    assert client.get("/healthz", headers={"X-Request-Id": "abc-123"}).headers["x-request-id"] == "abc-123"
    assert (
        client.get("/healthz", headers={"X-Request-Id": "bad id\twith spaces"}).headers["x-request-id"]
        != "bad id\twith spaces"
    )


def test_logs_never_contain_request_content(client: TestClient, caplog: pytest.LogCaptureFixture) -> None:
    secret = "ultra-confidential-sentence-4711"
    with caplog.at_level(logging.DEBUG):
        client.post("/v1/embed/text", json={"texts": [secret], "kind": "text"})
        client.post("/v1/detect-language", json={"text": secret})
        client.post("/v1/embed/text", json={"texts": [secret], "kind": "bogus"})  # validation error path
    blob = " ".join(f"{r.getMessage()} {getattr(r, 'fields', '')}" for r in caplog.records)
    assert "request" in blob and secret not in blob


# ----------------------------------------------------------------------------- concurrency
def test_limiter_caps_parallelism() -> None:
    async def main() -> int:
        lim = Limiter(2, 5.0, 3)
        running = peak = 0

        def work() -> None:
            nonlocal running, peak
            running += 1
            peak = max(peak, running)
            time.sleep(0.05)
            running -= 1

        await asyncio.gather(*[lim.run(work) for _ in range(8)])
        return peak

    assert asyncio.run(main()) == 2


def test_limiter_times_out_with_busy_and_retry_after() -> None:
    async def main() -> ApiError:
        lim = Limiter(1, 0.1, 7)
        async with lim.slot():
            with pytest.raises(ApiError) as e:
                async with lim.slot():
                    pass
            return e.value

    err = asyncio.run(main())
    assert err.status == 503 and err.code == "busy" and err.headers["Retry-After"] == "7"


def test_http_busy_when_burst_exceeds_capacity(
    make_client, registry: Registry, monkeypatch: pytest.MonkeyPatch
) -> None:
    c = make_client(max_concurrency=1, queue_timeout_s=0.3, retry_after_s=4)
    clip = registry.clip.get()
    real = clip.embed_image

    def slow(img):
        time.sleep(1.2)
        return real(img)

    monkeypatch.setattr(clip, "embed_image", slow)
    results: list = []
    lock = threading.Lock()

    def call() -> None:
        r = embed(c)
        with lock:
            results.append(r)

    threads = [threading.Thread(target=call) for _ in range(3)]
    for t in threads:
        t.start()
        time.sleep(0.05)
    for t in threads:
        t.join(30)
    codes = sorted(r.status_code for r in results)
    assert codes == [200, 503, 503], codes
    busy = next(r for r in results if r.status_code == 503)
    assert busy.json()["error"] == "busy" and busy.headers["retry-after"] == "4"
