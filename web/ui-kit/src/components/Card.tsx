import { forwardRef, type HTMLAttributes } from 'react';

export interface CardProps extends HTMLAttributes<HTMLElement> {
  padding?: 'none' | 'sm' | 'md' | 'lg';
  /** Adds a hover lift and pointer cursor; pair it with an `onClick`/`role` or wrap a link inside. */
  interactive?: boolean;
  as?: 'div' | 'section' | 'article';
}

/** The raised surface: a bordered, softly shadowed panel above the page background. */
export const Card = forwardRef<HTMLElement, CardProps>(function Card(
  { padding = 'md', interactive = false, as: Tag = 'div', className, children, ...rest },
  ref,
) {
  const cls = ['dam-card', padding !== 'none' && `dam-card--pad-${padding}`, interactive && 'dam-card--interactive', className]
    .filter(Boolean)
    .join(' ');
  return (
    // The polymorphic tag is a plain container; any role/handlers come from the consumer's props.
    <Tag ref={ref as never} className={cls} {...rest}>
      {children}
    </Tag>
  );
});
