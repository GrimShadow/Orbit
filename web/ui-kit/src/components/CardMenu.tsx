import { DotsThree } from '@phosphor-icons/react';
import { useEffect, useId, useRef, useState } from 'react';
import { MenuList, type MenuItem } from './Menu';

/** Kept as an alias: a card's action is just a menu item. */
export type CardAction = MenuItem;

interface CardMenuProps {
  /** Accessible name of the trigger, e.g. "Actions for Hero shot". */
  label: string;
  actions: MenuItem[];
  /** Called when the menu opens - a place to start fetching whatever the items' state depends on. */
  onOpen?: () => void;
}

/** A "..." trigger that opens the same menu a right-click does (ARIA menu-button pattern): the trigger toggles it,
 * Escape (or a click elsewhere) closes it and returns focus to the trigger. */
export function CardMenu({ label, actions, onOpen }: CardMenuProps) {
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

  if (actions.length === 0) return null;

  return (
    <div className="dam-cardmenu" ref={root}>
      <button
        ref={trigger}
        type="button"
        className="dam-cardmenu__trigger"
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => {
          if (!open) onOpen?.();
          setOpen((o) => !o);
        }}
      >
        <DotsThree size={18} weight="bold" aria-hidden="true" />
      </button>
      {open && (
        <div id={menuId} className="dam-cardmenu__list">
          <MenuList
            items={actions}
            label={label}
            focusFirst
            onClose={(returnFocus) => {
              setOpen(false);
              if (returnFocus) trigger.current?.focus();
            }}
          />
        </div>
      )}
    </div>
  );
}
