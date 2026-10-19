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
    if (focusFirst) focusables()[0]?.focus({ preventScroll: true });
  }, [focusFirst]);

  // A submenu that would run off the right edge opens to the left instead.
  useLayoutEffect(() => {
    if (!onExitLeft || !list.current) return;
    setFlip(list.current.getBoundingClientRect().right > window.innerWidth - 8);
  }, [onExitLeft]);

  const focusParentOf = (key: string) =>
    list.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(key)}"]`)?.focus({ preventScroll: true });

  const onKeyDown = (e: KeyboardEvent) => {
    if ((e.target as HTMLElement).closest('[role="menu"]') !== list.current) return; // a submenu handles its own keys
    const els = focusables();
    const at = els.indexOf(document.activeElement as HTMLElement);
    const move = (to: number) => els[(to + els.length) % els.length]?.focus({ preventScroll: true });
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

const GUTTER = 8;

interface FloatingMenuProps {
  /** Where to open: at a viewport point (right-click) or anchored below an element ("..." trigger). */
  at: { x: number; y: number } | { anchor: HTMLElement | null };
  label: string;
  items: MenuItem[];
  onClose: () => void;
  /** Receives focus when the menu closes via keyboard/selection; defaults to whatever was focused on open. */
  returnFocusTo?: HTMLElement | null;
  id?: string;
  className: string;
}

/** The floating layer shared by ContextMenu and CardMenu: portalled to <body> (so a card's overflow/transform can't
 * clip or trap it), kept inside the viewport, closed on an outside pointer-down, scroll, resize or window blur. */
function FloatingMenu({ at, label, items, onClose, returnFocusTo, id, className }: FloatingMenuProps) {
  const box = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);
  const [opener] = useState(() => (typeof document !== 'undefined' ? document.activeElement : null));
  const anchor = 'anchor' in at ? at.anchor : null;
  const x = 'x' in at ? at.x : undefined;
  const y = 'y' in at ? at.y : undefined;

  useLayoutEffect(() => {
    const r = box.current?.getBoundingClientRect();
    if (!r) return;
    const maxLeft = window.innerWidth - r.width - GUTTER;
    const maxTop = window.innerHeight - r.height - GUTTER;
    let left: number;
    let top: number;
    if (anchor) {
      const a = anchor.getBoundingClientRect();
      const rtl = getComputedStyle(anchor).direction === 'rtl';
      left = rtl ? a.left : a.right - r.width; // aligned to the trigger's end edge
      top = a.bottom + 4;
      if (top > maxTop && a.top - r.height - 4 >= GUTTER) top = a.top - r.height - 4; // no room below: open above
    } else {
      left = x ?? 0;
      top = y ?? 0;
    }
    setPos({ left: Math.max(GUTTER, Math.min(left, maxLeft)), top: Math.max(GUTTER, Math.min(top, maxTop)) });
  }, [anchor, x, y]);

  useEffect(() => {
    const away = (e: Event) => {
      const t = e.target as Node;
      // A press on the anchor itself is the trigger's own toggle; let its click handler decide.
      if (!box.current?.contains(t) && !anchor?.contains(t)) onClose();
    };
    // A scroll dismisses the menu only when it actually moved what the menu hangs off. Browsers also emit scroll events
    // for reasons that move nothing (focusing an item in a just-mounted fixed menu, scroll anchoring, a card's own
    // overflow), and treating every one as "the user scrolled away" made the menu close the instant it opened.
    const start = { x: window.scrollX, y: window.scrollY, rect: anchor?.getBoundingClientRect() };
    const moved = () => {
      if (anchor && start.rect) {
        const r = anchor.getBoundingClientRect();
        return Math.abs(r.top - start.rect.top) > 2 || Math.abs(r.left - start.rect.left) > 2;
      }
      return Math.abs(window.scrollX - start.x) > 2 || Math.abs(window.scrollY - start.y) > 2;
    };
    const onScroll = (e: Event) => {
      if (e.target instanceof Node && box.current?.contains(e.target)) return; // scrolling inside the menu itself
      if (moved()) onClose();
    };
    const dismiss = () => onClose();
    document.addEventListener('pointerdown', away, true);
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', dismiss);
    window.addEventListener('blur', dismiss);
    return () => {
      document.removeEventListener('pointerdown', away, true);
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('resize', dismiss);
      window.removeEventListener('blur', dismiss);
    };
  }, [onClose, anchor]);

  const close = (returnFocus: boolean) => {
    onClose();
    const target = returnFocusTo ?? opener;
    if (returnFocus && target instanceof HTMLElement) target.focus({ preventScroll: true });
  };

  return createPortal(
    <div
      ref={box}
      id={id}
      className={className}
      // Painted off-screen until measured so the first frame never flashes at the wrong spot.
      style={pos ? { left: pos.left, top: pos.top } : { left: 0, top: 0, visibility: 'hidden' }}
    >
      <MenuList items={items} label={label} onClose={close} focusFirst />
    </div>,
    // Inside <main> when there is one: still clear of any card's overflow/transform (fixed positioning, and nothing
    // around <main> clips), but within a landmark so assistive tech and axe's `region` rule see it as page content.
    (typeof document !== 'undefined' && document.querySelector('main')) || document.body,
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
  return <FloatingMenu at={{ x, y }} label={label} items={items} onClose={onClose} className="dam-contextmenu" />;
}

export interface PopoverMenuProps {
  /** The button that opened the menu; the menu hangs below it, aligned to its end edge. */
  anchor: HTMLElement | null;
  label: string;
  items: MenuItem[];
  onClose: () => void;
  /** Set it to the trigger's `aria-controls` value. */
  id?: string;
}

/** A menu anchored to a trigger button (the ARIA menu-button pattern's popup), portalled so no ancestor can clip it. */
export function PopoverMenu({ anchor, label, items, onClose, id }: PopoverMenuProps) {
  return (
    <FloatingMenu
      at={{ anchor }}
      label={label}
      items={items}
      onClose={onClose}
      returnFocusTo={anchor}
      id={id}
      className="dam-popover"
    />
  );
}
