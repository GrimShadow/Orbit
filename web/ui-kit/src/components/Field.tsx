import { CaretDown, WarningCircle } from '@phosphor-icons/react';
import { forwardRef, useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from 'react';

interface FieldProps {
  label: string;
  hint?: string;
  /** Renders an inline message below the control and marks it `aria-invalid`. */
  error?: string;
}

function describedBy(id: string, hint?: string, error?: string) {
  return [hint && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(' ') || undefined;
}

function Frame({ id, label, hint, error, children }: FieldProps & { id: string; children: ReactNode }) {
  return (
    <div className="dam-field">
      <label className="dam-label" htmlFor={id}>
        {label}
      </label>
      {children}
      {hint && (
        <span id={`${id}-hint`} className="dam-hint">
          {hint}
        </span>
      )}
      {error && (
        <span id={`${id}-error`} className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </span>
      )}
    </div>
  );
}

export const Input = forwardRef<HTMLInputElement, FieldProps & InputHTMLAttributes<HTMLInputElement>>(function Input(
  { label, hint, error, className, ...rest },
  ref,
) {
  const id = useId();
  return (
    <Frame id={id} label={label} hint={hint} error={error}>
      <input
        ref={ref}
        id={id}
        className={['dam-input', className].filter(Boolean).join(' ')}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, hint, error)}
        {...rest}
      />
    </Frame>
  );
});

export interface SelectOption {
  value: string;
  label: string;
}

/** A native <select> (keyboard, mobile pickers and form semantics come free) with a custom chevron. */
export const Select = forwardRef<
  HTMLSelectElement,
  FieldProps & { options: SelectOption[] } & SelectHTMLAttributes<HTMLSelectElement>
>(function Select({ label, hint, error, options, className, ...rest }, ref) {
  const id = useId();
  return (
    <Frame id={id} label={label} hint={hint} error={error}>
      <span className="dam-select-wrap">
        <select
          ref={ref}
          id={id}
          className={['dam-select', className].filter(Boolean).join(' ')}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy(id, hint, error)}
          {...rest}
        >
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
        {!rest.multiple && <CaretDown size={14} weight="bold" aria-hidden="true" className="dam-select__chevron" />}
      </span>
    </Frame>
  );
});
