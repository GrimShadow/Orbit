import { useId } from 'react';

export interface CheckboxOption {
  value: string;
  label: string;
  /** Shown end-aligned in tabular figures (e.g. a facet count). */
  count?: number;
  disabled?: boolean;
}

export interface CheckboxGroupProps {
  label: string;
  options: CheckboxOption[];
  value: string[];
  onChange: (next: string[]) => void;
  hint?: string;
  columns?: 1 | 2;
  /** Hides the legend visually (it stays available to assistive tech). */
  hideLabel?: boolean;
}

/** A fieldset of real checkboxes styled as a tidy list - the replacement for every `<select multiple>`. */
export function CheckboxGroup({ label, options, value, onChange, hint, columns = 1, hideLabel }: CheckboxGroupProps) {
  const id = useId();
  const toggle = (v: string, on: boolean) => onChange(on ? [...value, v] : value.filter((x) => x !== v));
  return (
    <fieldset className="dam-checkgroup" aria-describedby={hint ? `${id}-hint` : undefined}>
      <legend className={hideLabel ? 'dam-sr-only' : 'dam-checkgroup__legend'}>{label}</legend>
      <ul className={`dam-checkgroup__list${columns === 2 ? ' dam-checkgroup__list--2' : ''}`}>
        {options.map((o) => (
          <li key={o.value}>
            <label className={`dam-check${o.disabled ? ' dam-check--disabled' : ''}`}>
              <input
                type="checkbox"
                className="dam-check__input"
                value={o.value}
                checked={value.includes(o.value)}
                disabled={o.disabled}
                onChange={(e) => toggle(o.value, e.target.checked)}
              />
              <span className="dam-check__label">{o.label}</span>
              {o.count !== undefined && <span className="dam-check__count">{o.count}</span>}
            </label>
          </li>
        ))}
      </ul>
      {hint && (
        <span id={`${id}-hint`} className="dam-hint">
          {hint}
        </span>
      )}
    </fieldset>
  );
}
