import { CheckSquareOffset, Clock, HardDrives, Lightning, NotePencil, PaperPlaneTilt, Stack } from '@phosphor-icons/react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import {
  BarChart,
  Card,
  DistributionBar,
  EmptyState,
  FileTypeIcon,
  SegmentedControl,
  Skeleton,
  Sparkline,
  StatCard,
  TrendBadge,
  type DistributionSegment,
} from '@dam/ui-kit';
import { isForbidden, useOverview, type AnalyticsOverview } from '../../features/analytics/analyticsApi';
import { UnusedAssetsPanel } from '../../features/analytics/UnusedAssets';
import { formatBytes } from '../../lib/format';
import { thumbnailIds } from '../../lib/thumbnails';
import { facetCount, useLibrarySnapshot } from './homeData';
import { useThumbnailLinks } from '../../features/asset/assetApi';
import { useTaskSummary, type TaskSummary } from '../../features/workflow/workflowApi';
import { useAssets } from '../../features/library/assets';
import { ListSkeleton, ReviewList, SectionEmpty, SectionNote } from './HomeSections';

export const USAGE_DAYS = 30;
const REVIEW_COUNT = 5;

/** Percentage change, or null when there is no earlier figure to compare against. */
export function percentChange(now: number, before: number): number | null {
  if (before === 0) return now === 0 ? 0 : null;
  return ((now - before) / before) * 100;
}

const STATUS_ORDER: { value: string; series: number }[] = [
  { value: 'draft', series: 5 },
  { value: 'in_review', series: 3 },
  { value: 'approved', series: 4 },
  { value: 'published', series: 1 },
  { value: 'expired', series: 2 },
  { value: 'archived', series: 0 },
];
const TYPE_ORDER = ['image', 'video', 'document', 'audio', 'other'];

function KpiSkeleton() {
  return (
    <div className="dam-stat" aria-hidden="true">
      <Skeleton width={40} height={40} />
      <div className="dam-stat__body">
        <Skeleton width={90} height={12} />
        <Skeleton width={56} height={26} />
      </div>
    </div>
  );
}

/** The four headline figures. Usage and storage come from the analytics overview; a role without `analytics.read` sees
 * the review and draft counts in their place, so the row is always full and always useful. */
export function Kpis() {
  const { t, i18n } = useTranslation();
  const snapshot = useLibrarySnapshot();
  const overview = useOverview(USAGE_DAYS);
  const tasks = useTaskSummary();
  const fmt = (n: number) => new Intl.NumberFormat(i18n.language).format(n);
  const facets = snapshot.data?.facets;
  const total = snapshot.data?.total;
  const published = snapshot.data ? facetCount(facets, 'status', 'published') : undefined;
  const o = overview.data;
  const noAnalytics = isForbidden(overview.error);

  /** "3 overdue · SLA 92% on time": the overdue count is always said (or "Nothing waiting for you"); the tenant-wide SLA
   * figure only exists for people allowed to see it. */
  const taskHint = (s: TaskSummary | undefined) => {
    if (!s) return t('home.stats.tasksFailed');
    const parts = [
      s.overdueForMe > 0
        ? t('home.stats.tasksOverdue', { count: s.overdueForMe })
        : t(s.pendingForMe === 0 ? 'home.stats.tasksHintNone' : 'home.stats.tasksNoneOverdue'),
    ];
    if (s.slaAttainmentPercent !== null) parts.push(t('home.stats.tasksSla', { percent: Math.round(s.slaAttainmentPercent) }));
    return parts.join(' · ');
  };

  const cards: ReactNode[] = [];
  const weeks = o?.uploads.map((w) => w.count) ?? [];
  const recent = weeks.slice(-4).reduce((a, b) => a + b, 0);
  const earlier = weeks.slice(-8, -4).reduce((a, b) => a + b, 0);
  cards.push(
    snapshot.isPending ? (
      <KpiSkeleton key="total" />
    ) : (
      <Link key="total" to="/library" className="dam-home__statlink">
        <StatCard
          label={t('home.stats.total')}
          value={typeof total === 'number' ? fmt(total) : '–'}
          hint={typeof published === 'number' ? t('home.stats.totalPublished', { count: published }) : t('home.stats.totalHint')}
          icon={<Stack size={20} weight="duotone" />}
          tone="brand"
          trend={
            o ? (
              <TrendBadge change={percentChange(recent, earlier)} suffix={t('analytics.insights.vsPrevious', { days: 28 })} />
            ) : undefined
          }
          chart={weeks.length > 1 ? <Sparkline values={weeks} /> : undefined}
        />
      </Link>
    ),
    tasks.isPending ? (
      <KpiSkeleton key="tasks" />
    ) : (
      <Link
        key="tasks"
        to="/tasks"
        className="dam-home__statlink"
        title={
          tasks.data?.slaAttainmentPercent != null
            ? t('home.stats.tasksSlaTitle', { percent: Math.round(tasks.data.slaAttainmentPercent) })
            : undefined
        }
      >
        <StatCard
          label={t('home.stats.tasks')}
          value={tasks.data ? fmt(tasks.data.pendingForMe) : '–'}
          hint={taskHint(tasks.data)}
          icon={<CheckSquareOffset size={20} weight="duotone" />}
          tone={tasks.data && tasks.data.overdueForMe > 0 ? 'danger' : 'brand'}
        />
      </Link>
    ),
  );

  if (noAnalytics) {
    cards.push(
      <Link key="review" to="/library?status=in_review" className="dam-home__statlink">
        <StatCard
          label={t('home.stats.review')}
          value={fmt(facetCount(facets, 'status', 'in_review'))}
          hint={t('home.stats.reviewHint')}
          icon={<Clock size={20} weight="duotone" />}
          tone="info"
        />
      </Link>,
      <Link key="drafts" to="/library?status=draft" className="dam-home__statlink">
        <StatCard
          label={t('home.stats.drafts')}
          value={fmt(facetCount(facets, 'status', 'draft'))}
          hint={t('home.stats.draftsHint')}
          icon={<NotePencil size={20} weight="duotone" />}
        />
      </Link>,
    );
  } else if (!o) {
    cards.push(<KpiSkeleton key="usage" />, <KpiSkeleton key="storage" />);
  } else {
    const files = o.storage.byType.reduce((n, x) => n + x.count, 0);
    cards.push(
      <StatCard
        key="usage"
        label={t('analytics.insights.kpiUsage', { days: o.periodDays })}
        value={fmt(o.usage.total)}
        hint={t('analytics.insights.kpiUsageHint')}
        icon={<Lightning size={20} weight="duotone" />}
        tone="warning"
        trend={
          <TrendBadge
            change={percentChange(o.usage.total, o.usage.previousTotal)}
            suffix={t('analytics.insights.vsPrevious', { days: o.periodDays })}
          />
        }
        chart={<Sparkline values={o.usage.byDay.map((d) => d.count)} />}
      />,
      <StatCard
        key="storage"
        label={t('analytics.insights.kpiStorage')}
        value={formatBytes(o.storage.totalBytes)}
        hint={t('analytics.insights.kpiStorageHint', { count: files })}
        icon={<HardDrives size={20} weight="duotone" />}
        tone="info"
      />,
    );
  }

  return (
    <ul className="dam-home__stats" aria-label={t('home.stats.label')}>
      {cards.map((c, i) => (
        <li key={i}>{c}</li>
      ))}
    </ul>
  );
}

function CardHead({ id, title, hint }: { id: string; title: string; hint?: string }) {
  return (
    <div className="dam-insight__head">
      <h2 id={id} className="dam-home__h2">
        {title}
      </h2>
      {hint && <p className="dam-insight__hint">{hint}</p>}
    </div>
  );
}

function usageSeries(o: AnalyticsOverview, locale: string) {
  const day = new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short' });
  return o.usage.byDay.map((d) => {
    const label = day.format(new Date(`${d.date}T00:00:00`));
    return { label, value: d.count, title: `${label}: ${d.count}` };
  });
}

export function UsageCard() {
  const { t, i18n } = useTranslation();
  const query = useOverview(USAGE_DAYS);
  if (isForbidden(query.error)) return null;
  const o = query.data;
  const empty = o && o.usage.total === 0;
  return (
    <Card as="section" className="dam-insight dam-home__usage" aria-labelledby="insight-usage">
      <CardHead id="insight-usage" title={t('analytics.insights.usageTitle')} hint={t('analytics.insights.usageDesc')} />
      <div className="dam-insight__body dam-insight__body--chart">
        {query.error ? (
          <SectionNote onRetry={() => void query.refetch()} />
        ) : !o ? (
          <Skeleton width="100%" height={176} />
        ) : empty ? (
          <EmptyState
            titleAs="p"
            icon={<Lightning />}
            title={t('analytics.insights.usageEmpty')}
            description={t('analytics.insights.usageEmptyHint')}
          />
        ) : (
          <>
            <BarChart
              data={usageSeries(o, i18n.language)}
              label={t('analytics.insights.usageChart', { days: o.periodDays })}
              fill
            />
            {o.usage.byChannel.length > 0 && (
              <DistributionBar
                compact
                label={t('analytics.insights.usageTitle')}
                segments={o.usage.byChannel.map((c, i) => ({ label: c.channel, value: c.count, series: i }))}
              />
            )}
          </>
        )}
      </div>
    </Card>
  );
}

export function HealthCard() {
  const { t } = useTranslation();
  const snapshot = useLibrarySnapshot();
  const facets = snapshot.data?.facets;
  const statusSegments: DistributionSegment[] = STATUS_ORDER.map((s) => ({
    label: t(`library.status.${s.value}`),
    value: facetCount(facets, 'status', s.value),
    series: s.series,
  }));
  const typeValues = facets?.find((f) => f.field === 'type')?.values ?? [];
  const types = [...TYPE_ORDER, ...typeValues.map((v) => v.value).filter((v) => !TYPE_ORDER.includes(v))];
  const typeSegments: DistributionSegment[] = types.map((x, i) => ({
    label: t(`library.type.${x}`, { defaultValue: x }),
    value: facetCount(facets, 'type', x),
    series: i,
  }));
  return (
    <Card as="section" className="dam-insight dam-home__health" aria-labelledby="insight-health">
      <CardHead id="insight-health" title={t('analytics.insights.healthTitle')} />
      <div className="dam-insight__body">
        {snapshot.isPending ? (
          <Skeleton width="100%" height={176} />
        ) : snapshot.error ? (
          <SectionNote onRetry={() => void snapshot.refetch()} />
        ) : (
          <div className="dam-insight__stack">
            <div>
              <h3 className="dam-insight__sub">{t('analytics.insights.byStatus')}</h3>
              <DistributionBar compact label={t('analytics.insights.byStatus')} segments={statusSegments} />
            </div>
            <div>
              <h3 className="dam-insight__sub">{t('analytics.insights.byType')}</h3>
              <DistributionBar
                compact
                label={t('analytics.insights.byType')}
                segments={typeSegments.filter((s) => s.value > 0)}
              />
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}

function TopUsedList() {
  const { t } = useTranslation();
  const query = useOverview(USAGE_DAYS);
  const top = query.data?.topAssets ?? [];
  if (!query.data) return <Skeleton width="100%" height={160} />;
  if (top.length === 0)
    return (
      <EmptyState
        titleAs="p"
        icon={<Lightning />}
        title={t('analytics.insights.topEmpty')}
        description={t('analytics.insights.topEmptyBody')}
      />
    );
  return (
    <ol className="dam-toplist" aria-label={t('analytics.insights.topTitle')}>
      {top.map((a, i) => (
        <li key={a.assetId}>
          <Link to={`/library/assets/${a.assetId}`} className="dam-toplist__row">
            <span className="dam-toplist__rank" aria-hidden="true">
              {i + 1}
            </span>
            <span className="dam-toplist__icon" aria-hidden="true">
              <FileTypeIcon type={a.type} size={18} />
            </span>
            <span className="dam-toplist__title">{a.title}</span>
            <span className="dam-toplist__count">{t('analytics.insights.uses', { count: a.count })}</span>
          </Link>
        </li>
      ))}
    </ol>
  );
}

function ReviewQueue({ locale }: { locale: string }) {
  const { t } = useTranslation();
  const query = useAssets({ status: 'in_review', limit: REVIEW_COUNT });
  const assets = query.data?.items.slice(0, REVIEW_COUNT) ?? [];
  const thumbs = useThumbnailLinks(thumbnailIds(assets));
  if (query.isPending) return <ListSkeleton />;
  if (query.error) return <SectionNote onRetry={() => void query.refetch()} />;
  if (assets.length === 0)
    return (
      <SectionEmpty icon={<PaperPlaneTilt />} title={t('home.review.emptyTitle')} description={t('home.review.emptyBody')} />
    );
  return <ReviewList assets={assets} thumbs={thumbs.data} locale={locale} />;
}

/** One card, two lists: what is most used and what is waiting for a decision. A role without analytics sees only the
 * review queue. */
export function ActivityCard({ locale }: { locale: string }) {
  const { t } = useTranslation();
  const overview = useOverview(USAGE_DAYS);
  const snapshot = useLibrarySnapshot();
  const analytics = !isForbidden(overview.error);
  const [tab, setTab] = useState<'top' | 'review'>('top');
  const active = analytics ? tab : 'review';
  const waiting = snapshot.data ? facetCount(snapshot.data.facets, 'status', 'in_review') : null;
  return (
    <Card as="section" className="dam-insight dam-home__activity" aria-labelledby="insight-activity">
      <div className="dam-insight__head">
        <h2 id="insight-activity" className="dam-home__h2">
          {active === 'top' ? t('analytics.insights.topTitle') : t('home.review.title')}
        </h2>
        {analytics ? (
          <SegmentedControl
            label={t('analytics.insights.activityView')}
            value={active}
            onChange={(v) => setTab(v as 'top' | 'review')}
            options={[
              { value: 'top', label: t('analytics.insights.topTitle') },
              {
                value: 'review',
                label: waiting
                  ? t('analytics.insights.needsReviewCount', { count: waiting })
                  : t('analytics.insights.needsReview'),
              },
            ]}
          />
        ) : (
          <Link to="/library?status=in_review" className="dam-home__more">
            {t('home.review.viewAll')}
          </Link>
        )}
      </div>
      <div className="dam-insight__body">{active === 'top' ? <TopUsedList /> : <ReviewQueue locale={locale} />}</div>
    </Card>
  );
}

/** The dashboard's "not used" card; omitted entirely for roles without analytics access. */
export function UnusedCard() {
  const overview = useOverview(USAGE_DAYS);
  if (isForbidden(overview.error)) return null;
  return (
    <Card as="div" className="dam-insight dam-home__unused">
      <UnusedAssetsPanel compact />
    </Card>
  );
}
