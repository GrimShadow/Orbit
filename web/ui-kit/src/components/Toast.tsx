import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { Button } from './Button';

export interface ToastInput {
  title: string;
  message?: string;
  tone?: 'info' | 'success' | 'error';
  durationMs?: number;
}
interface ToastItem extends ToastInput {
  id: number;
}

const ToastContext = createContext<{ push: (t: ToastInput) => void } | null>(null);

export function useToast() {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error('useToast must be used inside <ToastProvider>');
  return ctx;
}

export function ToastProvider({ children, dismissLabel = 'Dismiss' }: { children: ReactNode; dismissLabel?: string }) {
  const [items, setItems] = useState<ToastItem[]>([]);
  const nextId = useRef(1);
  const dismiss = useCallback((id: number) => setItems((xs) => xs.filter((x) => x.id !== id)), []);
  const push = useCallback(
    (t: ToastInput) => {
      const id = nextId.current++;
      setItems((xs) => [...xs, { ...t, id }]);
      if (t.durationMs !== 0) setTimeout(() => dismiss(id), t.durationMs ?? 6000);
    },
    [dismiss],
  );
  const value = useMemo(() => ({ push }), [push]);

  return (
    <ToastContext.Provider value={value}>
      {children}
      {/* Live region stays mounted so screen readers announce additions. */}
      <div className="dam-toasts" role="region" aria-label="Notifications" aria-live="polite">
        {items.map((t) => (
          <div key={t.id} className={`dam-toast dam-toast--${t.tone ?? 'info'}`} role={t.tone === 'error' ? 'alert' : 'status'}>
            <div>
              <div className="dam-toast__title">{t.title}</div>
              {t.message && <div className="dam-toast__msg">{t.message}</div>}
            </div>
            <Button variant="ghost" size="sm" onClick={() => dismiss(t.id)} aria-label={dismissLabel}>
              ✕
            </Button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}
