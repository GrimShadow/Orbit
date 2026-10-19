import { useCallback, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';

/** Warns before unsaved work is lost: the browser's own prompt for reload/close, and for in-app links a confirm the page
 * renders itself (`pending` is the destination while the prompt is open). The console runs on a plain BrowserRouter,
 * which has no navigation blocker, so link clicks are intercepted instead; the browser's Back button is not. */
export function useLeaveGuard(dirty: boolean) {
  const navigate = useNavigate();
  const [pending, setPending] = useState<string | null>(null);

  useEffect(() => {
    if (!dirty) return;
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    const onClick = (e: MouseEvent) => {
      if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
      const a = (e.target as Element | null)?.closest?.('a[href]') as HTMLAnchorElement | null;
      if (!a || (a.target && a.target !== '_self') || a.hasAttribute('download')) return;
      const url = new URL(a.href, window.location.href);
      if (url.origin !== window.location.origin) return;
      if (url.pathname === window.location.pathname && url.search === window.location.search) return;
      e.preventDefault();
      e.stopPropagation();
      setPending(url.pathname + url.search + url.hash);
    };
    window.addEventListener('beforeunload', warn);
    document.addEventListener('click', onClick, true);
    return () => {
      window.removeEventListener('beforeunload', warn);
      document.removeEventListener('click', onClick, true);
    };
  }, [dirty]);

  const stay = useCallback(() => setPending(null), []);
  const leave = useCallback(() => {
    const to = pending;
    setPending(null);
    if (to) navigate(to);
  }, [pending, navigate]);
  return { pending, stay, leave };
}
