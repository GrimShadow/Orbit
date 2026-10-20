import { ChartBar, Warning } from '@phosphor-icons/react';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BarChart, Button, Card, DistributionBar, EmptyState, SegmentedControl, Skeleton } from '@dam/ui-kit';
import { useMe } from '../../api/me';
import { relativeTime } from '../../lib/relativeTime';
import { isForbidden } from '../analytics/analyticsApi';
import { useAssetAnalytics } from './insightsApi';
import type { AssetAnalyticsDto } from './insightsTypes';
import { formatCount } from './kpiFormat';
import { useNearViewport } from './useNearViewport';
import './insights.css';

const PERIODS = [30, 90] as const;
const RECENT_SHOWN = 5;

/** The busiest day of a zero-filled series, or `null` when nothing happened. */
export function busiestDay(byDay: AssetAnalyticsDto['byDay']) {
  return byDay.reduce<AssetAnalyticsDto['byDay'][number] | null>((best, d) => (d.count > (best?.count ?? 0) ? d : best), null);
}

/** How this asset has been used - on the asset page's Details tab, for people with `analytics.read`. It waits until it is
 * near the screen (and the asset itself has loaded, since it only mounts then), so the page never queues behind it. */
export function AssetUsageCard({ assetId }: { assetId: string }) {
  const me = useMe();
  if (!me.data?.permissions.includes('analytics.read')) return null;
  return <Loaded assetId={assetId} />;
}

function Loaded({ assetId }: { assetId: string }) {
  const { t, i18n } = useTranslation();
  const ref = useRef<HTMLDivElement>(null);
  const near = useNearViewport(ref);
  const [days, setDays] = useState<number>(30);
  const query = useAssetAnalytics(assetId, days, near);
  if (isForbidden(query.error)) return null;
  const a = query.data;
  const fmt = (n: number) => formatCount(n, i18n.language);
  const date = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium' });
  const day = new Intl.DateTimeFormat(i18n.language, { day: 'numeric', month: 'short', timeZone: 'UTC' });
  const dayLabel = (iso: string) => day.format(new Date(`${iso.slice(0, 10)}T00:00:00Z`));
  const typeLabel = (type: string) => t(`insights.asset.type.${type}`, { defaultValue: type });
  const channelLabel = (c: string | null) => (c && c !== 'unspecified' ? c : t('insights.asset.noChannel'));

  let body;
  if (query.error) {
    body = (
      <div role="alert" className="dam-usage__note">
        <Warning size={16} weight="bold" aria-hidden="true" />
        <span>{t('insights.asset.error')}</span>
        <Button size="sm" variant="ghost" onClick={() => void query.refetch()}>
          {t('insights.retry')}
        </Button>
      </div>
    );
  } else if (!a) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('insights.asset.loading')}</span>
        <div className="dam-stack" aria-hidden="true">
          <Skeleton height={28} width="40%" />
          <Skeleton height={96} />
        </div>
      </div>
    );
  } else if (a.total === 0) {
    body = (
      <EmptyState
        titleAs="p"
        icon={<ChartBar />}
        title={a.allTimeTotal > 0 ? t('insights.asset.notUsedInPeriod', { days }) : t('insights.asset.notUsedYet')}
        description={
          a.allTimeTotal > 0 && a.lastUsedAt
            ? t('insights.asset.lastUsedOn', { date: date.format(new Date(a.lastUsedAt)) })
            : t('insights.asset.notUsedYetBody')
        }
      />
    );
  } else {
    const busiest = busiestDay(a.byDay);
    const chartLabel = t('insights.asset.chartLabel', {
      days,
      total: fmt(a.total),
      busiest: busiest ? t('insights.asset.busiest', { date: dayLabel(busiest.date), count: busiest.count }) : '',
    });
    body = (
      <div className="dam-stack">
        <p className="dam-usage__total" aria-live="polite">
          <strong>{fmt(a.total)}</strong> {t('insights.asset.usesInPeriod', { count: a.total, days })}
        </p>
        <BarChart
          height={96}
          label={chartLabel}
          data={a.byDay.map((d) => ({
            label: dayLabel(d.date),
            value: d.count,
            title: `${dayLabel(d.date)}: ${t('insights.asset.uses', { count: d.count })}`,
          }))}
        />
        <dl className="dam-facts">
          <div className="dam-facts__row">
            <dt>{t('insights.asset.firstUsed')}</dt>
            <dd>{a.firstUsedAt ? date.format(new Date(a.firstUsedAt)) : t('insights.asset.never')}</dd>
          </div>
          <div className="dam-facts__row">
            <dt>{t('insights.asset.lastUsed')}</dt>
            <dd>{a.lastUsedAt ? date.format(new Date(a.lastUsedAt)) : t('insights.asset.never')}</dd>
          </div>
        </dl>
        {a.byType.length > 0 && (
          <div>
            <h3 className="dam-usage__sub">{t('insights.asset.byType')}</h3>
            <DistributionBar
              compact
              label={t('insights.asset.byType')}
              segments={a.byType.map((x, i) => ({ label: typeLabel(x.eventType), value: x.count, series: i }))}
            />
          </div>
        )}
        {a.byChannel.length > 0 && (
          <div>
            <h3 className="dam-usage__sub">{t('insights.asset.byChannel')}</h3>
            <DistributionBar
              compact
              label={t('insights.asset.byChannel')}
              segments={a.byChannel.map((x, i) => ({ label: channelLabel(x.channel), value: x.count, series: i }))}
            />
          </div>
        )}
        {a.recent.length > 0 && (
          <div>
            <h3 className="dam-usage__sub">{t('insights.asset.recent')}</h3>
            <ul className="dam-usage__recent" aria-label={t('insights.asset.recent')}>
              {a.recent.slice(0, RECENT_SHOWN).map((e) => (
                <li key={e.id}>
                  <span className="dam-usage__etype">{typeLabel(e.eventType)}</span>
                  <span className="dam-usage__echannel">{channelLabel(e.channel)}</span>
                  <time
                    className="dam-usage__etime"
                    dateTime={e.occurredAt}
                    title={new Date(e.occurredAt).toLocaleString(i18n.language)}
                  >
                    {relativeTime(e.occurredAt, i18n.language)}
                  </time>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    );
  }

  return (
    <Card as="section" padding="md" aria-labelledby="dam-asset-usage" className="dam-usage">
      <div className="dam-usage__head">
        <h2 id="dam-asset-usage" className="dam-metasection__title">
          {t('insights.asset.title')}
        </h2>
        <SegmentedControl
          label={t('insights.asset.period')}
          value={String(days)}
          onChange={(v) => setDays(Number(v))}
          options={PERIODS.map((d) => ({ value: String(d), label: t('insights.period.days', { count: d }) }))}
        />
      </div>
      <div ref={ref} aria-busy={query.isFetching}>
        {body}
      </div>
    </Card>
  );
}
