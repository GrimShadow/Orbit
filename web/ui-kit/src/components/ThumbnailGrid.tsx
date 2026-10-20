import { Check } from '@phosphor-icons/react';
import type { ReactNode } from 'react';
import { useState } from 'react';
import { Badge, type BadgeTone } from './Badge';
import { CardMenu, type CardAction } from './CardMenu';
import { ImageWithFallback } from './ImageWithFallback';
import { ContextMenu } from './Menu';
import { Skeleton } from './Skeleton';

export interface Thumb {
  id: string;
  title: string;
  subtitle?: string;
  imageUrl?: string;
  /** Drawn in the picture area when there is no `imageUrl`, or the image fails to load - typically a large file-type icon
   * (see `FileTypeIcon`). Decorative. Omit for a plain empty area. */
  placeholder?: ReactNode;
  badge?: string;
  /** Colour of the badge (see `Badge`); neutral when omitted. */
  badgeTone?: BadgeTone;
  /** Shown inline before the subtitle - typically a small file-type glyph (see `FileTypeIcon`). */
  icon?: ReactNode;
  /** A secondary metadata line below the subtitle, e.g. a relative "Updated 6 days ago". */
  meta?: string;
  /** Extra content under the card's text, outside the card's open button - so it may hold links and other controls
   * (e.g. "why it matched" chips). The footer is a `container-type: inline-size` box: use `@container` queries to
   * adapt to the card width. Omit for the plain card. */
  footer?: ReactNode;
}

interface ThumbnailGridProps {
  items: Thumb[];
  label: string;
  onOpen?: (id: string) => void;
  selectedIds?: string[];
  onToggleSelect?: (id: string, selected: boolean) => void;
  selectLabel?: (title: string) => string;
  /** Per-card quick actions, shown behind a "..." menu on the card; omit (or return none) for no menu. */
  cardActions?: (id: string) => CardAction[];
  menuLabel?: (title: string) => string;
  /** Called when a card's menu (button or right-click) opens - e.g. to load what its items depend on. */
  onMenuOpen?: (id: string) => void;
}

export function ThumbnailGrid({
  items,
  label,
  onOpen,
  selectedIds,
  onToggleSelect,
  selectLabel = (t) => `Select ${t}`,
  cardActions,
  menuLabel = (t) => `Actions for ${t}`,
  onMenuOpen,
}: ThumbnailGridProps) {
  const [context, setContext] = useState<{ id: string; x: number; y: number } | null>(null);
  const contextItem = context ? items.find((i) => i.id === context.id) : undefined;
  return (
    <ul className={`dam-grid${(selectedIds?.length ?? 0) > 0 ? ' dam-grid--selecting' : ''}`} aria-label={label}>
      {items.map((t) => {
        const isSelected = selectedIds?.includes(t.id) ?? false;
        return (
          <li
            key={t.id}
            className={`dam-thumb${isSelected ? ' dam-thumb--selected' : ''}`}
            onContextMenu={(e) => {
              if (!cardActions || cardActions(t.id).length === 0) return;
              e.preventDefault();
              onMenuOpen?.(t.id);
              setContext({ id: t.id, x: e.clientX, y: e.clientY });
            }}
          >
            {onToggleSelect && (
              <label className="dam-thumb__select-wrap">
                <input
                  type="checkbox"
                  className="dam-thumb__select"
                  aria-label={selectLabel(t.title)}
                  checked={isSelected}
                  onChange={(e) => onToggleSelect(t.id, e.target.checked)}
                />
                <Check weight="bold" className="dam-thumb__select-check" aria-hidden="true" />
              </label>
            )}
            {cardActions && <CardMenu label={menuLabel(t.title)} actions={cardActions(t.id)} onOpen={() => onMenuOpen?.(t.id)} />}
            <button type="button" className="dam-thumb__open" onClick={() => onOpen?.(t.id)}>
              <ImageWithFallback
                className="dam-thumb__img"
                src={t.imageUrl}
                loading="lazy"
                fallback={
                  <div className="dam-thumb__img dam-thumb__img--empty" aria-hidden="true">
                    {t.placeholder}
                  </div>
                }
              />
              <div className="dam-thumb__meta">
                <div className="dam-thumb__title">{t.title}</div>
                {(t.icon || t.subtitle) && (
                  <div className="dam-thumb__sub">
                    {t.icon}
                    {t.subtitle}
                  </div>
                )}
                {t.meta && <div className="dam-thumb__metaline">{t.meta}</div>}
                {t.badge && (
                  <Badge tone={t.badgeTone} size="sm">
                    {t.badge}
                  </Badge>
                )}
              </div>
            </button>
            {t.footer && <div className="dam-thumb__footer">{t.footer}</div>}
          </li>
        );
      })}
      {context && contextItem && cardActions && (
        <ContextMenu
          x={context.x}
          y={context.y}
          label={menuLabel(contextItem.title)}
          items={cardActions(context.id)}
          onClose={() => setContext(null)}
        />
      )}
    </ul>
  );
}

/** Placeholder cards with the same footprint as `ThumbnailGrid` cards, so content replaces them without the page
 * jumping. Announces `label` once to assistive tech. */
export function ThumbnailGridSkeleton({ count = 10, label = 'Loading' }: { count?: number; label?: string }) {
  return (
    <div role="status" aria-live="polite">
      <span className="dam-sr-only">{label}</span>
      <ul className="dam-grid" aria-hidden="true">
        {Array.from({ length: count }, (_, i) => (
          <li key={i} className="dam-thumb dam-thumb--skeleton">
            <div className="dam-thumb__skel-img">
              <Skeleton height="100%" />
            </div>
            <div className="dam-thumb__meta">
              <Skeleton height={14} width="70%" />
              <div className="dam-thumb__skel-line">
                <Skeleton height={12} width="40%" />
              </div>
              <div className="dam-thumb__skel-line">
                <Skeleton height={18} width={56} />
              </div>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}
