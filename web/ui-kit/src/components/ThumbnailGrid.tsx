import { Check } from '@phosphor-icons/react';
import type { ReactNode } from 'react';
import { CardMenu, type CardAction } from './CardMenu';

export interface Thumb {
  id: string;
  title: string;
  subtitle?: string;
  imageUrl?: string;
  badge?: string;
  /** Shown inline before the subtitle - typically a small file-type glyph (see `FileTypeIcon`). */
  icon?: ReactNode;
  /** A secondary metadata line below the subtitle, e.g. a relative "Updated 6 days ago". */
  meta?: string;
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
}: ThumbnailGridProps) {
  return (
    <ul className="dam-grid" aria-label={label}>
      {items.map((t) => {
        const isSelected = selectedIds?.includes(t.id) ?? false;
        return (
          <li key={t.id} className={`dam-thumb${isSelected ? ' dam-thumb--selected' : ''}`}>
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
            {cardActions && <CardMenu label={menuLabel(t.title)} actions={cardActions(t.id)} />}
            <button type="button" className="dam-thumb__open" onClick={() => onOpen?.(t.id)}>
              {t.imageUrl ? (
                <img className="dam-thumb__img" src={t.imageUrl} alt="" loading="lazy" />
              ) : (
                <div className="dam-thumb__img" aria-hidden="true" />
              )}
              <div className="dam-thumb__meta">
                <div className="dam-thumb__title">{t.title}</div>
                {(t.icon || t.subtitle) && (
                  <div className="dam-thumb__sub">
                    {t.icon}
                    {t.subtitle}
                  </div>
                )}
                {t.meta && <div className="dam-thumb__metaline">{t.meta}</div>}
                {t.badge && <span className="dam-badge">{t.badge}</span>}
              </div>
            </button>
          </li>
        );
      })}
    </ul>
  );
}
