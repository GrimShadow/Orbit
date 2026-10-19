import { anchorFromGesture, containedBox, isRegionSized, MIN_DRAG, normalizePoint, pct, rectBetween } from './geometry';

describe('normalizePoint', () => {
  // The picture sits at (100, 50) in the viewport and is 400 x 200.
  const box = { left: 100, top: 50, width: 400, height: 200 };

  it('maps viewport points to fractions of the picture, not of the container', () => {
    expect(normalizePoint(100, 50, box)).toEqual({ x: 0, y: 0 });
    expect(normalizePoint(300, 150, box)).toEqual({ x: 0.5, y: 0.5 });
    expect(normalizePoint(500, 250, box)).toEqual({ x: 1, y: 1 });
  });

  it('clamps points outside the picture to its edges', () => {
    expect(normalizePoint(-40, 9999, box)).toEqual({ x: 0, y: 1 });
    expect(normalizePoint(900, -5, box)).toEqual({ x: 1, y: 0 });
  });

  it('follows the picture as it scrolls: the same pixel is a different fraction once the box moved', () => {
    expect(normalizePoint(300, 150, { ...box, left: 0, top: -50 }).x).toBeCloseTo(0.75);
    expect(normalizePoint(300, 150, { ...box, left: 0, top: -50 }).y).toBe(1);
  });

  it('gives the same fractions at fit and at 100% for the same spot on the picture', () => {
    const fit = { left: 10, top: 10, width: 300, height: 150 };
    const actual = { left: -200, top: -90, width: 1200, height: 600 };
    const at = (b: typeof fit, fx: number, fy: number) => normalizePoint(b.left + b.width * fx, b.top + b.height * fy, b);
    expect(at(fit, 0.25, 0.8)).toEqual(at(actual, 0.25, 0.8));
  });

  it("is not mirrored in a right-to-left layout: x is measured from the picture's physical left edge", () => {
    document.documentElement.dir = 'rtl';
    expect(normalizePoint(150, 50, box).x).toBe(0.125);
    document.documentElement.dir = '';
  });

  it('survives a zero-sized box', () => {
    expect(normalizePoint(10, 10, { left: 0, top: 0, width: 0, height: 0 })).toEqual({ x: 0, y: 0 });
    expect(normalizePoint(Number.NaN, 10, box).x).toBe(0);
  });
});

describe('rectBetween', () => {
  it('is the same whichever way the drag went', () => {
    const a = { x: 0.2, y: 0.3 };
    const b = { x: 0.6, y: 0.9 };
    expect(rectBetween(a, b)).toEqual({ x: 0.2, y: 0.3, w: 0.4, h: 0.6 });
    expect(rectBetween(b, a)).toEqual(rectBetween(a, b));
    expect(rectBetween({ x: 0.6, y: 0.3 }, { x: 0.2, y: 0.9 })).toEqual({ x: 0.2, y: 0.3, w: 0.4, h: 0.6 });
  });

  it('never leaves the unit square, even for out-of-range points or rounding noise', () => {
    const r = rectBetween({ x: -2, y: 0.9999999 }, { x: 3, y: 7 });
    expect(r.x).toBe(0);
    expect(r.w).toBe(1);
    expect(r.x + r.w).toBeLessThanOrEqual(1);
    expect(r.y + r.h).toBeLessThanOrEqual(1);
    const third = rectBetween({ x: 1 / 3, y: 0.1 + 0.2 }, { x: 2 / 3, y: 1 });
    expect(third.x + third.w).toBeLessThanOrEqual(1);
    expect(third.y + third.h).toBeLessThanOrEqual(1);
  });

  it('rounds to six decimals', () => {
    expect(rectBetween({ x: 1 / 3, y: 0 }, { x: 2 / 3, y: 0.5 }).x).toBe(0.333333);
  });
});

describe('anchorFromGesture', () => {
  it('turns a real drag into a region', () => {
    expect(anchorFromGesture({ x: 0.1, y: 0.2 }, { x: 0.4, y: 0.6 })).toEqual({ type: 'region', x: 0.1, y: 0.2, w: 0.3, h: 0.4 });
  });

  it('turns a click, or a sliver, into a pin where the gesture started', () => {
    expect(anchorFromGesture({ x: 0.5, y: 0.5 }, { x: 0.5, y: 0.5 })).toEqual({ type: 'point', x: 0.5, y: 0.5 });
    expect(anchorFromGesture({ x: 0.5, y: 0.5 }, { x: 0.5 + MIN_DRAG / 2, y: 0.9 })).toEqual({ type: 'point', x: 0.5, y: 0.5 });
    expect(anchorFromGesture({ x: 0.5, y: 0.5 }, { x: 0.9, y: 0.5 + MIN_DRAG / 2 })).toEqual({ type: 'point', x: 0.5, y: 0.5 });
  });

  it('needs the minimum in BOTH dimensions, and exactly the minimum counts', () => {
    expect(isRegionSized({ x: 0, y: 0, w: MIN_DRAG, h: MIN_DRAG })).toBe(true);
    expect(isRegionSized({ x: 0, y: 0, w: MIN_DRAG - 0.001, h: 0.5 })).toBe(false);
    expect(anchorFromGesture({ x: 0.1, y: 0.1 }, { x: 0.1 + MIN_DRAG, y: 0.1 + MIN_DRAG }).type).toBe('region');
  });

  it('records the page of a paged document, and only then', () => {
    expect(anchorFromGesture({ x: 0.1, y: 0.1 }, { x: 0.5, y: 0.5 }, 3)).toMatchObject({ type: 'region', page: 3 });
    expect(anchorFromGesture({ x: 0.1, y: 0.1 }, { x: 0.1, y: 0.1 }, 2)).toEqual({ type: 'point', x: 0.1, y: 0.1, page: 2 });
    expect('page' in anchorFromGesture({ x: 0.1, y: 0.1 }, { x: 0.5, y: 0.5 })).toBe(false);
  });
});

describe('containedBox', () => {
  it('is the whole element when its shape already matches the picture (the stage CSS)', () => {
    expect(containedBox({ width: 400, height: 200 }, { width: 2000, height: 1000 })).toEqual({
      left: 0,
      top: 0,
      width: 400,
      height: 200,
    });
  });

  it('excludes letterbox bars when the element is taller or wider than the picture', () => {
    expect(containedBox({ width: 400, height: 400 }, { width: 2000, height: 1000 })).toEqual({
      left: 0,
      top: 100,
      width: 400,
      height: 200,
    });
    expect(containedBox({ width: 600, height: 200 }, { width: 2000, height: 1000 })).toEqual({
      left: 100,
      top: 0,
      width: 400,
      height: 200,
    });
  });

  it('falls back to the element until the picture size is known', () => {
    expect(containedBox({ width: 400, height: 200 }, null)).toEqual({ left: 0, top: 0, width: 400, height: 200 });
    expect(containedBox({ width: 400, height: 200 }, { width: 0, height: 0 })).toEqual({
      left: 0,
      top: 0,
      width: 400,
      height: 200,
    });
  });
});

it('formats percentages without float noise', () => {
  expect(pct(0.3333333333)).toBe('33.333333%');
  expect(pct(0.1 + 0.2)).toBe('30%');
});
