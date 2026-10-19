import { CaretDown, CaretUp, CaretUpDown } from '@phosphor-icons/react';
import type { ReactNode } from 'react';
import { Skeleton } from './Skeleton';

export interface Column<T> {
  key: string;
  header: ReactNode;
  render?: (row: T) => ReactNode;
  sortable?: boolean;
  /** Extra class for this column's header and cells (e.g. `dam-col--hide-sm` to drop it on phones). */
  className?: string;
}
export interface SortState {
  key: string;
  direction: 'asc' | 'desc';
}

export interface TableProps<T> {
  columns: Column<T>[];
  rows: T[];
  getRowId: (row: T) => string;
  /** Accessible name for the table (required: every data table needs one). */
  caption: string;
  sort?: SortState;
  onSortChange?: (sort: SortState) => void;
  loading?: boolean;
  emptyMessage?: string;
  /** When set, a checkbox column is added (same optional-selection contract as `ThumbnailGrid`). */
  selectedIds?: string[];
  onToggleSelect?: (id: string, selected: boolean) => void;
  selectLabel?: (row: T) => string;
}

export function Table<T>({
  columns,
  rows,
  getRowId,
  caption,
  sort,
  onSortChange,
  loading,
  emptyMessage = 'Nothing to show.',
  selectedIds,
  onToggleSelect,
  selectLabel = (row) => `Select ${getRowId(row)}`,
}: TableProps<T>) {
  const cell = (row: T, c: Column<T>): ReactNode =>
    c.render ? c.render(row) : String((row as Record<string, unknown>)[c.key] ?? '');
  return (
    <div className="dam-table-wrap">
      <table className="dam-table">
        <caption className="dam-sr-only">{caption}</caption>
        <thead>
          <tr>
            {onToggleSelect && (
              <th scope="col">
                <span className="dam-sr-only">Select</span>
              </th>
            )}
            {columns.map((c) => {
              const active = sort?.key === c.key;
              const ariaSort = active ? (sort.direction === 'asc' ? 'ascending' : 'descending') : undefined;
              const SortIcon = active ? (sort.direction === 'asc' ? CaretUp : CaretDown) : CaretUpDown;
              return (
                <th key={c.key} scope="col" className={c.className} aria-sort={c.sortable ? (ariaSort ?? 'none') : undefined}>
                  {c.sortable && onSortChange ? (
                    <button
                      type="button"
                      className="dam-sort"
                      onClick={() => onSortChange({ key: c.key, direction: active && sort.direction === 'asc' ? 'desc' : 'asc' })}
                    >
                      {c.header}
                      <SortIcon size={12} weight="bold" aria-hidden="true" className="dam-sort__icon" />
                    </button>
                  ) : (
                    c.header
                  )}
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {loading
            ? Array.from({ length: 4 }, (_, i) => (
                <tr key={i}>
                  {onToggleSelect && (
                    <td>
                      <Skeleton height={16} width={16} />
                    </td>
                  )}
                  {columns.map((c) => (
                    <td key={c.key} className={c.className}>
                      <Skeleton height={16} />
                    </td>
                  ))}
                </tr>
              ))
            : rows.map((r) => {
                const id = getRowId(r);
                const selected = selectedIds?.includes(id) ?? false;
                return (
                  <tr key={id} className={selected ? 'dam-table__row--selected' : undefined}>
                    {onToggleSelect && (
                      <td>
                        <input
                          type="checkbox"
                          className="dam-check__input"
                          aria-label={selectLabel(r)}
                          checked={selected}
                          onChange={(e) => onToggleSelect(id, e.target.checked)}
                        />
                      </td>
                    )}
                    {columns.map((c) => (
                      <td key={c.key} className={c.className}>
                        {cell(r, c)}
                      </td>
                    ))}
                  </tr>
                );
              })}
        </tbody>
      </table>
      {!loading && rows.length === 0 && <div className="dam-empty">{emptyMessage}</div>}
    </div>
  );
}
