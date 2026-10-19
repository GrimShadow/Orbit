import { forwardRef, type ButtonHTMLAttributes } from 'react';

export interface IconButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'aria-label' | 'title'> {
  /** The accessible name; also shown as the native tooltip. */
  label: string;
  variant?: 'ghost' | 'secondary' | 'primary';
  size?: 'sm' | 'md';
  loading?: boolean;
}

/** A square button whose only visible content is an icon (36px md / 32px sm). Shares the Button variants. */
export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(function IconButton(
  { label, variant = 'ghost', size = 'md', loading = false, disabled, className, children, type = 'button', ...rest },
  ref,
) {
  const cls = ['dam-btn', 'dam-btn--icon', `dam-btn--${variant}`, size === 'sm' && 'dam-btn--sm', className]
    .filter(Boolean)
    .join(' ');
  return (
    <button
      ref={ref}
      type={type}
      className={cls}
      aria-label={label}
      title={label}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      {...rest}
    >
      {loading ? <span className="dam-spinner" aria-hidden="true" /> : children}
    </button>
  );
});
