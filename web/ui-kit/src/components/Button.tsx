import { forwardRef, type ButtonHTMLAttributes } from 'react';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
  size?: 'md' | 'sm';
  loading?: boolean;
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'secondary', size = 'md', loading = false, disabled, className, children, type = 'button', ...rest },
  ref,
) {
  const cls = ['dam-btn', `dam-btn--${variant}`, size === 'sm' && 'dam-btn--sm', className].filter(Boolean).join(' ');
  return (
    <button ref={ref} type={type} className={cls} disabled={disabled || loading} aria-busy={loading || undefined} {...rest}>
      {loading && <span className="dam-spinner" aria-hidden="true" />}
      {children}
    </button>
  );
});
