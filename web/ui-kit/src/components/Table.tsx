import type { ReactNode } from 'react';
import { Skeleton } from './Skeleton';

export interface Column<T> {
  key: string;
  header: string;
  render?: (row: T) => ReactNode;
  sortable?: boolean;
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
}: TableProps<T>) {
  const cell = (row: T, c: Column<T>): ReactNode =>
    c.render ? c.render(row) : String((row as Record<string, unknown>)[c.key] ?? '');
  return (
    <div className="dam-table-wrap">
      <table className="dam-table">
        <caption className="dam-sr-only">{caption}</caption>
        <thead>
          <tr>
            {columns.map((c) => {
              const active = sort?.key === c.key;
              const ariaSort = active ? (sort.direction === 'asc' ? 'ascending' : 'descending') : undefined;
              return (
                <th key={c.key} scope="col" aria-sort={c.sortable ? (ariaSort ?? 'none') : undefined}>
                  {c.sortable && onSortChange ? (
                    <button
                      type="button"
                      className="dam-sort"
                      onClick={() => onSortChange({ key: c.key, direction: active && sort.direction === 'asc' ? 'desc' : 'asc' })}
                    >
                      {c.header}
                      <span aria-hidden="true">{active ? (sort.direction === 'asc' ? '▲' : '▼') : '↕'}</span>
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
                  {columns.map((c) => (
                    <td key={c.key}>
                      <Skeleton height={16} />
                    </td>
                  ))}
                </tr>
              ))
            : rows.map((r) => (
                <tr key={getRowId(r)}>
                  {columns.map((c) => (
                    <td key={c.key}>{cell(r, c)}</td>
                  ))}
                </tr>
              ))}
        </tbody>
      </table>
      {!loading && rows.length === 0 && <div className="dam-empty">{emptyMessage}</div>}
    </div>
  );
}
