import { CaretRight } from '@phosphor-icons/react';

export interface BreadcrumbItem {
  id: string;
  label: string;
}

interface BreadcrumbProps {
  /** Accessible name for the trail (required: every nav landmark needs one). */
  label: string;
  rootLabel: string;
  items: BreadcrumbItem[];
  onNavigate: (id: string | undefined) => void;
}

/** A clickable path trail (root -> ... -> current), the current segment as plain text. Ancestors first, the item
 * furthest along the path last. */
export function Breadcrumb({ label, rootLabel, items, onNavigate }: BreadcrumbProps) {
  return (
    <nav aria-label={label} className="dam-breadcrumb">
      <ol>
        <li>
          {items.length === 0 ? (
            <span aria-current="page">{rootLabel}</span>
          ) : (
            <button type="button" onClick={() => onNavigate(undefined)}>
              {rootLabel}
            </button>
          )}
        </li>
        {items.map((item, i) => (
          <li key={item.id}>
            <CaretRight aria-hidden="true" className="dam-breadcrumb__sep" />
            {i === items.length - 1 ? (
              <span aria-current="page">{item.label}</span>
            ) : (
              <button type="button" onClick={() => onNavigate(item.id)}>
                {item.label}
              </button>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}
