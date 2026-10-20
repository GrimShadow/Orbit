import { ArrowsClockwise, Copy, Lock, Repeat, Timer, Warning } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Button, EmptyState, PageHeader } from '@dam/ui-kit';
import { isForbidden } from '../analytics/analyticsApi';
import { formatBytes } from '../../lib/format';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { KpiCard, KpiCardSkeleton, type KpiCardProps } from './KpiCard';
import { InsightsPeriodPicker } from './InsightsPeriodPicker';
import { InsightsTabs } from './InsightsTabs';
import { useKpis } from './insightsApi';
import type { KpiDto } from './insightsTypes';
import { changePercent, formatCount, formatDay, formatDecimal, formatHours, formatRatio } from './kpiFormat';
import { useInsightsPeriod } from './useInsightsPeriod';
import './insights.css';

type Translate = ReturnType<typeof useTranslation>['t'];

/** Turns the API's figures into what each card shows. Pure, so the wording and the null handling are testable on their own. */
export function buildKpiCards(k: KpiDto, t: Translate, locale: string): Omit<KpiCardProps, 'icon'>[] {
  const n = (v: number) => formatCount(v, locale);
  const ratio = (v: number | null) => (v === null ? null : formatRatio(v, locale));
  const hours = (v: number | null) => (v === null ? null : formatHours(v, t, locale));
  const days = k.period.days;
  const pe = k.publishingEffort;
  const du = k.duplicates;
  const re = k.reuse;
  const sla = k.workflowSla;

  return [
    {
      title: t('insights.kpi.publishing.title'),
      headlineLabel: t('insights.kpi.publishing.headline'),
      headline: hours(pe.medianHours),
      emptyReason: t('insights.kpi.publishing.empty'),
      caption: t('insights.kpi.publishing.caption', { count: pe.publishedCount }),
      change: changePercent(pe.medianHours, pe.previous.medianHours),
      better: 'down',
      previous: hours(pe.previous.medianHours),
      days,
      facts: [
        { label: t('insights.kpi.publishing.published'), value: n(pe.publishedCount) },
        { label: t('insights.kpi.publishing.p90'), value: hours(pe.p90Hours) },
        { label: t('insights.kpi.publishing.firstPass'), value: ratio(pe.firstPassRate) },
      ],
      measured: [t('insights.kpi.publishing.measured1'), t('insights.kpi.publishing.measured2')],
    },
    {
      title: t('insights.kpi.duplicates.title'),
      headlineLabel: t('insights.kpi.duplicates.headline'),
      headline: n(du.duplicatesPrevented),
      emptyReason: '',
      caption:
        du.rate === null
          ? t('insights.kpi.duplicates.empty')
          : t('insights.kpi.duplicates.caption', { prevented: n(du.duplicatesPrevented), uploads: n(du.uploads) }),
      change: changePercent(du.duplicatesPrevented, du.previous.duplicatesPrevented),
      better: 'up',
      previous: n(du.previous.duplicatesPrevented),
      days,
      facts: [
        { label: t('insights.kpi.duplicates.uploads'), value: n(du.uploads) },
        { label: t('insights.kpi.duplicates.rate'), value: ratio(du.rate) },
        { label: t('insights.kpi.duplicates.bytes'), value: formatBytes(du.bytesNotStored) },
        { label: t('insights.kpi.duplicates.inLibrary'), value: n(du.duplicateAssetsInLibrary) },
      ],
      measured: [t('insights.kpi.duplicates.measured1'), t('insights.kpi.duplicates.measured2')],
    },
    {
      title: t('insights.kpi.reuse.title'),
      headlineLabel: t('insights.kpi.reuse.headline'),
      headline: ratio(re.rate),
      emptyReason: t('insights.kpi.reuse.empty'),
      caption: t('insights.kpi.reuse.caption', { reused: n(re.reusedAssets), used: n(re.usedAssets) }),
      change: changePercent(re.rate, re.previous.rate),
      better: 'up',
      previous: ratio(re.previous.rate),
      days,
      facts: [
        { label: t('insights.kpi.reuse.used'), value: n(re.usedAssets) },
        { label: t('insights.kpi.reuse.reused'), value: n(re.reusedAssets) },
        { label: t('insights.kpi.reuse.multi'), value: n(re.multiChannelAssets) },
        {
          label: t('insights.kpi.reuse.avg'),
          value: re.avgUsesPerUsedAsset === null ? null : formatDecimal(re.avgUsesPerUsedAsset, locale),
        },
      ],
      measured: [t('insights.kpi.reuse.measured1'), t('insights.kpi.reuse.measured2')],
    },
    {
      title: t('insights.kpi.sla.title'),
      headlineLabel: t('insights.kpi.sla.headline'),
      headline: ratio(sla.attainment),
      emptyReason: t('insights.kpi.sla.empty'),
      caption: t('insights.kpi.sla.caption', { onTime: n(sla.onTime), decided: n(sla.decided) }),
      change: changePercent(sla.attainment, sla.previous.attainment),
      better: 'up',
      previous: ratio(sla.previous.attainment),
      days,
      facts: [
        { label: t('insights.kpi.sla.decided'), value: n(sla.decided) },
        { label: t('insights.kpi.sla.onTime'), value: n(sla.onTime) },
        { label: t('insights.kpi.sla.breached'), value: n(sla.breached) },
        { label: t('insights.kpi.sla.overdue'), value: sla.overdueOpen === null ? null : n(sla.overdueOpen) },
      ],
      measured: [t('insights.kpi.sla.measured1'), t('insights.kpi.sla.measured2')],
    },
  ];
}

const ICONS = [
  <Timer key="p" size={20} weight="duotone" />,
  <Copy key="d" size={20} weight="duotone" />,
  <Repeat key="r" size={20} weight="duotone" />,
  <Warning key="s" size={20} weight="duotone" />,
];

/** `/insights/kpis` - the four key results the business cares about, for a chosen window against the one before it. */
export function KeyResultsPage() {
  const { t, i18n } = useTranslation();
  useDocumentTitle(t('insights.kpis.pageTitle'));
  const [days, setDays] = useInsightsPeriod();
  const query = useKpis(days);
  const k = query.data;

  let body;
  if (isForbidden(query.error)) {
    body = <EmptyState icon={<Lock />} title={t('insights.noAccessTitle')} description={t('insights.noAccessBody')} />;
  } else if (query.error) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<Warning />}
          title={t('insights.kpis.error')}
          description={t('insights.kpis.errorHint')}
          action={<Button onClick={() => void query.refetch()}>{t('insights.retry')}</Button>}
        />
      </div>
    );
  } else if (!k) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('insights.kpis.loading')}</span>
        <div className="dam-kpis">
          {ICONS.map((_, i) => (
            <KpiCardSkeleton key={i} />
          ))}
        </div>
      </div>
    );
  } else {
    const cards = buildKpiCards(k, t, i18n.language);
    body = (
      <>
        <p className="dam-ins__period" role="status">
          {t('insights.kpis.periodLine', {
            from: formatDay(k.period.from, i18n.language),
            to: formatDay(k.period.to, i18n.language),
            prevFrom: formatDay(k.previous.from, i18n.language),
            prevTo: formatDay(k.previous.to, i18n.language),
            days: k.period.days,
          })}
        </p>
        <div className="dam-kpis" aria-busy={query.isFetching}>
          {cards.map((c, i) => (
            <KpiCard key={c.title} {...c} icon={ICONS[i]} />
          ))}
        </div>
      </>
    );
  }

  return (
    <div className="dam-page dam-ins">
      <PageHeader
        eyebrow={t('insights.title')}
        title={t('insights.kpis.pageTitle')}
        description={t('insights.kpis.description')}
      />
      <InsightsTabs />
      <div className="dam-ins__toolbar">
        <InsightsPeriodPicker days={days} onChange={setDays} />
        {k && (
          <Button variant="ghost" size="sm" loading={query.isFetching} onClick={() => void query.refetch()}>
            <ArrowsClockwise size={14} weight="bold" aria-hidden="true" />
            {t('insights.refresh')}
          </Button>
        )}
      </div>
      {body}
    </div>
  );
}
