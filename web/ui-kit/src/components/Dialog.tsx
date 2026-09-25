import { useEffect, useId, useRef, type ReactNode } from 'react';
import { Button } from './Button';

interface DialogProps {
  open: boolean;
  onClose: () => void;
  title: string;
  children: ReactNode;
  closeLabel?: string;
  variant?: 'modal' | 'drawer';
}

/** Built on the native <dialog>: focus trap, Esc, inert background and top-layer come from the browser. */
export function Dialog({ open, onClose, title, children, closeLabel = 'Close', variant = 'modal' }: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const d = ref.current;
    if (!d) return;
    if (open && !d.open) d.showModal();
    if (!open && d.open) d.close();
  }, [open]);

  return (
    // Backdrop click is a pointer convenience only; keyboard users close with Esc (native) or the Close button.
    // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions, jsx-a11y/click-events-have-key-events
    <dialog
      ref={ref}
      className={`dam-dialog${variant === 'drawer' ? ' dam-dialog--drawer' : ''}`}
      aria-labelledby={titleId}
      onClose={onClose}
      onClick={(e) => {
        if (e.target === ref.current) onClose();
      }}
    >
      <div className="dam-dialog__head">
        <h2 id={titleId} className="dam-dialog__title">
          {title}
        </h2>
        <Button variant="ghost" size="sm" onClick={onClose} aria-label={closeLabel}>
          ✕
        </Button>
      </div>
      <div className="dam-dialog__body">{open ? children : null}</div>
    </dialog>
  );
}

export const Modal = (p: Omit<DialogProps, 'variant'>) => <Dialog {...p} variant="modal" />;
export const Drawer = (p: Omit<DialogProps, 'variant'>) => <Dialog {...p} variant="drawer" />;
