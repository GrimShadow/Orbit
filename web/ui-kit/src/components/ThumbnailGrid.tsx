export interface Thumb {
  id: string;
  title: string;
  subtitle?: string;
  imageUrl?: string;
  badge?: string;
}

interface ThumbnailGridProps {
  items: Thumb[];
  label: string;
  onOpen?: (id: string) => void;
  selectedIds?: string[];
  onToggleSelect?: (id: string, selected: boolean) => void;
  selectLabel?: (title: string) => string;
}

export function ThumbnailGrid({
  items,
  label,
  onOpen,
  selectedIds,
  onToggleSelect,
  selectLabel = (t) => `Select ${t}`,
}: ThumbnailGridProps) {
  return (
    <ul className="dam-grid" aria-label={label}>
      {items.map((t) => (
        <li key={t.id} className="dam-thumb">
          {onToggleSelect && (
            <input
              type="checkbox"
              className="dam-thumb__select"
              aria-label={selectLabel(t.title)}
              checked={selectedIds?.includes(t.id) ?? false}
              onChange={(e) => onToggleSelect(t.id, e.target.checked)}
            />
          )}
          <button type="button" className="dam-thumb__open" onClick={() => onOpen?.(t.id)}>
            {t.imageUrl ? (
              <img className="dam-thumb__img" src={t.imageUrl} alt="" loading="lazy" />
            ) : (
              <div className="dam-thumb__img" aria-hidden="true" />
            )}
            <div className="dam-thumb__meta">
              <div className="dam-thumb__title">{t.title}</div>
              {t.subtitle && <div className="dam-thumb__sub">{t.subtitle}</div>}
              {t.badge && <span className="dam-badge">{t.badge}</span>}
            </div>
          </button>
        </li>
      ))}
    </ul>
  );
}
