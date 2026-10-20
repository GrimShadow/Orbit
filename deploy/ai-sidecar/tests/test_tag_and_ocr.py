from __future__ import annotations

import json

from app.registry import Registry, load_default_labels
from fastapi.testclient import TestClient

from .helpers import beach, encode, field_and_sky, solid, text_image


def tag(client: TestClient, img, **form):
    data = {k: (v if isinstance(v, str) else json.dumps(v) if isinstance(v, list) else str(v)) for k, v in form.items()}
    return client.post("/v1/tag", files={"file": ("x.png", encode(img), "image/png")}, data=data)


def test_default_tagging_of_a_generated_scene(client: TestClient) -> None:
    r = tag(client, field_and_sky())
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["model"].startswith("clip-vit-base-patch32")
    tags = body["tags"]
    assert 1 <= len(tags) <= 12
    scores = [t["score"] for t in tags]
    assert scores == sorted(scores, reverse=True) and all(0.03 <= s <= 1.0 for s in scores) and sum(scores) <= 1.0001
    known = set(load_default_labels())
    assert all(t["label"] in known for t in tags)
    outdoors = {"field", "grass", "meadow", "grassland", "farmland", "sky", "clouds", "landscape photo", "green color",
                "countryside", "hill", "wheat field", "flower field", "park", "blue color", "horizon", "sunrise",
                "sunset", "rice paddy", "savanna", "garden", "illustration", "digital art", "cartoon", "vector graphic",
                "natural light", "bright scene", "aerial view of landscape", "vibrant colors", "colorful"}  # fmt: skip
    assert {t["label"] for t in tags} & outdoors, tags


def test_top_k_and_threshold(client: TestClient) -> None:
    assert len(tag(client, beach(), top_k=3, threshold=0.0).json()["tags"]) == 3
    assert len(tag(client, beach(), top_k=100, threshold=0.0).json()["tags"]) == 100
    strict = tag(client, beach(), threshold=0.9999).json()["tags"]
    assert len(strict) <= 1


def test_custom_labels_are_scored_over_the_given_set(client: TestClient) -> None:
    r = tag(client, solid((220, 20, 20)), labels=["red", "blue", "green"])
    tags = r.json()["tags"]
    assert tags[0]["label"] == "red" and tags[0]["score"] > 0.5
    assert [t["label"] for t in tags] == ["red"]  # default threshold for caller-supplied labels is 0.12
    r2 = tag(client, solid((20, 40, 220)), labels=["red", "blue", "green"])
    assert r2.json()["tags"][0]["label"] == "blue"


def test_label_embeddings_are_cached_per_label_set(client: TestClient, registry: Registry) -> None:
    clip = registry.clip.get()
    labels = ["zebra crossing", "traffic light", "bicycle lane"]
    tag(client, beach(), labels=labels)
    size = len(clip._label_cache)
    tag(client, field_and_sky(), labels=labels)
    assert len(clip._label_cache) == size  # same set: no new entry, no recompute
    tag(client, beach(), labels=[*labels, "pedestrian"])
    assert len(clip._label_cache) == size + 1


def test_tag_validation(client: TestClient) -> None:
    for form in (
        {"labels": "not json"},
        {"labels": json.dumps({"a": 1})},
        {"labels": json.dumps([1, 2])},
        {"labels": json.dumps(["x" * 101])},
        {"top_k": 0},
        {"top_k": 101},
        {"threshold": 1.5},
        {"threshold": -0.1},
    ):
        r = client.post("/v1/tag", files={"file": ("x.png", encode(solid((1, 2, 3))), "image/png")}, data=form)
        assert r.status_code == 400 and r.json()["error"] == "invalid_request", form


def ocr(client: TestClient, img, **form):
    return client.post("/v1/ocr", files={"file": ("x.png", encode(img), "image/png")}, data=form)


def test_ocr_reads_rendered_text(client: TestClient) -> None:
    r = ocr(client, text_image(["HELLO ORBIT 2026"]))
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["model"].startswith("rapidocr")
    assert " ".join(body["text"].upper().split()) == "HELLO ORBIT 2026", body
    assert len(body["lines"]) >= 1
    line = body["lines"][0]
    assert 0.5 <= line["score"] <= 1.0
    assert len(line["box"]) == 4 and all(len(p) == 2 for p in line["box"])


def test_ocr_multiline_and_language(client: TestClient) -> None:
    lines = ["Acme Roadshow opens on Monday", "Doors open at nine in the morning"]
    body = ocr(client, text_image(lines, 40)).json()
    assert body["text"].count("\n") == 1
    first, second = body["text"].lower().split("\n")
    assert "roadshow" in first and "morning" in second
    assert body["language"] == "en"
    ys = [min(p[1] for p in ln["box"]) for ln in body["lines"]]
    assert ys == sorted(ys)  # reading order, top to bottom


def test_ocr_blank_image_and_unsupported_language_hint(client: TestClient) -> None:
    body = ocr(client, solid((255, 255, 255), (300, 120))).json()
    assert body["text"] == "" and body["lines"] == [] and body["language"] is None
    warned = ocr(client, solid((255, 255, 255), (300, 120)), lang="hi").json()
    assert "warnings" in warned and "Devanagari" in warned["warnings"][0]
    assert "warnings" not in ocr(client, solid((255, 255, 255), (300, 120)), lang="en").json()
    assert ocr(client, solid((255, 255, 255), (300, 120)), lang="!!").status_code == 400
