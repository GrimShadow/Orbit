import { DotsThree } from '@phosphor-icons/react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';

export interface CardAction {
  key: string;
  label: string;
  icon?: ReactNode;
  /** `danger` is styled as destructive; everything else is neutral. */
  tone?: 'danger';
  onSelect: () => void;
}

interface CardMenuProps {
  /** Accessible name of the trigger, e.g. "Actions for Hero shot". */
  label: string;
  actions: CardAction[];
}

/** A "..." trigger that opens a small action list on a card - the ARIA menu-button pattern: the trigger toggles it,
 * Arrow keys move between items, Home/End jump, Escape (or a click elsewhere) closes and returns focus to the trigger. */
export function CardMenu({ label, actions }: CardMenuProps) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const menuId = useId();

  useEffect(() => {
    if (!open) return;
    const onPointer = (e: PointerEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', onPointer);
    return () => document.removeEventListener('pointerdown', onPointer);
  }, [open]);

  useEffect(() => {
    if (open) root.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
  }, [open]);

  if (actions.length === 0) return null;

  const close = (returnFocus: boolean) => {
    setOpen(false);
    if (returnFocus) trigger.current?.focus();
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    const items = [...(root.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [])];
    const at = items.indexOf(document.activeElement as HTMLElement);
    const move = (to: number) => {
      e.preventDefault();
      items[(to + items.length) % items.length]?.focus();
    };
    if (e.key === 'Escape') {
      e.preventDefault();
      close(true);
    } else if (e.key === 'ArrowDown') move(at + 1);
    else if (e.key === 'ArrowUp') move(at - 1);
    else if (e.key === 'Home') move(0);
    else if (e.key === 'End') move(items.length - 1);
    else if (e.key === 'Tab') close(false);
  };

  return (
    <div className="dam-cardmenu" ref={root} onKeyDown={onKeyDown}>
      <button
        ref={trigger}
        type="button"
        className="dam-cardmenu__trigger"
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => setOpen((o) => !o)}
      >
        <DotsThree size={18} weight="bold" aria-hidden="true" />
      </button>
      {open && (
        <div id={menuId} role="menu" aria-label={label} className="dam-cardmenu__list">
          {actions.map((a) => (
            <button
              key={a.key}
              type="button"
              role="menuitem"
              tabIndex={-1}
              className={`dam-cardmenu__item${a.tone === 'danger' ? ' dam-cardmenu__item--danger' : ''}`}
              onClick={() => {
                close(true);
                a.onSelect();
              }}
            >
              {a.icon}
              {a.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
