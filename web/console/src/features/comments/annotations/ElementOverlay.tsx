import { forwardRef, useCallback, useLayoutEffect, useState, type HTMLAttributes } from 'react';
import { containedBox, type Box } from './geometry';

const same = (a: Box | null, b: Box) => a && a.left === b.left && a.top === b.top && a.width === b.width && a.height === b.height;

/** Where `el` really is, in the scrolling coordinates of its offset parent (the stage canvas, which is positioned): its
 * offset box, shrunk to the picture itself for an `object-fit: contain` image. */
function measure(el: HTMLElement): Box {
  const inner = containedBox(
    { width: el.offsetWidth, height: el.offsetHeight },
    el instanceof HTMLImageElement ? { width: el.naturalWidth, height: el.naturalHeight } : null,
  );
  return { left: el.offsetLeft + inner.left, top: el.offsetTop + inner.top, width: inner.width, height: inner.height };
}

/** The picture's rectangle in VIEWPORT coordinates right now (scroll and RTL placement included) - what pointer events
 * are normalised against. */
export function viewportBox(el: HTMLElement): Box {
  const r = el.getBoundingClientRect();
  const inner = containedBox(
    { width: r.width, height: r.height },
    el instanceof HTMLImageElement ? { width: el.naturalWidth, height: el.naturalHeight } : null,
  );
  return { left: r.left + inner.left, top: r.top + inner.top, width: inner.width, height: inner.height };
}

/** The box of `el` (an image or a video), kept current across resize, zoom, centring changes and the image finishing
 * loading. Null until there is an element. */
export function useElementBox(el: HTMLElement | null): Box | null {
  const [box, setBox] = useState<Box | null>(null);
  const update = useCallback(() => {
    if (!el) return;
    const next = measure(el);
    setBox((prev) => (same(prev, next) ? prev : next));
  }, [el]);

  useLayoutEffect(() => {
    if (!el) return;
    // A microtask: it still lands before the browser paints, and the first measurement is "external state arriving".
    queueMicrotask(update);
    const ro = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(update);
    ro?.observe(el);
    // The element is centred inside the canvas, so a canvas resize moves it without resizing it.
    if (el.parentElement) ro?.observe(el.parentElement);
    el.addEventListener('load', update);
    el.addEventListener('loadedmetadata', update);
    window.addEventListener('resize', update);
    return () => {
      ro?.disconnect();
      el.removeEventListener('load', update);
      el.removeEventListener('loadedmetadata', update);
      window.removeEventListener('resize', update);
    };
  }, [el, update]);

  return el ? box : null;
}

interface Props extends HTMLAttributes<HTMLDivElement> {
  /** The image or video this overlay sits exactly on top of. */
  target: HTMLElement | null;
}

/** An absolutely positioned layer with exactly the box of `target`, so children can be placed in percentages of the
 * picture. Renders nothing until the target exists. */
export const ElementOverlay = forwardRef<HTMLDivElement, Props>(function ElementOverlay(
  { target, style, children, ...rest },
  ref,
) {
  const box = useElementBox(target);
  if (!target || !box) return null;
  return (
    <div ref={ref} {...rest} style={{ ...style, left: box.left, top: box.top, width: box.width, height: box.height }}>
      {children}
    </div>
  );
});
