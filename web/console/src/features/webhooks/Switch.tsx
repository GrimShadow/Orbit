import { useId } from 'react';

interface SwitchProps {
  checked: boolean;
  onChange: (next: boolean) => void;
  /** Visible text beside the switch; it is also the accessible name (`ariaLabel` adds context, e.g. which webhook). */
  label: string;
  ariaLabel?: string;
  hint?: string;
  disabled?: boolean;
}

/** An on/off switch (`role="switch"`): a button, so Space and Enter toggle it and a form never submits through it. */
export function Switch({ checked, onChange, label, ariaLabel, hint, disabled }: SwitchProps) {
  const id = useId();
  return (
    <span className="dam-whswitchwrap">
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-label={ariaLabel ?? label}
        aria-describedby={hint ? `${id}-hint` : undefined}
        disabled={disabled}
        className="dam-whswitch"
        onClick={() => onChange(!checked)}
      >
        <span className="dam-whswitch__thumb" aria-hidden="true" />
      </button>
      <span className="dam-whswitchwrap__text" aria-hidden={ariaLabel ? true : undefined}>
        {label}
        {hint && (
          <span id={`${id}-hint`} className="dam-hint dam-whswitchwrap__hint">
            {hint}
          </span>
        )}
      </span>
    </span>
  );
}
