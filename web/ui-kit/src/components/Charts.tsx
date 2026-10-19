import { useId } from 'react';

/** Categorical series colours are `--dam-chart-1..6`; wrap past six. */
const seriesVar = (i: number) => `var(--dam-chart-${(i % 6) + 1})`;

export interface SparklineProps {
  values: number[];
  /** Accessible description, e.g. "Daily downloads, last 30 days". Omit to hide the chart from assistive tech. */
  label?: string;
  height?: number;
}

/** A tiny trend line with a soft area under it, drawn in the current text colour's brand tone. */
export function Sparkline({ values, label, height = 36 }: SparklineProps) {
  const gradient = useId();
  if (values.length < 2) return null;
  const max = Math.max(...values, 1);
  const w = 100;
  const step = w / (values.length - 1);
  const pts = values.map((v, i) => [i * step, height - 3 - (v / max) * (height - 8)] as const);
  const line = pts.map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${x.toFixed(2)} ${y.toFixed(2)}`).join(' ');
  const area = `${line} L${w} ${height} L0 ${height} Z`;
  return (
    <svg
      className="dam-spark"
      viewBox={`0 0 ${w} ${height}`}
      preserveAspectRatio="none"
      height={height}
      role={label ? 'img' : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
    >
      <defs>
        <linearGradient id={gradient} x1="0" x2="0" y1="0" y2="1">
          <stop offset="0" stopColor="currentColor" stopOpacity="0.22" />
          <stop offset="1" stopColor="currentColor" stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={area} fill={`url(#${gradient})`} />
      <path
        d={line}
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinejoin="round"
        strokeLinecap="round"
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  );
}

export interface BarDatum {
  /** Shown under the bar (when it is one of the labelled ones) and in its tooltip. */
  label: string;
  value: number;
  /** Overrides the tooltip, e.g. "12 Oct: 5 downloads". */
  title?: string;
}

export interface BarChartProps {
  data: BarDatum[];
  /** Accessible summary of the whole chart - bars are presentational. */
  label: string;
  height?: number;
  /** Label every Nth bar (plus the last one). Defaults to a density that fits ~6 labels. */
  labelEvery?: number;
  /** Colour index into the chart palette. */
  series?: number;
}

/** Vertical bars scaled to the largest value; empty (zero) bars keep a hairline so the time axis stays readable. */
export function BarChart({ data, label, height = 140, labelEvery, series = 0 }: BarChartProps) {
  const max = Math.max(...data.map((d) => d.value), 1);
  const every = labelEvery ?? Math.max(1, Math.ceil(data.length / 6));
  return (
    <div
      className="dam-bars"
      role="img"
      aria-label={label}
      style={{ ['--dam-bars-h' as string]: `${height}px`, ['--dam-bar' as string]: seriesVar(series) }}
    >
      <div className="dam-bars__plot">
        {data.map((d, i) => (
          <div key={`${d.label}-${i}`} className="dam-bars__col" title={d.title ?? `${d.label}: ${d.value}`}>
            <span
              className={`dam-bars__bar${d.value === 0 ? ' dam-bars__bar--zero' : ''}`}
              style={{ height: `${Math.max((d.value / max) * 100, 0)}%` }}
            />
          </div>
        ))}
      </div>
      <div className="dam-bars__axis" aria-hidden="true">
        {data.map((d, i) => (
          <span key={`${d.label}-${i}`} className="dam-bars__tick">
            {i % every === 0 || i === data.length - 1 ? d.label : ''}
          </span>
        ))}
      </div>
    </div>
  );
}

export interface DistributionSegment {
  label: string;
  value: number;
  /** Text shown in the legend instead of the raw count, e.g. "2.4 GB". */
  display?: string;
  /** Chart palette index; defaults to the segment's position. */
  series?: number;
}

export interface DistributionBarProps {
  segments: DistributionSegment[];
  /** Accessible name for the group. */
  label: string;
}

/** One proportional bar with a legend underneath - status pipeline, type mix, storage split. */
export function DistributionBar({ segments, label }: DistributionBarProps) {
  const shown = segments.filter((s) => s.value > 0);
  const total = shown.reduce((n, s) => n + s.value, 0);
  return (
    <div className="dam-dist" role="group" aria-label={label}>
      <div className="dam-dist__bar" aria-hidden="true">
        {total === 0 ? (
          <span className="dam-dist__seg dam-dist__seg--empty" style={{ flexGrow: 1 }} />
        ) : (
          shown.map((s) => (
            <span
              key={s.label}
              className="dam-dist__seg"
              title={`${s.label}: ${s.display ?? s.value}`}
              style={{ flexGrow: s.value, background: seriesVar(s.series ?? segments.indexOf(s)) }}
            />
          ))
        )}
      </div>
      <ul className="dam-dist__legend">
        {segments.map((s, i) => (
          <li key={s.label} className="dam-dist__item">
            <span className="dam-dist__dot" aria-hidden="true" style={{ background: seriesVar(s.series ?? i) }} />
            <span className="dam-dist__name">{s.label}</span>
            <span className="dam-dist__val">{s.display ?? s.value}</span>
            {total > 0 && <span className="dam-dist__pct">{Math.round((s.value / total) * 100)}%</span>}
          </li>
        ))}
      </ul>
    </div>
  );
}

export interface TrendBadgeProps {
  /** Percentage change versus the previous period; `null` when there is nothing to compare against. */
  change: number | null;
  /** Whether a rise is good news (usage) or bad news (e.g. unused assets). */
  goodWhen?: 'up' | 'down';
  /** Spoken suffix, e.g. "vs the previous 30 days". */
  suffix: string;
  /** Text for "no change / nothing to compare". */
  emptyLabel?: string;
}

export function TrendBadge({ change, goodWhen = 'up', suffix, emptyLabel = '' }: TrendBadgeProps) {
  if (change === null || !Number.isFinite(change))
    return emptyLabel ? <span className="dam-trend dam-trend--flat">{emptyLabel}</span> : null;
  const rounded = Math.round(change);
  const dir = rounded === 0 ? 'flat' : rounded > 0 ? 'up' : 'down';
  const tone = dir === 'flat' ? 'flat' : dir === goodWhen ? 'good' : 'bad';
  const arrow = dir === 'up' ? '↑' : dir === 'down' ? '↓' : '→';
  return (
    <span className={`dam-trend dam-trend--${tone}`}>
      <span aria-hidden="true">
        {arrow} {Math.abs(rounded)}%
      </span>
      <span className="dam-sr-only">{`${dir === 'flat' ? 'no change' : `${dir} ${Math.abs(rounded)}%`} ${suffix}`}</span>
    </span>
  );
}
