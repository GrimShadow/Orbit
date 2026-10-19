import { useLayoutEffect, useState, type RefObject } from 'react';

/** Measures the element matching `[data-indicator-key="<active>"]` inside `container` and returns a transform that
 * moves a 1px-wide indicator over it - so the sliding underline (Tabs) and the selected pill (SegmentedControl) animate
 * with `transform` only. `ready` is false until a non-zero width has been measured (first paint, jsdom). */
export function useIndicator(container: RefObject<HTMLElement | null>, active: string | undefined) {
  const [box, setBox] = useState<{ x: number; w: number } | null>(null);

  useLayoutEffect(() => {
    const root = container.current;
    if (!root || active === undefined) return;
    const measure = () => {
      const el = root.querySelector<HTMLElement>(`[data-indicator-key="${CSS.escape(active)}"]`);
      if (!el) return setBox(null);
      setBox({ x: el.offsetLeft, w: el.offsetWidth });
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(measure);
    ro.observe(root);
    return () => ro.disconnect();
  }, [container, active]);

  const ready = !!box && box.w > 0;
  return {
    ready,
    style: ready ? { transform: `translateX(${box.x}px) scaleX(${box.w})` } : { transform: 'scaleX(0)' },
  };
}
