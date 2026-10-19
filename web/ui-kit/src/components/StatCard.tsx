import type { ReactNode } from 'react';
import type { BadgeTone } from './Badge';

export interface StatCardProps {
  label: string;
  value: string | number;
  hint?: string;
  icon?: ReactNode;
  tone?: BadgeTone;
  /** Right-aligned change indicator (a `TrendBadge`). */
  trend?: ReactNode;
  /** A decorative chart along the bottom (a `Sparkline`). */
  chart?: ReactNode;
}

/** A single figure with its label - for the dashboard's row of counts. */
export function StatCard({ label, value, hint, icon, tone = 'neutral', trend, chart }: StatCardProps) {
  return (
    <div className={`dam-stat dam-stat--${tone}`}>
      {icon && (
        <div className="dam-stat__icon" aria-hidden="true">
          {icon}
        </div>
      )}
      <div className="dam-stat__body">
        <span className="dam-stat__label">{label}</span>
        <span className="dam-stat__value">{value}</span>
        {hint && <span className="dam-stat__hint">{hint}</span>}
      </div>
      {trend && <div className="dam-stat__trend">{trend}</div>}
      {chart && <div className="dam-stat__chart">{chart}</div>}
    </div>
  );
}
