import { DotsThree } from '@phosphor-icons/react';
import { useCallback, useId, useState } from 'react';
import { PopoverMenu, type MenuItem } from './Menu';

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
 * Escape (or a click elsewhere) closes it and returns focus to the trigger. The menu itself is portalled to <body>
 * (see `PopoverMenu`) so the card's `overflow: hidden` and hover transform can neither clip nor stack above it. */
export function CardMenu({ label, actions, onOpen }: CardMenuProps) {
  const [open, setOpen] = useState(false);
  // The trigger element is state (callback ref) because the portalled menu needs it during render to anchor itself.
  const [trigger, setTrigger] = useState<HTMLButtonElement | null>(null);
  const menuId = useId();
  const close = useCallback(() => setOpen(false), []);

  if (actions.length === 0) return null;

  return (
    <div className="dam-cardmenu">
      <button
        ref={setTrigger}
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
      {open && <PopoverMenu id={menuId} anchor={trigger} label={label} items={actions} onClose={close} />}
    </div>
  );
}
