import { forwardRef, useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from 'react';

interface FieldProps {
  label: string;
  hint?: string;
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

export const Select = forwardRef<
  HTMLSelectElement,
  FieldProps & { options: SelectOption[] } & SelectHTMLAttributes<HTMLSelectElement>
>(function Select({ label, hint, error, options, className, ...rest }, ref) {
  const id = useId();
  return (
    <Frame id={id} label={label} hint={hint} error={error}>
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
    </Frame>
  );
});
