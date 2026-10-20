import { CaretRight } from '@phosphor-icons/react';
import { useId, useState, type ReactNode } from 'react';

/** A heading whose body folds away: a real button inside the heading (so the outline stays intact) controlling a region. */
export function Disclosure({
  title,
  meta,
  defaultOpen = false,
  actions,
  children,
}: {
  title: string;
  /** Quiet text after the title ("4 lines"). */
  meta?: string;
  defaultOpen?: boolean;
  /** Controls that sit at the end of the heading row and stay usable while folded (Copy). */
  actions?: ReactNode;
  children: ReactNode;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const id = useId();
  return (
    <div className="dam-ai-disc" data-open={open || undefined}>
      <div className="dam-ai-disc__row">
        <h3 className="dam-ai-sub dam-ai-disc__title">
          <button
            type="button"
            className="dam-ai-disc__toggle"
            aria-expanded={open}
            aria-controls={id}
            onClick={() => setOpen((o) => !o)}
          >
            <CaretRight className="dam-ai-disc__caret" size={14} weight="bold" aria-hidden="true" />
            <span>{title}</span>
            {meta && <span className="dam-ai-disc__meta">{meta}</span>}
          </button>
        </h3>
        {actions}
      </div>
      <div id={id} className="dam-ai-disc__body" hidden={!open}>
        {children}
      </div>
    </div>
  );
}
