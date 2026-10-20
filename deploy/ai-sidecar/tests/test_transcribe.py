from __future__ import annotations

import shutil
import subprocess
import wave
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

SPEECH = "Hello. This is a test of the Orbit speech recognition service. The weather is sunny today."

needs_espeak = pytest.mark.skipif(shutil.which("espeak-ng") is None, reason="espeak-ng not installed")


def silence_wav(path: Path, seconds: float = 3.0) -> Path:
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(16000)
        w.writeframes(b"\x00\x00" * int(16000 * seconds))
    return path


def post(client: TestClient, path: Path, name: str | None = None, **form):
    with path.open("rb") as f:
        return client.post("/v1/transcribe", files={"file": (name or path.name, f.read(), "audio/wav")}, data=form)


@pytest.fixture(scope="module")
def speech_wav(tmp_path_factory: pytest.TempPathFactory) -> Path:
    if shutil.which("espeak-ng") is None:
        pytest.skip("espeak-ng not installed")
    out = tmp_path_factory.mktemp("speech") / "speech.wav"
    subprocess.run(["espeak-ng", "-v", "en-us", "-s", "150", "-w", str(out), SPEECH], check=True)
    return out


@needs_espeak
def test_transcribes_synthetic_speech_with_timestamps(client: TestClient, speech_wav: Path) -> None:
    r = post(client, speech_wav)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["model"].startswith("faster-whisper-")
    assert body["language"] == "en"
    assert body["duration"] > 4
    segs = body["segments"]
    assert segs, body
    prev_end = 0
    for s in segs:
        assert isinstance(s["startMs"], int) and isinstance(s["endMs"], int) and s["text"]
        assert s["startMs"] >= prev_end - 50 and s["endMs"] > s["startMs"]
        assert s["endMs"] <= body["duration"] * 1000 + 500
        prev_end = s["endMs"]
    text = " ".join(s["text"] for s in segs).lower()
    assert "test" in text and ("speech" in text or "recognition" in text), text


@needs_espeak
def test_forced_language_and_other_containers(client: TestClient, speech_wav: Path, tmp_path: Path) -> None:
    assert post(client, speech_wav, lang="en").json()["language"] == "en"
    for ext, codec in (("mp3", "libmp3lame"), ("m4a", "aac"), ("ogg", "libvorbis")):
        out = tmp_path / f"speech.{ext}"
        res = subprocess.run(
            ["ffmpeg", "-y", "-loglevel", "error", "-i", str(speech_wav), "-c:a", codec, str(out)], check=False
        )
        if res.returncode != 0:
            continue  # encoder not in this ffmpeg build; decoding of the others is still covered
        r = post(client, out)
        assert r.status_code == 200, (ext, r.text)
        assert "test" in " ".join(s["text"] for s in r.json()["segments"]).lower(), ext


@needs_espeak
def test_max_seconds_truncates(client: TestClient, speech_wav: Path) -> None:
    body = post(client, speech_wav, max_seconds="2").json()
    assert body["duration"] <= 2.05


def test_silence_gives_empty_segments(client: TestClient, tmp_path: Path) -> None:
    r = post(client, silence_wav(tmp_path / "silence.wav"))
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["segments"] == [] and abs(body["duration"] - 3.0) < 0.05
    assert body["language"] is None


def test_transcribe_errors(client: TestClient, tmp_path: Path) -> None:
    junk = tmp_path / "junk.wav"
    junk.write_bytes(b"this is not audio" * 100)
    r = post(client, junk)
    assert r.status_code == 415 and r.json()["error"] == "unsupported_media"
    empty = tmp_path / "empty.wav"
    empty.write_bytes(b"")
    assert post(client, empty).status_code == 400
    wav = silence_wav(tmp_path / "s.wav", 1.0)
    for form in ({"lang": "xx-yy-zz-1"}, {"lang": "zz"}, {"max_seconds": "0"}, {"max_seconds": "999999"}):
        r = post(client, wav, **form)
        assert r.status_code == 400 and r.json()["error"] == "invalid_request", form
    assert client.post("/v1/transcribe").status_code == 400


def test_video_without_audio_returns_empty_transcript(client: TestClient, tmp_path: Path) -> None:
    out = tmp_path / "silent.mp4"
    res = subprocess.run(
        ["ffmpeg", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=64x64:d=1", "-an", str(out)],
        check=False,
    )
    if res.returncode != 0:
        pytest.skip("ffmpeg cannot synthesise a test video")
    r = post(client, out)
    assert r.status_code == 200 and r.json()["segments"] == [] and r.json()["duration"] == 0.0
