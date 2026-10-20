"""Programmatic fixtures: no binary files are checked in."""

from __future__ import annotations

import io

from PIL import Image, ImageDraw, ImageFont


def encode(img: Image.Image, fmt: str = "PNG", **kwargs: object) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format=fmt, **kwargs)  # type: ignore[arg-type]
    return buf.getvalue()


def solid(color: tuple[int, int, int], size: tuple[int, int] = (256, 256)) -> Image.Image:
    return Image.new("RGB", size, color)


def stripes(size: tuple[int, int] = (256, 256), n: int = 8) -> Image.Image:
    img = Image.new("RGB", size, (255, 255, 255))
    d = ImageDraw.Draw(img)
    w = size[0] // n
    for i in range(0, n, 2):
        d.rectangle([i * w, 0, (i + 1) * w, size[1]], fill=(0, 0, 0))
    return img


def field_and_sky(size: tuple[int, int] = (320, 240)) -> Image.Image:
    """Blue sky over a green field with a small yellow sun."""
    img = Image.new("RGB", size, (110, 170, 235))
    d = ImageDraw.Draw(img)
    d.rectangle([0, size[1] // 2, size[0], size[1]], fill=(60, 150, 60))
    d.ellipse([size[0] - 90, 20, size[0] - 40, 70], fill=(255, 230, 40))
    return img


def beach(size: tuple[int, int] = (320, 240)) -> Image.Image:
    """Sky, sea and sand in bands."""
    img = Image.new("RGB", size, (140, 190, 240))
    d = ImageDraw.Draw(img)
    d.rectangle([0, int(size[1] * 0.40), size[0], int(size[1] * 0.65)], fill=(20, 90, 170))
    d.rectangle([0, int(size[1] * 0.65), size[0], size[1]], fill=(235, 210, 150))
    return img


def font(size: int) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    return ImageFont.load_default(size=size)


def text_image(lines: list[str], size_px: int = 56, pad: int = 30) -> Image.Image:
    f = font(size_px)
    probe = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    widths = [probe.textbbox((0, 0), line, font=f)[2] for line in lines]
    line_h = int(size_px * 1.5)
    img = Image.new("RGB", (max(widths) + 2 * pad, line_h * len(lines) + 2 * pad), (255, 255, 255))
    d = ImageDraw.Draw(img)
    for i, line in enumerate(lines):
        d.text((pad, pad + i * line_h), line, fill=(0, 0, 0), font=f)
    return img
