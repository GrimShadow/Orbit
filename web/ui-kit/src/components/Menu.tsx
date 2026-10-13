import { CaretRight, Check } from '@phosphor-icons/react';
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

export interface MenuItem {
  key: string;
  label: string;
  icon?: ReactNode;
  /** `danger` is styled as destructive; everything else is neutral. */
  tone?: 'danger';
  /** Set (true or false) to make this a checkable item - e.g. "available for Canva". */
  checked?: boolean;
  disabled?: boolean;
  /** Draws a divider above this item to group related actions. */
  separatorBefore?: boolean;
  onSelect?: () => void;
  /** Makes this item open a submenu. An empty list shows `emptyLabel` (disabled) instead. */
  children?: MenuItem[];
  emptyLabel?: string;
}

interface MenuListProps {
  items: MenuItem[];
  label: string;
  /** Closes the whole menu; `returnFocus` is false when the user tabbed away. */
  onClose: (returnFocus: boolean) => void;
  focusFirst?: boolean;
  /** Present on a submenu: Left/Escape close just this level and hand focus back to its parent item. */
  onExitLeft?: () => void;
}

/** The ARIA `menu` pattern: Arrow keys move, Home/End jump, Right opens a submenu, Left/Escape close one level,
 * Enter/Space activate. Used by the "..." button menu and the right-click context menu alike. */
export function MenuList({ items, label, onClose, focusFirst, onExitLeft }: MenuListProps) {
  const list = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState<{ key: string; focus: boolean } | null>(null);
  const [flip, setFlip] = useState(false);

  const focusables = () =>
    Array.from(list.current?.children ?? [])
      .filter((el) => el.classList.contains('dam-menu__entry'))
      .map((el) => el.firstElementChild as HTMLElement)
      .filter((b) => b && !b.hasAttribute('disabled'));

  useEffect(() => {
    if (focusFirst) focusables()[0]?.focus();
  }, [focusFirst]);

  // A submenu that would run off the right edge opens to the left instead.
  useLayoutEffect(() => {
    if (!onExitLeft || !list.current) return;
    setFlip(list.current.getBoundingClientRect().right > window.innerWidth - 8);
  }, [onExitLeft]);

  const focusParentOf = (key: string) => list.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(key)}"]`)?.focus();

  const onKeyDown = (e: KeyboardEvent) => {
    if ((e.target as HTMLElement).closest('[role="menu"]') !== list.current) return; // a submenu handles its own keys
    const els = focusables();
    const at = els.indexOf(document.activeElement as HTMLElement);
    const move = (to: number) => els[(to + els.length) % els.length]?.focus();
    switch (e.key) {
      case 'ArrowDown':
        move(at + 1);
        break;
      case 'ArrowUp':
        move(at < 0 ? els.length - 1 : at - 1);
        break;
      case 'Home':
        move(0);
        break;
      case 'End':
        move(els.length - 1);
        break;
      case 'ArrowRight': {
        const key = (document.activeElement as HTMLElement | null)?.dataset.key;
        if (key && (document.activeElement as HTMLElement).getAttribute('aria-haspopup')) setOpen({ key, focus: true });
        else return;
        break;
      }
      case 'ArrowLeft':
        if (!onExitLeft) return;
        onExitLeft();
        break;
      case 'Escape':
        if (onExitLeft) onExitLeft();
        else onClose(true);
        break;
      case 'Tab':
        onClose(false);
        return;
      default:
        return;
    }
    e.preventDefault();
    e.stopPropagation();
  };

  return (
    <div
      ref={list}
      role="menu"
      tabIndex={-1}
      aria-label={label}
      className={`dam-menu${flip ? ' dam-menu--flip' : ''}${onExitLeft ? ' dam-menu--sub' : ''}`}
      onKeyDown={onKeyDown}
    >
      {items.map((item) => {
        const isSub = item.children !== undefined;
        const empty = isSub && item.children!.length === 0;
        const expanded = open?.key === item.key;
        return (
          <div
            key={item.key}
            className="dam-menu__entry"
            onMouseEnter={() => setOpen(isSub && !empty ? { key: item.key, focus: false } : null)}
          >
            {item.separatorBefore && <div role="separator" className="dam-menu__sep" />}
            <button
              type="button"
              role={item.checked !== undefined ? 'menuitemcheckbox' : 'menuitem'}
              aria-checked={item.checked}
              aria-haspopup={isSub && !empty ? 'menu' : undefined}
              aria-expanded={isSub && !empty ? expanded : undefined}
              data-key={item.key}
              tabIndex={-1}
              disabled={item.disabled || empty}
              className={`dam-menu__item${item.tone === 'danger' ? ' dam-menu__item--danger' : ''}`}
              onClick={(e) => {
                if (isSub) {
                  // Hovering already opened it, so a click must not close it again; moving away or Escape does that.
                  setOpen({ key: item.key, focus: e.detail === 0 });
                  return;
                }
                onClose(true);
                item.onSelect?.();
              }}
            >
              <span className="dam-menu__lead" aria-hidden="true">
                {item.checked ? <Check size={14} weight="bold" /> : item.icon}
              </span>
              <span className="dam-menu__label">{empty ? (item.emptyLabel ?? item.label) : item.label}</span>
              {isSub && !empty && <CaretRight size={12} weight="bold" aria-hidden="true" className="dam-menu__caret" />}
            </button>
            {isSub && !empty && expanded && (
              <MenuList
                items={item.children!}
                label={item.label}
                onClose={onClose}
                focusFirst={open?.focus}
                onExitLeft={() => {
                  setOpen(null);
                  focusParentOf(item.key);
                }}
              />
            )}
          </div>
        );
      })}
    </div>
  );
}

interface ContextMenuProps {
  /** Pointer position (viewport coordinates) the menu opens at. */
  x: number;
  y: number;
  label: string;
  items: MenuItem[];
  onClose: () => void;
}

/** A right-click menu: opens at the pointer, stays inside the viewport, and closes on Escape, an outside click, a scroll
 * or a resize - returning focus to wherever it was when it opened. */
export function ContextMenu({ x, y, label, items, onClose }: ContextMenuProps) {
  const box = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ left: x, top: y });
  const [opener] = useState(() => (typeof document !== 'undefined' ? document.activeElement : null));

  useLayoutEffect(() => {
    const r = box.current?.getBoundingClientRect();
    if (!r) return;
    setPos({
      left: Math.max(8, Math.min(x, window.innerWidth - r.width - 8)),
      top: Math.max(8, Math.min(y, window.innerHeight - r.height - 8)),
    });
  }, [x, y]);

  useEffect(() => {
    const away = (e: Event) => {
      if (!box.current?.contains(e.target as Node)) onClose();
    };
    const dismiss = () => onClose();
    document.addEventListener('pointerdown', away, true);
    window.addEventListener('scroll', dismiss, true);
    window.addEventListener('resize', dismiss);
    window.addEventListener('blur', dismiss);
    return () => {
      document.removeEventListener('pointerdown', away, true);
      window.removeEventListener('scroll', dismiss, true);
      window.removeEventListener('resize', dismiss);
      window.removeEventListener('blur', dismiss);
    };
  }, [onClose]);

  const close = (returnFocus: boolean) => {
    onClose();
    if (returnFocus && opener instanceof HTMLElement) opener.focus();
  };

  return createPortal(
    <div ref={box} className="dam-contextmenu" style={{ left: pos.left, top: pos.top }}>
      <MenuList items={items} label={label} onClose={close} focusFirst />
    </div>,
    document.body,
  );
}
