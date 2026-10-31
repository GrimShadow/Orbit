"""Sample media for the demo library, generated with the Python standard library (plus ffmpeg/afconvert when installed).

Everything is deterministic: the same input gives the same bytes, so the library looks the same on every machine and run. All names and
artwork are fictional (Acme Motors, Roadster, Trail, Sedan, Lumen).
"""
from __future__ import annotations

import math
import random
import shutil
import struct
import subprocess
import tempfile
import wave
import zlib
from pathlib import Path

Color = tuple[int, int, int]


def hexcolor(h: str) -> Color:
    h = h.lstrip("#")
    return int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)


def mix(a: Color, b: Color, t: float) -> Color:
    t = max(0.0, min(1.0, t))
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))  # type: ignore[return-value]


# ---- a tiny raster canvas -----------------------------------------------------------------------------------------------
class Canvas:
    def __init__(self, w: int, h: int, bg: Color = (0, 0, 0)):
        self.w, self.h = w, h
        self.rows = [bytearray(bytes(bg) * w) for _ in range(h)]

    # spans -------------------------------------------------------------------------------------------------------------
    def span(self, y: int, x0: int, x1: int, c: Color, a: float = 1.0) -> None:
        if y < 0 or y >= self.h:
            return
        x0, x1 = max(0, int(x0)), min(self.w, int(x1))
        if x1 <= x0:
            return
        row = self.rows[y]
        if a >= 0.999:
            row[x0 * 3:x1 * 3] = bytes(c) * (x1 - x0)
            return
        seg = row[x0 * 3:x1 * 3]
        for ch in range(3):
            table = bytes(int(v + (c[ch] - v) * a) for v in range(256))
            seg[ch::3] = seg[ch::3].translate(table)
        row[x0 * 3:x1 * 3] = seg

    def vgradient(self, stops: list[tuple[float, Color]], y0: int = 0, y1: int | None = None) -> None:
        y1 = self.h if y1 is None else y1
        for y in range(y0, y1):
            t = (y - y0) / max(1, y1 - y0 - 1)
            for i in range(len(stops) - 1):
                (ta, ca), (tb, cb) = stops[i], stops[i + 1]
                if ta <= t <= tb:
                    self.span(y, 0, self.w, mix(ca, cb, (t - ta) / max(1e-9, tb - ta)))
                    break

    def rect(self, x0: float, y0: float, x1: float, y1: float, c: Color, a: float = 1.0) -> None:
        for y in range(int(y0), int(y1)):
            self.span(y, x0, x1, c, a)

    def circle(self, cx: float, cy: float, r: float, c: Color, a: float = 1.0) -> None:
        for y in range(int(cy - r), int(cy + r) + 1):
            dy = y - cy
            if abs(dy) <= r:
                dx = math.sqrt(r * r - dy * dy)
                self.span(y, cx - dx, cx + dx, c, a)

    def ellipse(self, cx: float, cy: float, rx: float, ry: float, c: Color, a: float = 1.0) -> None:
        for y in range(int(cy - ry), int(cy + ry) + 1):
            k = 1 - ((y - cy) / ry) ** 2
            if k >= 0:
                dx = rx * math.sqrt(k)
                self.span(y, cx - dx, cx + dx, c, a)

    def glow(self, cx: float, cy: float, r: float, c: Color, strength: float = 0.5) -> None:
        steps = 28
        for i in range(steps, 0, -1):
            self.circle(cx, cy, r * i / steps, c, strength * 0.16 / steps * 3 + 0.004)

    def polygon(self, pts: list[tuple[float, float]], c: Color, a: float = 1.0) -> None:
        ys = [p[1] for p in pts]
        n = len(pts)
        for y in range(max(0, int(min(ys))), min(self.h, int(max(ys)) + 1)):
            xs = []
            for i in range(n):
                (xa, ya), (xb, yb) = pts[i], pts[(i + 1) % n]
                if (ya <= y < yb) or (yb <= y < ya):
                    xs.append(xa + (y - ya) * (xb - xa) / (yb - ya))
            xs.sort()
            for j in range(0, len(xs) - 1, 2):
                self.span(y, xs[j], xs[j + 1], c, a)

    def ridge(self, base_y: float, amp: float, seed: int, c: Color, a: float = 1.0, freq: float = 1.0) -> None:
        rnd = random.Random(seed)
        ph = [rnd.uniform(0, 6.28) for _ in range(3)]
        for x in range(self.w):
            t = x / self.w * math.pi * 2 * freq
            y = base_y - amp * (0.55 * math.sin(t + ph[0]) + 0.3 * math.sin(2.3 * t + ph[1]) + 0.15 * math.sin(5.1 * t + ph[2]) + 0.6)
            for yy in range(int(y), self.h):
                self.rows[yy][x * 3:x * 3 + 3] = bytes(c) if a >= 0.999 else bytes(int(self.rows[yy][x * 3 + k] + (c[k] - self.rows[yy][x * 3 + k]) * a) for k in range(3))

    def stars(self, seed: int, count: int, y_max: int) -> None:
        rnd = random.Random(seed)
        for _ in range(count):
            x, y = rnd.randrange(self.w), rnd.randrange(y_max)
            self.circle(x, y, rnd.choice((0.8, 1.0, 1.4)), (255, 250, 235), rnd.uniform(0.4, 0.9))

    def vignette(self, strength: float = 0.35) -> None:
        for y in range(self.h):
            dy = (y / self.h - 0.5) * 2
            f = 1 - strength * max(0.0, abs(dy) - 0.45) ** 2 * 3
            table = bytes(int(v * f) for v in range(256))
            self.rows[y] = bytearray(self.rows[y].translate(table))

    # output ------------------------------------------------------------------------------------------------------------
    def png(self) -> bytes:
        raw = b"".join(b"\x00" + bytes(r) for r in self.rows)

        def chunk(tag: bytes, data: bytes) -> bytes:
            body = tag + data
            return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

        return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", self.w, self.h, 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))

    # text --------------------------------------------------------------------------------------------------------------
    def text(self, x: int, y: int, s: str, c: Color, size: int = 4, a: float = 1.0, spacing: int = 1) -> int:
        """Draws s with a 5x7 pixel font at integer scale `size`; returns the x after the last glyph."""
        for ch in s.upper():
            g = FONT.get(ch, FONT["?"])
            for ry, bits in enumerate(g):
                for rx in range(5):
                    if bits & (1 << (4 - rx)):
                        self.rect(x + rx * size, y + ry * size, x + (rx + 1) * size, y + (ry + 1) * size, c, a)
            x += (5 + spacing) * size
        return x

    def text_width(self, s: str, size: int = 4, spacing: int = 1) -> int:
        return len(s) * (5 + spacing) * size - spacing * size


def _glyphs(rows: dict[str, str]) -> dict[str, tuple[int, ...]]:
    return {k: tuple(int(r, 2) for r in v.split("/")) for k, v in rows.items()}


FONT = _glyphs({
    "A": "01110/10001/10001/11111/10001/10001/10001", "B": "11110/10001/10001/11110/10001/10001/11110",
    "C": "01110/10001/10000/10000/10000/10001/01110", "D": "11110/10001/10001/10001/10001/10001/11110",
    "E": "11111/10000/10000/11110/10000/10000/11111", "F": "11111/10000/10000/11110/10000/10000/10000",
    "G": "01110/10001/10000/10111/10001/10001/01111", "H": "10001/10001/10001/11111/10001/10001/10001",
    "I": "01110/00100/00100/00100/00100/00100/01110", "J": "00111/00010/00010/00010/00010/10010/01100",
    "K": "10001/10010/10100/11000/10100/10010/10001", "L": "10000/10000/10000/10000/10000/10000/11111",
    "M": "10001/11011/10101/10101/10001/10001/10001", "N": "10001/11001/10101/10011/10001/10001/10001",
    "O": "01110/10001/10001/10001/10001/10001/01110", "P": "11110/10001/10001/11110/10000/10000/10000",
    "Q": "01110/10001/10001/10001/10101/10010/01101", "R": "11110/10001/10001/11110/10100/10010/10001",
    "S": "01111/10000/10000/01110/00001/00001/11110", "T": "11111/00100/00100/00100/00100/00100/00100",
    "U": "10001/10001/10001/10001/10001/10001/01110", "V": "10001/10001/10001/10001/10001/01010/00100",
    "W": "10001/10001/10001/10101/10101/10101/01010", "X": "10001/10001/01010/00100/01010/10001/10001",
    "Y": "10001/10001/01010/00100/00100/00100/00100", "Z": "11111/00001/00010/00100/01000/10000/11111",
    "0": "01110/10001/10011/10101/11001/10001/01110", "1": "00100/01100/00100/00100/00100/00100/01110",
    "2": "01110/10001/00001/00010/00100/01000/11111", "3": "11110/00001/00001/01110/00001/00001/11110",
    "4": "00010/00110/01010/10010/11111/00010/00010", "5": "11111/10000/11110/00001/00001/10001/01110",
    "6": "00110/01000/10000/11110/10001/10001/01110", "7": "11111/00001/00010/00100/01000/01000/01000",
    "8": "01110/10001/10001/01110/10001/10001/01110", "9": "01110/10001/10001/01111/00001/00010/01100",
    " ": "00000/00000/00000/00000/00000/00000/00000", "-": "00000/00000/00000/11111/00000/00000/00000",
    ".": "00000/00000/00000/00000/00000/01100/01100", ":": "00000/01100/01100/00000/01100/01100/00000",
    "!": "00100/00100/00100/00100/00100/00000/00100", "/": "00001/00001/00010/00100/01000/10000/10000",
    "%": "11001/11010/00010/00100/01000/01011/10011", "&": "01100/10010/10100/01000/10101/10010/01101",
    "?": "01110/10001/00001/00010/00100/00000/00100", "'": "00100/00100/01000/00000/00000/00000/00000",
    "+": "00000/00100/00100/11111/00100/00100/00000", ",": "00000/00000/00000/00000/01100/00100/01000",
})


# ---- cars (flat silhouettes) --------------------------------------------------------------------------------------------
def car(cv: Canvas, cx: float, ground_y: float, s: float, style: str, body: Color, glass: Color = (28, 36, 48), lights: bool = True,
        facing: int = 1) -> None:
    """A flat side-view car. `style` is roadster (low and long), trail (tall, roof rack) or sedan. `s` is the scale; the car is about 360*s wide."""
    f = facing
    pts_by_style = {
        # (x, y) relative to the car's centre-bottom axis, units of s; y is up
        "roadster": dict(body=[(-190, 28), (-185, 62), (-150, 78), (-60, 86), (-18, 112), (70, 118), (112, 96), (178, 76), (192, 50), (188, 28)],
                         cabin=[(-34, 90), (-4, 114), (66, 116), (98, 92)], wheel_r=34, wheel_x=(-118, 122), rack=False),
        "trail": dict(body=[(-186, 36), (-184, 120), (-150, 140), (-110, 146), (-96, 190), (96, 192), (126, 150), (176, 138), (190, 100), (188, 36)],
                      cabin=[(-88, 150), (-82, 184), (90, 186), (112, 150)], wheel_r=44, wheel_x=(-114, 118), rack=True),
        "sedan": dict(body=[(-196, 30), (-192, 74), (-150, 88), (-84, 94), (-44, 136), (62, 138), (110, 98), (174, 86), (194, 60), (192, 30)],
                      cabin=[(-70, 98), (-40, 132), (58, 134), (96, 100)], wheel_r=37, wheel_x=(-124, 126), rack=False),
    }
    st = pts_by_style[style]

    def tx(pt: tuple[float, float]) -> tuple[float, float]:
        return cx + f * pt[0] * s, ground_y - pt[1] * s

    r = st["wheel_r"] * s
    cv.ellipse(cx, ground_y + 2 * s, 205 * s, 10 * s, (0, 0, 0), 0.35)             # soft shadow
    cv.polygon([tx(p) for p in st["body"]], body)
    cv.polygon([tx(p) for p in st["body"][2:-2]], mix(body, (255, 255, 255), 0.10), 0.5)   # upper highlight
    cv.polygon([tx(p) for p in st["cabin"]], glass)
    if style != "roadster":
        mid = sum(p[0] for p in st["cabin"]) / 4
        cv.rect(cx + f * mid * s - 1.5 * s, ground_y - st["cabin"][1][1] * s, cx + f * mid * s + 1.5 * s, ground_y - st["cabin"][0][1] * s, body)
    if st["rack"]:
        top = max(p[1] for p in st["cabin"])
        cv.rect(cx - 70 * s, ground_y - (top + 9) * s, cx + 80 * s, ground_y - (top + 5) * s, (30, 30, 34))
    for wx in st["wheel_x"]:
        x = cx + f * wx * s
        cv.circle(x, ground_y - r + 2 * s, r, (16, 17, 20))
        cv.circle(x, ground_y - r + 2 * s, r * 0.58, (150, 156, 166))
        cv.circle(x, ground_y - r + 2 * s, r * 0.30, (40, 42, 48))
    if lights:
        hx, hy = tx((st["body"][-1][0] - 6, st["body"][-1][1] + 12))
        cv.glow(hx, hy, 80 * s, (255, 236, 190), 1.0)
        cv.circle(hx, hy, 6 * s, (255, 250, 235))
        tx_, ty_ = tx((st["body"][0][0] + 6, st["body"][0][1] + 20))
        cv.circle(tx_, ty_, 5 * s, (230, 40, 40))


def scene(kind: str, seed: int, body: Color, w: int = 1200, h: int = 675, style: str = "roadster") -> Canvas:
    """A stylised landscape or studio image with a car silhouette. Deterministic for a given (kind, seed)."""
    cv = Canvas(w, h)
    rnd = random.Random(seed)
    s = w / 1200 * rnd.uniform(0.95, 1.1)
    if kind == "sunset":
        cv.vgradient([(0, (36, 24, 74)), (0.45, (206, 82, 96)), (0.75, (255, 170, 98)), (1, (255, 214, 140))], 0, int(h * 0.7))
        cv.glow(w * rnd.uniform(0.3, 0.7), h * 0.52, h * 0.6, (255, 200, 140), 1)
        cv.circle(w * 0.5, h * 0.5, h * 0.09, (255, 236, 190))
        cv.ridge(h * 0.62, h * 0.16, seed, (86, 52, 92), 0.9, 1.1)
        cv.ridge(h * 0.70, h * 0.12, seed + 1, (52, 34, 70), 1.0, 1.6)
        cv.vgradient([(0, (42, 34, 52)), (1, (18, 16, 24))], int(h * 0.7), h)
        cv.polygon([(w * 0.46, h * 0.7), (w * 0.54, h * 0.7), (w * 0.98, h), (w * 0.02, h)], (58, 54, 66))
        cv.polygon([(w * 0.497, h * 0.7), (w * 0.503, h * 0.7), (w * 0.53, h), (w * 0.47, h)], (230, 190, 120), 0.5)
        car(cv, w * 0.5, h * 0.9, s * 1.05, style, body)
    elif kind == "night":
        cv.vgradient([(0, (6, 10, 28)), (0.7, (24, 34, 70)), (1, (60, 70, 110))], 0, int(h * 0.72))
        cv.stars(seed, 90, int(h * 0.45))
        for i in range(14):       # city blocks
            bw = rnd.randrange(60, 120)
            bx = i * w / 13 - bw / 2
            bh = rnd.randrange(int(h * 0.12), int(h * 0.38))
            cv.rect(bx, h * 0.72 - bh, bx + bw, h * 0.72, (16 + i % 3 * 4, 20, 36))
            for wy in range(int(h * 0.72 - bh) + 10, int(h * 0.72) - 8, 22):
                for wx in range(int(bx) + 8, int(bx + bw) - 10, 18):
                    if rnd.random() < 0.45:
                        cv.rect(wx, wy, wx + 7, wy + 10, (255, 214, 130), 0.85)
        cv.vgradient([(0, (26, 28, 42)), (1, (8, 9, 14))], int(h * 0.72), h)
        cv.rect(0, h * 0.72, w, h * 0.725, (110, 130, 190), 0.5)
        car(cv, w * 0.5, h * 0.88, s * 1.1, style, body)
    elif kind == "mountain":
        cv.vgradient([(0, (92, 140, 196)), (1, (206, 226, 240))], 0, int(h * 0.65))
        for i in range(3):
            cv.ellipse(rnd.randrange(w), rnd.randrange(40, int(h * 0.3)), rnd.randrange(80, 200), rnd.randrange(14, 30), (255, 255, 255), 0.55)
        cv.ridge(h * 0.55, h * 0.34, seed, (130, 150, 176), 1.0, 0.9)
        cv.ridge(h * 0.66, h * 0.22, seed + 3, (66, 96, 90), 1.0, 1.4)
        cv.vgradient([(0, (70, 100, 70)), (1, (36, 58, 44))], int(h * 0.66), h)
        for i in range(26):       # pines
            tx_, base = rnd.randrange(w), rnd.randrange(int(h * 0.68), int(h * 0.82))
            ts = rnd.uniform(0.6, 1.4)
            cv.polygon([(tx_, base - 70 * ts), (tx_ - 18 * ts, base), (tx_ + 18 * ts, base)], (22, 52, 40))
        cv.polygon([(w * 0.2, h), (w * 0.62, h * 0.74), (w * 0.7, h * 0.74), (w * 0.9, h)], (120, 100, 78))
        car(cv, w * 0.55, h * 0.88, s * 1.0, style, body)
    elif kind == "coast":
        cv.vgradient([(0, (120, 190, 236)), (1, (232, 244, 250))], 0, int(h * 0.55))
        cv.circle(w * 0.78, h * 0.2, h * 0.07, (255, 247, 214))
        cv.vgradient([(0, (36, 130, 170)), (1, (14, 80, 120))], int(h * 0.55), int(h * 0.75))
        for i in range(30):
            yy = rnd.randrange(int(h * 0.56), int(h * 0.74))
            xx = rnd.randrange(w)
            cv.rect(xx, yy, xx + rnd.randrange(30, 120), yy + 2, (210, 240, 250), 0.5)
        cv.ridge(h * 0.78, h * 0.08, seed, (206, 186, 140), 1.0, 0.8)
        cv.vgradient([(0, (84, 86, 96)), (1, (44, 46, 54))], int(h * 0.77), h)
        cv.rect(0, h * 0.77, w, h * 0.785, (240, 236, 224))
        for i in range(0, w, 120):
            cv.rect(i, h * 0.9, i + 60, h * 0.905, (240, 220, 140), 0.8)
        car(cv, w * 0.5, h * 0.9, s * 1.05, style, body, facing=-1)
    elif kind == "studio":
        cv.vgradient([(0, (30, 34, 42)), (0.7, (52, 58, 70)), (1, (26, 28, 34))], 0, h)
        cv.glow(w * 0.5, h * 0.45, h * 0.8, (190, 210, 240), 0.9)
        cv.ellipse(w * 0.5, h * 0.86, w * 0.42, h * 0.06, (0, 0, 0), 0.4)
        cv.rect(0, h * 0.82, w, h, (36, 38, 46), 0.55)
        car(cv, w * 0.5, h * 0.84, s * 1.1, style, body, lights=True)
    elif kind == "interior":
        cv.vgradient([(0, (28, 22, 38)), (1, (70, 44, 54))], 0, h)
        cv.glow(w * 0.5, h * 0.4, h * 0.7, (255, 170, 110), 0.8)
        cv.ellipse(w * 0.5, h * 0.62, w * 0.34, h * 0.22, (18, 18, 22))                 # dashboard
        cv.circle(w * 0.36, h * 0.6, h * 0.17, (12, 12, 14))                            # steering wheel
        cv.circle(w * 0.36, h * 0.6, h * 0.12, (60, 56, 62), 1.0)
        cv.circle(w * 0.36, h * 0.6, h * 0.075, (12, 12, 14))
        cv.rect(w * 0.55, h * 0.5, w * 0.78, h * 0.62, (14, 18, 26))
        cv.rect(w * 0.56, h * 0.51, w * 0.77, h * 0.61, (60, 150, 190), 0.8)
        cv.polygon([(0, h * 0.7), (w, h * 0.7), (w, h), (0, h)], (20, 16, 22), 0.9)
    cv.vignette(0.3)
    return cv


def banner(title: str, subtitle: str, a: Color, b: Color, seed: int, w: int = 1200, h: int = 400, style: str = "roadster", body: Color = (230, 232, 238)) -> Canvas:
    cv = Canvas(w, h)
    cv.vgradient([(0, a), (1, b)])
    rnd = random.Random(seed)
    for i in range(7):                                              # soft bokeh discs
        cv.circle(rnd.randrange(w), rnd.randrange(h), rnd.randrange(30, 120), mix(a, (255, 255, 255), 0.5), 0.12)
    cv.polygon([(w * 0.55, 0), (w, 0), (w, h), (w * 0.38, h)], mix(a, b, 0.5), 0.55)
    car(cv, w * 0.76, h * 0.9, w / 1200 * 0.9, style, body)
    size = 7 if len(title) <= 14 else 5
    cv.text(60, int(h * 0.28), title, (255, 255, 255), size)
    cv.text(62, int(h * 0.28) + 7 * size + 26, subtitle, mix((255, 255, 255), a, 0.15), 3)
    cv.rect(60, int(h * 0.28) - 22, 160, int(h * 0.28) - 16, (255, 214, 120))
    return cv


def diagram(kind: str, seed: int, w: int = 1200, h: int = 675) -> Canvas:
    """Line-art style technical graphics for the Lumen lighting range."""
    cv = Canvas(w, h, (14, 22, 36))
    cv.vgradient([(0, (12, 20, 34)), (1, (22, 34, 54))])
    for x in range(0, w, 60):
        cv.rect(x, 0, x + 1, h, (60, 84, 120), 0.25)
    for y in range(0, h, 60):
        cv.rect(0, y, w, y + 1, (60, 84, 120), 0.25)
    if kind == "headlamp":
        cx, cy = w * 0.36, h * 0.5
        cv.ellipse(cx, cy, 250, 130, (24, 38, 60))
        cv.ellipse(cx, cy, 250, 130, (90, 150, 220), 0.0)
        for i in range(3):
            cv.circle(cx - 90 + i * 90, cy, 46, (230, 240, 255), 0.95)
            cv.circle(cx - 90 + i * 90, cy, 30, (255, 252, 235))
        for k in range(9):                                        # beam fan
            ang = math.radians(-26 + k * 6.5)
            cv.polygon([(cx + 270, cy), (cx + 270 + math.cos(ang) * 520, cy + math.sin(ang) * 520), (cx + 270 + math.cos(ang + 0.08) * 520, cy + math.sin(ang + 0.08) * 520)],
                       (255, 236, 170), 0.10)
        cv.text(60, 56, "LUMEN HEADLAMP", (200, 224, 255), 6)
        cv.text(60, 112, "3-MODULE ARRAY", (120, 160, 210), 3)
    else:
        for k in range(6):
            y = h * 0.28 + k * 60
            cv.polygon([(130, y), (w * 0.62, y - 20 + k * 6), (w * 0.62, y + 40 - k * 4), (130, y + 18)], (255, 232, 160), 0.07 + 0.02 * k)
        car(cv, w * 0.2, h * 0.82, 0.62, "sedan", (200, 206, 216))
        cv.text(60, 56, "LUMEN ADAPTIVE BEAM", (200, 224, 255), 6)
        cv.text(60, 112, "SHAPES LIGHT AROUND TRAFFIC", (120, 160, 210), 3)
    return cv


# ---- PDF manual -------------------------------------------------------------------------------------------------------
def _pdf_escape(s: str) -> str:
    return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")


def pdf(title: str, chapters: list[tuple[str, list[str]]], accent: Color = (0.1, 0.35, 0.6)) -> bytes:
    """A multi-page PDF with a cover and one page per chapter, set in Helvetica, with a bookmark (outline) for each chapter."""
    objs: list[bytes] = []

    def add(b: bytes) -> int:
        objs.append(b)
        return len(objs)

    cat_id = add(b"")          # 1 catalog, filled at the end
    pages_id = add(b"")        # 2 pages
    font_id = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    bold_id = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")

    def page_stream(ops: str) -> int:
        data = ops.encode("latin-1", "replace")
        return add(b"<< /Length %d >>\nstream\n" % len(data) + data + b"\nendstream")

    page_ids: list[int] = []

    def new_page(ops: str) -> int:
        sid = page_stream(ops)
        pid = add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 %d 0 R /F2 %d 0 R >> >> /Contents %d 0 R >>" % (pages_id, font_id, bold_id, sid))
        page_ids.append(pid)
        return pid

    r, g, b = accent
    cover = (f"{r} {g} {b} rg 0 250 595 592 re f {r * 0.6} {g * 0.6} {b * 0.6} rg 0 250 595 14 re f "
             f"1 1 1 rg BT /F2 40 Tf 50 640 Td ({_pdf_escape(title)}) Tj ET BT /F1 18 Tf 50 600 Td (Acme Motors - Owner information) Tj ET "
             f"1 0.85 0.4 rg 50 560 90 6 re f 0.2 g BT /F1 11 Tf 50 200 Td (Sample document. Acme Motors is a fictional brand.) Tj ET")
    new_page(cover)
    outline_targets: list[tuple[str, int]] = [("Cover", page_ids[0])]
    for name, paras in chapters:
        y = 760
        ops = [f"{r} {g} {b} rg 0 790 595 52 re f 1 1 1 rg BT /F2 20 Tf 50 806 Td ({_pdf_escape(name)}) Tj ET 0.15 g"]
        for para in paras:
            words, line = para.split(), ""
            ops.append(f"BT /F1 11 Tf 50 {y} Td")
            first = True
            for wd in words:
                if len(line) + len(wd) + 1 > 88:
                    ops.append(f"({_pdf_escape(line)}) Tj 0 -15 Td" if not first else f"({_pdf_escape(line)}) Tj 0 -15 Td")
                    first = False
                    y -= 15
                    line = wd
                else:
                    line = f"{line} {wd}".strip()
            ops.append(f"({_pdf_escape(line)}) Tj ET")
            y -= 30
        outline_targets.append((name, new_page(" ".join(ops))))

    outline_root = add(b"")
    item_ids = [add(b"") for _ in outline_targets]
    for i, (name, pid) in enumerate(outline_targets):
        prev = b" /Prev %d 0 R" % item_ids[i - 1] if i else b""
        nxt = b" /Next %d 0 R" % item_ids[i + 1] if i < len(item_ids) - 1 else b""
        objs[item_ids[i] - 1] = b"<< /Title (%s) /Parent %d 0 R%s%s /Dest [%d 0 R /Fit] >>" % (_pdf_escape(name).encode("latin-1", "replace"), outline_root, prev, nxt, pid)
    objs[outline_root - 1] = b"<< /Type /Outlines /First %d 0 R /Last %d 0 R /Count %d >>" % (item_ids[0], item_ids[-1], len(item_ids))
    objs[pages_id - 1] = b"<< /Type /Pages /Kids [%s] /Count %d >>" % (b" ".join(b"%d 0 R" % p for p in page_ids), len(page_ids))
    objs[cat_id - 1] = b"<< /Type /Catalog /Pages %d 0 R /Outlines %d 0 R /PageMode /UseOutlines >>" % (pages_id, outline_root)
    info_id = add(b"<< /Title (%s) /Producer (Orbit demo kit) >>" % _pdf_escape(title).encode("latin-1", "replace"))

    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for i, o in enumerate(objs, 1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + o + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    for off in offsets:
        out += b"%010d 00000 n \n" % off
    out += b"trailer\n<< /Size %d /Root %d 0 R /Info %d 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, cat_id, info_id, xref)
    return bytes(out)


# ---- audio and video ----------------------------------------------------------------------------------------------------
def tone_wav(seconds: float = 8.0, rate: int = 44100) -> bytes:
    """A soft arpeggio (A minor 7) with a slow fade in and out."""
    notes = [220.0, 261.63, 329.63, 392.0, 329.63, 261.63]
    n = int(seconds * rate)
    frames = bytearray()
    for i in range(n):
        t = i / rate
        k = int(t * 3) % len(notes)
        local = (t * 3) % 1
        env = math.exp(-3.0 * local) * min(1.0, t / 0.4) * min(1.0, (seconds - t) / 1.2)
        f = notes[k]
        v = (math.sin(2 * math.pi * f * t) + 0.35 * math.sin(2 * math.pi * 2 * f * t) + 0.15 * math.sin(2 * math.pi * 3 * f * t)) * env * 0.38
        frames += struct.pack("<h", int(max(-1.0, min(1.0, v)) * 32767))
    with tempfile.TemporaryFile() as tf:
        with wave.open(tf, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(rate)
            w.writeframes(bytes(frames))
        tf.seek(0)
        return tf.read()


def encode_mp3(wav: bytes) -> bytes | None:
    """MP3 through ffmpeg when it is installed; otherwise None (the caller falls back to the WAV)."""
    ff = shutil.which("ffmpeg")
    if not ff:
        return None
    r = subprocess.run([ff, "-v", "error", "-f", "wav", "-i", "pipe:0", "-c:a", "libmp3lame", "-b:a", "96k", "-map_metadata", "-1", "-f", "mp3", "pipe:1"],
                       input=wav, capture_output=True, timeout=60)
    return r.stdout if r.returncode == 0 and r.stdout else None


def teaser_mp4(png: bytes, seconds: int = 5) -> bytes | None:
    """A slow push-in on a still image with a soft tone: a short video without any external footage. None when ffmpeg is missing."""
    ff = shutil.which("ffmpeg")
    if not ff:
        return None
    with tempfile.TemporaryDirectory() as d:
        src, out = Path(d, "in.png"), Path(d, "out.mp4")
        src.write_bytes(png)
        cmd = [ff, "-v", "error", "-y", "-loop", "1", "-framerate", "24", "-i", str(src), "-f", "lavfi", "-i", f"sine=frequency=196:duration={seconds}",
               "-vf", f"scale=1920:-2,zoompan=z='1+0.0016*on':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={seconds * 24}:s=960x540:fps=24,format=yuv420p",
               "-t", str(seconds), "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-c:a", "aac", "-b:a", "64k", "-af", "volume=0.2,afade=t=out:st=%d:d=1" % (seconds - 1),
               "-shortest", "-movflags", "+faststart", "-map_metadata", "-1", str(out)]
        r = subprocess.run(cmd, capture_output=True, timeout=120)
        return out.read_bytes() if r.returncode == 0 and out.exists() else None
