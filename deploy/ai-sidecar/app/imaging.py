"""Image decoding and CLIP preprocessing (Pillow + numpy only: no torchvision)."""

from __future__ import annotations

import io

import numpy as np
from PIL import Image, ImageOps, UnidentifiedImageError

from .errors import ApiError

ALLOWED_FORMATS = {"JPEG", "MPO", "PNG", "WEBP", "GIF", "BMP", "TIFF"}

CLIP_SIZE = 224
CLIP_MEAN = np.array([0.48145466, 0.4578275, 0.40821073], dtype=np.float32)
CLIP_STD = np.array([0.26862954, 0.26130258, 0.27577711], dtype=np.float32)


def load_image(data: bytes, max_pixels: int) -> Image.Image:
    """Decode to an upright RGB image. GIF/WebP animations use their first frame."""
    Image.MAX_IMAGE_PIXELS = max_pixels
    try:
        with Image.open(io.BytesIO(data)) as img:
            if img.format not in ALLOWED_FORMATS:
                raise ApiError(415, "unsupported_media", "unsupported image format (jpeg, png, webp, gif)")
            if img.width * img.height > max_pixels:
                raise ApiError(413, "image_too_large", f"image has more than {max_pixels} pixels")
            img.seek(0)
            img.load()
            out = ImageOps.exif_transpose(img)
            return _to_rgb(out)
    except ApiError:
        raise
    except Image.DecompressionBombError as exc:
        raise ApiError(413, "image_too_large", "image is too large to decode") from exc
    except (UnidentifiedImageError, OSError, ValueError, SyntaxError, EOFError) as exc:
        raise ApiError(415, "unsupported_media", "could not decode the image") from exc


def _to_rgb(img: Image.Image) -> Image.Image:
    if img.mode in ("RGBA", "LA") or (img.mode == "P" and "transparency" in img.info):
        rgba = img.convert("RGBA")
        background = Image.new("RGBA", rgba.size, (255, 255, 255, 255))
        return Image.alpha_composite(background, rgba).convert("RGB")
    return img.convert("RGB")


def clip_preprocess(img: Image.Image) -> np.ndarray:
    """Shortest side -> 224 (bicubic), centre crop, scale to [0,1], normalise. Returns (1,3,224,224) float32."""
    w, h = img.size
    scale = CLIP_SIZE / min(w, h)
    nw, nh = max(CLIP_SIZE, round(w * scale)), max(CLIP_SIZE, round(h * scale))
    resized = img.resize((nw, nh), Image.Resampling.BICUBIC)
    left, top = (nw - CLIP_SIZE) // 2, (nh - CLIP_SIZE) // 2
    cropped = resized.crop((left, top, left + CLIP_SIZE, top + CLIP_SIZE))
    arr = np.asarray(cropped, dtype=np.float32) / 255.0
    arr = (arr - CLIP_MEAN) / CLIP_STD
    return np.ascontiguousarray(arr.transpose(2, 0, 1)[None, ...], dtype=np.float32)


def l2_normalize(x: np.ndarray) -> np.ndarray:
    norm = np.linalg.norm(x, axis=-1, keepdims=True)
    return x / np.maximum(norm, 1e-12)
