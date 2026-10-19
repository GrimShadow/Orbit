import type { NewAnchor } from '../commentsApi';

/** Pure maths for drawing on a rendered image or page. Everything is in fractions (0..1) of the image's OWN rendered box,
 * never of the scrolling container around it, so a stored anchor means the same thing at any zoom, window size or
 * rendition (ADR-0032). Coordinates are physical (left/top), also in right-to-left layouts: an image does not mirror. */

/** A drag has to cover at least this fraction of BOTH dimensions to count as a region; anything smaller is a click (a pin). */
export const MIN_DRAG = 0.015;

export interface Box {
  left: number;
  top: number;
  width: number;
  height: number;
}
export interface Pt {
  x: number;
  y: number;
}
export interface Frac {
  x: number;
  y: number;
  w: number;
  h: number;
}

export const clamp01 = (n: number) => (Number.isFinite(n) ? Math.min(1, Math.max(0, n)) : 0);
/** Six decimals is 1 px in a million: tidy numbers on the wire, far below anything a pointer can express. */
export const round6 = (n: number) => Math.round(n * 1e6) / 1e6;

/** Where an `object-fit: contain` image really sits inside its element: the element box shrunk to the picture's aspect
 * ratio and centred. With the stage's CSS the element already matches the picture, so this is the identity - it is here
 * so a letterboxed image (or one whose box is forced) still yields coordinates of the picture, not of the empty bars. */
export function containedBox(el: { width: number; height: number }, natural: { width: number; height: number } | null): Box {
  const whole = { left: 0, top: 0, width: el.width, height: el.height };
  if (!natural || natural.width <= 0 || natural.height <= 0 || el.width <= 0 || el.height <= 0) return whole;
  const scale = Math.min(el.width / natural.width, el.height / natural.height);
  const width = natural.width * scale;
  const height = natural.height * scale;
  return { left: (el.width - width) / 2, top: (el.height - height) / 2, width, height };
}

/** A viewport point as a clamped fraction of `box` (a viewport-space rectangle, e.g. `getBoundingClientRect()` of the
 * image, which already includes scrolling and any RTL placement). A zero-sized box yields 0,0 rather than NaN. */
export function normalizePoint(clientX: number, clientY: number, box: Box): Pt {
  if (box.width <= 0 || box.height <= 0) return { x: 0, y: 0 };
  return { x: clamp01((clientX - box.left) / box.width), y: clamp01((clientY - box.top) / box.height) };
}

/** The rectangle spanned by two points, whichever way it was dragged; always inside 0..1 (`x + w <= 1`, `y + h <= 1`). */
export function rectBetween(a: Pt, b: Pt): Frac {
  const x = round6(clamp01(Math.min(a.x, b.x)));
  const y = round6(clamp01(Math.min(a.y, b.y)));
  const w = Math.min(round6(Math.abs(clamp01(a.x) - clamp01(b.x))), round6(1 - x));
  const h = Math.min(round6(Math.abs(clamp01(a.y) - clamp01(b.y))), round6(1 - y));
  return { x, y, w, h };
}

export const isRegionSized = (r: Frac) => r.w >= MIN_DRAG && r.h >= MIN_DRAG;

/** What a finished gesture means: a real drag is a region, a click (or a sliver) is a pin where it started. `page` is
 * recorded for paged documents only. */
export function anchorFromGesture(start: Pt, end: Pt, page?: number): NewAnchor {
  const r = rectBetween(start, end);
  const withPage = page === undefined ? {} : { page };
  if (isRegionSized(r)) return { type: 'region', ...r, ...withPage };
  return { type: 'point', x: round6(clamp01(start.x)), y: round6(clamp01(start.y)), ...withPage };
}

/** CSS percentages for a fraction-positioned element. */
export const pct = (n: number) => `${round6(n * 100)}%`;
