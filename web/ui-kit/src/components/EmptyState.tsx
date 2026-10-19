import type { ReactNode } from 'react';

export interface EmptyStateProps {
  /** A Phosphor icon (or any node); sits in a soft brand-tinted well. */
  icon?: ReactNode;
  title: string;
  description?: string;
  /** One or more buttons/links offering the obvious next step. */
  action?: ReactNode;
  /** Heading level of the title - keep the page's heading order intact (h2 under a page h1; p inside a panel). */
  titleAs?: 'h2' | 'h3' | 'p';
}

/** A composed "nothing here yet" block: icon well, title, one line of guidance and an action. */
export function EmptyState({ icon, title, description, action, titleAs: Title = 'h2' }: EmptyStateProps) {
  return (
    <div className="dam-emptystate">
      {icon && (
        <div className="dam-emptystate__icon" aria-hidden="true">
          {icon}
        </div>
      )}
      <Title className="dam-emptystate__title">{title}</Title>
      {description && <p className="dam-emptystate__desc">{description}</p>}
      {action && <div className="dam-emptystate__action">{action}</div>}
    </div>
  );
}
