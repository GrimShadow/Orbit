import { useRef, type ReactNode } from 'react';
import { useIndicator } from './useIndicator';

export interface SegmentedOption {
  value: string;
  label: string;
  icon?: ReactNode;
}

export interface SegmentedControlProps {
  /** Accessible name of the group, e.g. "View". */
  label: string;
  value: string;
  onChange: (value: string) => void;
  options: SegmentedOption[];
  size?: 'sm' | 'md';
}

/** A group of `aria-pressed` toggle buttons with one animated selected pill - for view switches (grid / list). */
export function SegmentedControl({ label, value, onChange, options }: SegmentedControlProps) {
  const root = useRef<HTMLDivElement>(null);
  const pill = useIndicator(root, value);
  return (
    <div ref={root} role="group" aria-label={label} className={`dam-segmented${pill.ready ? '' : ' dam-segmented--static'}`}>
      <span className="dam-segmented__pill" aria-hidden="true" style={pill.style} />
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          className="dam-segmented__btn"
          aria-pressed={o.value === value}
          data-indicator-key={o.value}
          onClick={() => onChange(o.value)}
        >
          {o.icon}
          {o.label}
        </button>
      ))}
    </div>
  );
}
