import { CaretDown } from '@phosphor-icons/react';
import { useId, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Card, Skeleton, TrendBadge } from '@dam/ui-kit';
import { verdictOf } from './kpiFormat';

export interface KpiFact {
  label: string;
  /** `null` is a figure that cannot be calculated yet; it reads "No data yet". */
  value: string | null;
}

export interface KpiCardProps {
  title: string;
  icon: ReactNode;
  /** What the headline figure is, e.g. "Median time to publish". */
  headlineLabel: string;
  /** The formatted figure, or `null` when there is nothing to measure. */
  headline: string | null;
  /** Shown under a figure, e.g. "12 of 80 uploads". */
  caption?: string;
  /** Why there is no figure, shown instead of one. */
  emptyReason: string;
  /** Percentage change of the headline versus the previous period, or `null`. */
  change: number | null;
  /** Which direction of change is good news for this metric. */
  better: 'up' | 'down';
  /** The previous period's headline, formatted; `null` when it had none. */
  previous: string | null;
  days: number;
  facts: KpiFact[];
  /** Plain-language definition, one paragraph per entry. */
  measured: string[];
}

const VERDICT_TONE = { better: 'good', worse: 'bad', same: 'flat', unknown: 'flat' } as const;

/** One of the four key results: the headline figure, how it moved against the previous period (in words as well as colour),
 * the supporting figures, and a short "How this is measured" disclosure that matches the definition in ADR-0037. */
export function KpiCard(p: KpiCardProps) {
  const { t } = useTranslation();
  const id = useId();
  const verdict = verdictOf(p.change, p.better);
  const previousLabel = t('insights.kpi.previousValue', {
    days: p.days,
    value: p.previous ?? t('insights.noDataYet'),
  });
  return (
    <Card as="section" className="dam-kpi" aria-labelledby={`${id}-title`}>
      <div className="dam-kpi__head">
        <span className="dam-kpi__icon" aria-hidden="true">
          {p.icon}
        </span>
        <h2 id={`${id}-title`} className="dam-kpi__title">
          {p.title}
        </h2>
      </div>

      <div className="dam-kpi__main">
        <span className="dam-kpi__label">{p.headlineLabel}</span>
        {p.headline === null ? (
          <>
            <span className="dam-kpi__value dam-kpi__value--empty">{t('insights.noDataYet')}</span>
            <span className="dam-kpi__caption">{p.emptyReason}</span>
          </>
        ) : (
          <>
            <span className="dam-kpi__value">{p.headline}</span>
            {p.caption && <span className="dam-kpi__caption">{p.caption}</span>}
          </>
        )}
      </div>

      <div className="dam-kpi__trend">
        <TrendBadge change={p.change} goodWhen={p.better} suffix={t('insights.vsPrevious', { days: p.days })} />
        <span className={`dam-trend dam-trend--${VERDICT_TONE[verdict]}`}>{t(`insights.verdict.${verdict}`)}</span>
        <span className="dam-kpi__previous">{previousLabel}</span>
      </div>

      <dl className="dam-kpi__facts">
        {p.facts.map((f) => (
          <div key={f.label} className="dam-kpi__fact">
            <dt>{f.label}</dt>
            <dd>{f.value ?? <span className="dam-kpi__nodata">{t('insights.noDataYet')}</span>}</dd>
          </div>
        ))}
      </dl>

      <details className="dam-kpi__how">
        <summary>
          <CaretDown size={14} weight="bold" aria-hidden="true" className="dam-kpi__howicon" />
          {t('insights.howMeasured')}
        </summary>
        <div className="dam-kpi__howbody">
          {p.measured.map((m) => (
            <p key={m}>{m}</p>
          ))}
        </div>
      </details>
    </Card>
  );
}

export function KpiCardSkeleton() {
  return (
    <Card as="div" className="dam-kpi" aria-hidden="true">
      <Skeleton width="55%" height={18} />
      <Skeleton width="40%" height={36} />
      <Skeleton width="70%" height={14} />
      <Skeleton width="100%" height={72} />
    </Card>
  );
}
