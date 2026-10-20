import { useEffect, useState, type RefObject } from 'react';

/** True once the element is near the viewport (200px margin), and stays true. Without `IntersectionObserver` (old browsers,
 * jsdom) it turns true on the first effect, i.e. right after the first paint - so whatever waits on it is always loaded
 * after the content around it, never before. */
export function useNearViewport(ref: RefObject<Element | null>): boolean {
  const [near, setNear] = useState(false);
  useEffect(() => {
    if (near) return;
    const el = ref.current;
    if (!el || typeof IntersectionObserver === 'undefined') {
      setNear(true);
      return;
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setNear(true);
          observer.disconnect();
        }
      },
      { rootMargin: '200px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [near, ref]);
  return near;
}
