from __future__ import annotations

import io

import numpy as np
import pytest
from app.errors import ApiError
from app.imaging import CLIP_MEAN, CLIP_STD, clip_preprocess, l2_normalize, load_image
from PIL import Image

from .helpers import encode, solid

MAX = 100_000_000


def test_preprocess_shape_dtype_and_determinism() -> None:
    img = solid((10, 200, 30), (640, 480))
    a, b = clip_preprocess(img), clip_preprocess(img)
    assert a.shape == (1, 3, 224, 224)
    assert a.dtype == np.float32
    assert a.flags["C_CONTIGUOUS"]
    assert np.array_equal(a, b)


def test_preprocess_normalisation_values() -> None:
    white = clip_preprocess(solid((255, 255, 255)))
    expected = ((1.0 - CLIP_MEAN) / CLIP_STD).reshape(1, 3, 1, 1)
    assert np.allclose(white, np.broadcast_to(expected, white.shape), atol=1e-5)
    black = clip_preprocess(solid((0, 0, 0)))
    assert np.allclose(black, np.broadcast_to((-CLIP_MEAN / CLIP_STD).reshape(1, 3, 1, 1), black.shape), atol=1e-5)


def test_preprocess_centre_crops_wide_images() -> None:
    img = Image.new("RGB", (900, 300), (255, 0, 0))
    img.paste((0, 0, 255), (300, 0, 600, 300))  # blue centre third
    out = clip_preprocess(img)
    blue_channel_value = (1.0 - CLIP_MEAN[2]) / CLIP_STD[2]
    assert np.allclose(out[0, 2, 8:-8, 8:-8], blue_channel_value, atol=0.05)  # the 224px crop sees only the blue centre


def test_preprocess_small_images_are_upscaled() -> None:
    assert clip_preprocess(solid((1, 2, 3), (16, 9))).shape == (1, 3, 224, 224)


def test_l2_normalize() -> None:
    v = l2_normalize(np.array([[3.0, 4.0], [0.0, 0.0]], dtype=np.float32))
    assert np.allclose(v[0], [0.6, 0.8])
    assert np.all(np.isfinite(v))


@pytest.mark.parametrize("fmt", ["PNG", "JPEG", "WEBP", "GIF", "BMP"])
def test_load_supported_formats(fmt: str) -> None:
    img = load_image(encode(solid((200, 30, 30), (64, 48)), fmt), MAX)
    assert img.mode == "RGB" and img.size == (64, 48)


def test_gif_uses_first_frame() -> None:
    first, second = solid((255, 0, 0), (32, 32)), solid((0, 0, 255), (32, 32))
    buf = io.BytesIO()
    first.save(buf, format="GIF", save_all=True, append_images=[second], duration=100, loop=0)
    px = load_image(buf.getvalue(), MAX).getpixel((5, 5))
    assert isinstance(px, tuple) and px[0] > 200 and px[2] < 50


def test_transparent_png_is_composited_on_white() -> None:
    rgba = Image.new("RGBA", (16, 16), (0, 0, 0, 0))
    assert load_image(encode(rgba), MAX).getpixel((3, 3)) == (255, 255, 255)


def test_exif_orientation_is_applied() -> None:
    img = Image.new("RGB", (40, 20), (255, 0, 0))
    exif = Image.Exif()
    exif[0x0112] = 6  # rotate 90 clockwise to display
    assert load_image(encode(img, "JPEG", exif=exif), MAX).size == (20, 40)


def test_garbage_and_unsupported_input() -> None:
    for payload in (b"not an image", b"\x00" * 100):
        with pytest.raises(ApiError) as e:
            load_image(payload, MAX)
        assert e.value.status == 415 and e.value.code == "unsupported_media"


def test_pixel_limit() -> None:
    with pytest.raises(ApiError) as e:
        load_image(encode(solid((1, 1, 1), (2000, 2000))), 1_000_000)
    assert e.value.status == 413 and e.value.code == "image_too_large"
