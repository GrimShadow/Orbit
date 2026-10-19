import { useTranslation } from 'react-i18next';
import { Button, X } from '../../ui';
import './filterChips.css';

export interface FilterChip {
  /** Stable key (e.g. `status:draft`). */
  id: string;
  /** The facet name, e.g. "Status". */
  group: string;
  /** The chosen value, e.g. "Draft". */
  label: string;
  onRemove: () => void;
}

/** The active filters as removable chips plus a "Clear filters" action - shared by the library and search screens so a
 * filter looks and is undone the same way in both. Renders nothing when there are no filters. */
export function FilterChips({
  chips,
  onClear,
  label,
  clearLabel,
}: {
  chips: FilterChip[];
  onClear: () => void;
  label?: string;
  /** Text of the clear action; "Clear filters" by default. */
  clearLabel?: string;
}) {
  const { t } = useTranslation();
  if (chips.length === 0) return null;
  return (
    <div className="dam-filterchips" role="group" aria-label={label ?? t('library.activeFilters')}>
      {chips.map((c) => (
        <span key={c.id} className="dam-chip">
          <span className="dam-chip__text">
            <span className="dam-filterchips__key">{c.group}:</span> {c.label}
          </span>
          <button
            type="button"
            className="dam-chip__remove"
            aria-label={t('library.removeFilter', { group: c.group, label: c.label })}
            onClick={c.onRemove}
          >
            <X size={12} weight="bold" aria-hidden="true" />
          </button>
        </span>
      ))}
      <Button variant="ghost" size="sm" onClick={onClear}>
        {clearLabel ?? t('library.clearFilters')}
      </Button>
    </div>
  );
}
