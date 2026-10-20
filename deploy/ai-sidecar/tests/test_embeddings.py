from __future__ import annotations

import numpy as np
from fastapi.testclient import TestClient

from .helpers import beach, encode, field_and_sky, solid, stripes


def post_image(client: TestClient, img_bytes: bytes, name: str = "x.png"):
    return client.post("/v1/embed/image", files={"file": (name, img_bytes, "image/png")})


def embed_images(client: TestClient, images: list[bytes]) -> np.ndarray:
    return np.array([post_image(client, b).json()["embedding"] for b in images])


def embed_clip_text(client: TestClient, texts: list[str]) -> np.ndarray:
    r = client.post("/v1/embed/text", json={"texts": texts, "kind": "clip"})
    assert r.status_code == 200, r.text
    return np.array(r.json()["embeddings"])


def test_image_embedding_shape_norm_and_determinism(client: TestClient) -> None:
    png = encode(field_and_sky())
    r = post_image(client, png)
    assert r.status_code == 200
    body = r.json()
    assert body["model"].startswith("clip-vit-base-patch32")
    assert body["dim"] == 512 and len(body["embedding"]) == 512
    assert abs(np.linalg.norm(body["embedding"]) - 1.0) < 1e-4
    assert post_image(client, png).json()["embedding"] == body["embedding"]


def test_same_picture_in_different_formats_is_nearly_identical(client: TestClient) -> None:
    img = field_and_sky()
    a = np.array(post_image(client, encode(img, "PNG")).json()["embedding"])
    b = np.array(post_image(client, encode(img, "JPEG", quality=90), "x.jpg").json()["embedding"])
    c = np.array(post_image(client, encode(img, "WEBP", quality=90), "x.webp").json()["embedding"])
    assert float(a @ b) > 0.95 and float(a @ c) > 0.95


def test_red_square_is_closer_to_red_than_blue(client: TestClient) -> None:
    img = embed_images(client, [encode(solid((220, 20, 20)))])[0]
    red, blue = embed_clip_text(client, ["red", "blue"])
    assert float(img @ red) > float(img @ blue)
    red2, blue2 = embed_clip_text(client, ["a red image", "a blue image"])
    assert float(img @ red2) > float(img @ blue2)


def test_text_to_image_retrieval_over_generated_images(client: TestClient) -> None:
    images = {
        "a red image": solid((220, 20, 20)),
        "a blue image": solid((20, 40, 220)),
        "a green image": solid((20, 170, 40)),
        "a yellow image": solid((245, 225, 20)),
        "black and white vertical stripes": stripes(),
        "a beach with sand, sea and sky": beach(),
    }
    queries = list(images)
    img_emb = embed_images(client, [encode(i) for i in images.values()])
    txt_emb = embed_clip_text(client, queries)
    sims = txt_emb @ img_emb.T  # rows: queries, columns: images (same order)
    for q in range(len(queries)):
        assert int(np.argmax(sims[q])) == q, (queries[q], sims[q].round(3).tolist())


def test_clip_text_is_normalised_case_insensitive_and_truncated_to_77_tokens(client: TestClient) -> None:
    a, b = embed_clip_text(client, ["A Red Car", "a red car"])
    assert abs(np.linalg.norm(a) - 1.0) < 1e-4
    assert np.allclose(a, b, atol=1e-5)
    # "cat" is one token: 75 of them fill the 77-token context (BOS + EOS); anything beyond is ignored.
    full, longer = embed_clip_text(client, ["cat " * 75, "cat " * 600])
    assert np.allclose(full, longer, atol=1e-5)
    assert not np.allclose(embed_clip_text(client, ["cat " * 20])[0], full, atol=1e-3)


def test_clip_text_accepts_max_batch_and_length(client: TestClient) -> None:
    r = client.post("/v1/embed/text", json={"texts": ["x" * 4000] * 64, "kind": "clip"})
    assert r.status_code == 200 and len(r.json()["embeddings"]) == 64 and r.json()["dim"] == 512


def test_multilingual_text_embeddings(client: TestClient) -> None:
    def embed(texts: list[str], role: str | None = None) -> np.ndarray:
        body: dict = {"texts": texts, "kind": "text"}
        if role:
            body["role"] = role
        r = client.post("/v1/embed/text", json=body)
        assert r.status_code == 200, r.text
        j = r.json()
        assert j["model"] == "multilingual-e5-small" and j["dim"] == 384
        return np.array(j["embeddings"])

    passages = embed(
        [
            "To reset your password, open Settings, choose Security and press Reset password.",
            "The annual report shows revenue growth in all regions during the last fiscal year.",
            "Para restablecer la contraseña, abra Configuración, elija Seguridad y pulse Restablecer.",
        ]
    )
    assert np.allclose(np.linalg.norm(passages, axis=1), 1.0, atol=1e-4)
    query = embed(["how do I change my password?"], "query")[0]
    sims = passages @ query
    assert sims[0] > sims[1] and sims[2] > sims[1]  # on-topic beats off-topic, also across languages
    # role matters (prefix), default role is "passage"
    assert not np.allclose(embed(["hello"], "query"), embed(["hello"], "passage"), atol=1e-4)
    assert np.allclose(embed(["hello"]), embed(["hello"], "passage"), atol=1e-6)


def test_text_embed_validation(client: TestClient) -> None:
    for body in (
        {"texts": [], "kind": "clip"},
        {"texts": ["a"] * 65, "kind": "clip"},
        {"texts": ["x" * 4001], "kind": "text"},
        {"texts": ["a"], "kind": "other"},
        {"texts": ["a"]},
        {"texts": ["a"], "kind": "text", "role": "nope"},
    ):
        r = client.post("/v1/embed/text", json=body)
        assert r.status_code == 400, body
        assert r.json()["error"] == "invalid_request" and "message" in r.json()
