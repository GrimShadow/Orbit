import type { HTMLAttributes, ReactNode } from 'react';

export type BadgeTone = 'neutral' | 'info' | 'success' | 'warning' | 'danger' | 'brand';

export interface BadgeProps extends Omit<HTMLAttributes<HTMLSpanElement>, 'children'> {
  /** `neutral` (the default) keeps the plain `dam-badge` look consumers already rely on. */
  tone?: BadgeTone;
  size?: 'sm' | 'md';
  children: ReactNode;
}

/** A small status label. Colour is never the only signal - the text carries the meaning. */
export function Badge({ tone = 'neutral', size = 'md', className, children, ...rest }: BadgeProps) {
  const cls = ['dam-badge', tone !== 'neutral' && `dam-badge--${tone}`, size === 'sm' && 'dam-badge--sm', className]
    .filter(Boolean)
    .join(' ');
  return (
    <span className={cls} {...rest}>
      {children}
    </span>
  );
}
