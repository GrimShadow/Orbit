import type { ReactNode } from 'react';
import './pages.css';

/** The Orbit motif used by full-page states: a planet on a ring, with a moon that has drifted off its orbit
 * (`lost`) or a warning badge on the planet (`fault`). Purely decorative. */
export function StatusArt({ variant }: { variant: 'lost' | 'fault' }) {
  return (
    <svg className="dam-statusart" viewBox="0 0 200 150" fill="none" aria-hidden="true" focusable="false">
      <circle className="dam-statusart__halo" cx="100" cy="75" r="66" />
      <ellipse className="dam-statusart__track" cx="100" cy="75" rx="82" ry="34" transform="rotate(-24 100 75)" />
      <circle className="dam-statusart__planet" cx="100" cy="75" r="24" />
      <path className="dam-statusart__ring" d="M 25.1 108.35 A 82 34 -24 0 0 174.9 41.65" />
      {variant === 'lost' ? (
        <>
          <circle className="dam-statusart__moon" cx="186" cy="116" r="7" />
          <path className="dam-statusart__trail" d="M 174.9 41.65 Q 200 76 188 108" />
        </>
      ) : (
        <circle className="dam-statusart__moon" cx="174.9" cy="41.65" r="7" />
      )}
      {variant === 'fault' && (
        <g className="dam-statusart__alert" transform="translate(112 54)">
          <circle cx="12" cy="12" r="12" />
          <path d="M12 6.5v7M12 17v.5" strokeWidth="2.4" strokeLinecap="round" />
        </g>
      )}
    </svg>
  );
}

export interface StatusPanelProps {
  variant: 'lost' | 'fault';
  title: string;
  description: string;
  actions?: ReactNode;
  /** `alert` for failures that appear unprompted (render errors, a failed callback). */
  role?: 'alert';
  /** Shown beneath the description in a muted, selectable line (e.g. a support reference). */
  detail?: ReactNode;
}

/** A full-page state (not found, crash, failed sign-in): art, an h1, guidance and next steps. Reuses the ui-kit
 * `EmptyState` look but owns the page's only h1. */
export function StatusPanel({ variant, title, description, actions, role, detail }: StatusPanelProps) {
  return (
    <div className="dam-statuspage" role={role}>
      <StatusArt variant={variant} />
      <div className="dam-emptystate dam-statuspage__body">
        <h1 className="dam-emptystate__title dam-statuspage__title">{title}</h1>
        <p className="dam-emptystate__desc">{description}</p>
        {detail && <div className="dam-statuspage__detail">{detail}</div>}
        {actions && <div className="dam-emptystate__action">{actions}</div>}
      </div>
    </div>
  );
}
