import type { ReactNode } from 'react';

export interface PageHeaderProps {
  title: string;
  description?: string;
  /** Small uppercase context line above the title, e.g. the section name. */
  eyebrow?: string;
  /** Primary/secondary actions, end-aligned on the title row. */
  actions?: ReactNode;
  /** A toolbar row (filters, view switch, search) rendered below the title row. */
  children?: ReactNode;
  /** The page title is an h1 by default; pass 'h2' when the page already has one. */
  titleAs?: 'h1' | 'h2';
}

export function PageHeader({ title, description, eyebrow, actions, children, titleAs: Title = 'h1' }: PageHeaderProps) {
  return (
    <header className="dam-pagehead">
      <div className="dam-pagehead__row">
        <div className="dam-pagehead__text">
          {eyebrow && <span className="dam-pagehead__eyebrow">{eyebrow}</span>}
          <Title className="dam-pagehead__title">{title}</Title>
          {description && <p className="dam-pagehead__desc">{description}</p>}
        </div>
        {actions && <div className="dam-pagehead__actions">{actions}</div>}
      </div>
      {children && <div className="dam-pagehead__toolbar">{children}</div>}
    </header>
  );
}
