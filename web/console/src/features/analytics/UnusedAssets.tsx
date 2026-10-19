import { Archive, CaretRight, CheckCircle, DownloadSimple } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Badge, Button, EmptyState, FileTypeIcon, Modal, Skeleton, Table, useToast, type Column } from '@dam/ui-kit';
import { useApiClient } from '../../api/apiClient';
import { formatBytes } from '../../lib/format';
import { relativeTime } from '../../lib/relativeTime';
import { useThumbnailLinks } from '../asset/assetApi';
import { statusTone } from '../library/statusTone';
import { useRetireAssets, useUnusedAssets, type UnusedAsset } from './analyticsApi';
import { toCsv, downloadCsv } from './csv';
import { fetchAllUnused } from './fetchAll';
import { PeriodPicker, usePeriodLabel } from './PeriodPicker';
import { encodePeriod, periodDays } from './period';
import { usePeriod } from './usePeriod';
import './analytics.css';

const TYPES = ['image', 'video', 'document', 'audio', 'other'] as const;

/** The "not used" tracker. `compact` is the dashboard card (a handful of rows and a link on); the full variant adds the
 * type filter, paging and the whole list. Both share the period (URL + remembered), the selection and the actions. */
export function UnusedAssetsPanel({ compact = false, titleAs: Title = 'h2' }: { compact?: boolean; titleAs?: 'h2' | 'h3' }) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const api = useApiClient();
  const [period, setPeriod] = usePeriod();
  const label = usePeriodLabel();
  const [status, setStatus] = useState<'published' | 'all'>('published');
  const [type, setType] = useState('');
  const [selected, setSelected] = useState<string[]>([]);
  const [confirm, setConfirm] = useState(false);
  const [exporting, setExporting] = useState(false);
  const retire = useRetireAssets();

  const days = periodDays(period);
  const filter = { days, status, type: type || undefined };
  const query = useUnusedAssets(filter, compact ? 6 : 50);
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];
  const first = query.data?.pages[0];
  const thumbs = useThumbnailLinks(items.map((a) => a.id));
  const fmt = (n: number) => new Intl.NumberFormat(i18n.language).format(n);
  const total = first?.total ?? 0;
  const totalText = first && !first.totalIsExact ? `${fmt(total)}+` : fmt(total);
  const retirable = items.filter((a) => selected.includes(a.id));

  const toggle = (id: string, on: boolean) => setSelected((s) => (on ? [...new Set([...s, id])] : s.filter((x) => x !== id)));
  const changeFilter = (fn: () => void) => {
    fn();
    setSelected([]);
  };

  const exportCsv = async () => {
    setExporting(true);
    try {
      const { items: all, capped } = await fetchAllUnused(api, filter);
      const rows = [
        [
          t('analytics.unused.colAsset'),
          t('analytics.unused.colType'),
          t('analytics.unused.colStatus'),
          t('analytics.unused.colFolder'),
          t('analytics.unused.colCreated'),
          t('analytics.unused.colLastUsed'),
          t('analytics.unused.colUses'),
          t('analytics.unused.colIdleDays'),
          t('analytics.unused.colSize'),
          'ID',
        ],
        ...all.map((a) => [
          a.title,
          a.type,
          a.status,
          a.folderPath,
          a.createdAt,
          a.lastUsedAt,
          a.useCount,
          a.idleDays,
          a.sizeBytes,
          a.id,
        ]),
      ];
      downloadCsv(`unused-assets-${new Date().toISOString().slice(0, 10)}.csv`, toCsv(rows));
      push({
        title: t('analytics.unused.exported', { count: all.length }),
        message: capped ? t('analytics.unused.exportCapped') : undefined,
        tone: 'success',
      });
    } catch {
      push({ title: t('analytics.unused.exportFailed'), tone: 'error' });
    } finally {
      setExporting(false);
    }
  };

  const doRetire = () =>
    retire.mutate(retirable, {
      onSuccess: (r) => {
        setConfirm(false);
        setSelected(r.failed.map((f) => f.id));
        if (r.failed.length === 0)
          push({ title: t('analytics.unused.archived', { count: r.succeeded.length }), tone: 'success' });
        else
          push({
            title: t('analytics.unused.archivedSome', { done: r.succeeded.length, failed: r.failed.length }),
            message: r.failed[0]?.error,
            tone: 'error',
          });
      },
      onError: () => {
        setConfirm(false);
        push({ title: t('analytics.unused.archiveFailed'), tone: 'error' });
      },
    });

  const columns: Column<UnusedAsset>[] = [
    {
      key: 'title',
      header: t('analytics.unused.colAsset'),
      render: (a) => (
        <div className="dam-assetcell">
          {thumbs.data?.[a.id] ? (
            <img className="dam-table__thumb" src={thumbs.data[a.id]!} alt="" loading="lazy" />
          ) : (
            <span className="dam-table__thumb dam-assetcell__fallback" aria-hidden="true">
              <FileTypeIcon type={a.type} size={18} />
            </span>
          )}
          <span className="dam-unused__titlecell">
            <Link to={`/library/assets/${a.id}`} className="dam-assetcell__title">
              {a.title}
            </Link>
            <span className="dam-unused__sub">{[t(`library.type.${a.type}`), a.folderPath].filter(Boolean).join(' · ')}</span>
          </span>
        </div>
      ),
    },
    {
      key: 'status',
      header: t('analytics.unused.colStatus'),
      className: 'dam-col--hide-sm',
      render: (a) => (
        <Badge tone={statusTone(a.status)} size="sm">
          {t(`library.status.${a.status}`)}
        </Badge>
      ),
    },
    {
      key: 'lastUsed',
      header: t('analytics.unused.colLastUsed'),
      render: (a) =>
        a.lastUsedAt ? (
          <time dateTime={a.lastUsedAt} title={new Date(a.lastUsedAt).toLocaleString(i18n.language)}>
            {relativeTime(a.lastUsedAt, i18n.language)}
          </time>
        ) : (
          <Badge
            tone="warning"
            size="sm"
            title={t('analytics.unused.neverHint', { date: new Date(a.createdAt).toLocaleDateString(i18n.language) })}
          >
            {t('analytics.unused.never')}
          </Badge>
        ),
    },
    {
      key: 'idle',
      header: t('analytics.unused.colIdle'),
      render: (a) => <span className="dam-unused__idle">{t('analytics.unused.idleDays', { count: a.idleDays })}</span>,
    },
    {
      key: 'size',
      header: t('analytics.unused.colSize'),
      className: 'dam-col--hide-sm dam-unused__num',
      render: (a) => (a.sizeBytes === null ? '–' : formatBytes(a.sizeBytes)),
    },
  ];

  return (
    <section className={`dam-unused${compact ? ' dam-unused--compact' : ''}`} aria-labelledby="unused-title">
      <div className="dam-unused__head">
        <div className="dam-unused__titles">
          <Title id="unused-title" className="dam-unused__title">
            {t('analytics.unused.title')}
          </Title>
          <p className="dam-unused__desc">{t('analytics.unused.desc')}</p>
        </div>
        <PeriodPicker value={period} onChange={(p) => changeFilter(() => setPeriod(p))} />
      </div>

      <div className="dam-unused__bar">
        <p className="dam-unused__summary" aria-live="polite">
          {query.isPending ? (
            <Skeleton width={260} height={20} />
          ) : query.error ? (
            t('analytics.unused.error')
          ) : (
            <>
              <strong className="dam-unused__count">{totalText}</strong>{' '}
              {t(status === 'all' ? 'analytics.unused.summaryAll' : 'analytics.unused.summaryPublished', {
                count: total,
                period: label(period),
              })}
            </>
          )}
        </p>
        <div className="dam-unused__filters">
          <label className="dam-unused__filter">
            <span className="dam-sr-only">{t('analytics.unused.filterStatus')}</span>
            <select
              className="dam-select"
              value={status}
              onChange={(e) => changeFilter(() => setStatus(e.target.value as 'published' | 'all'))}
            >
              <option value="published">{t('analytics.unused.statusPublished')}</option>
              <option value="all">{t('analytics.unused.statusAll')}</option>
            </select>
          </label>
          {!compact && (
            <label className="dam-unused__filter">
              <span className="dam-sr-only">{t('analytics.unused.filterType')}</span>
              <select className="dam-select" value={type} onChange={(e) => changeFilter(() => setType(e.target.value))}>
                <option value="">{t('analytics.unused.allTypes')}</option>
                {TYPES.map((x) => (
                  <option key={x} value={x}>
                    {t(`library.type.${x}`)}
                  </option>
                ))}
              </select>
            </label>
          )}
          <Button variant="secondary" size="sm" loading={exporting} disabled={total === 0} onClick={() => void exportCsv()}>
            <DownloadSimple size={16} weight="bold" aria-hidden="true" />
            {t('analytics.unused.export')}
          </Button>
        </div>
      </div>

      {selected.length > 0 && (
        <div className="dam-unused__selection" role="region" aria-label={t('analytics.unused.selectionLabel')}>
          <span>{t('analytics.unused.selected', { count: selected.length })}</span>
          <div className="dam-unused__selectionactions">
            <Button variant="ghost" size="sm" onClick={() => setSelected([])}>
              {t('analytics.unused.clear')}
            </Button>
            <Button variant="secondary" size="sm" onClick={() => setConfirm(true)}>
              <Archive size={16} weight="bold" aria-hidden="true" />
              {t('analytics.unused.archive')}
            </Button>
          </div>
        </div>
      )}

      {!query.isPending && !query.error && items.length === 0 ? (
        <EmptyState
          titleAs="p"
          icon={<CheckCircle />}
          title={t('analytics.unused.emptyTitle', { period: label(period) })}
          description={t('analytics.unused.emptyBody')}
        />
      ) : (
        <Table
          columns={columns}
          rows={items}
          getRowId={(a) => a.id}
          caption={t('analytics.unused.caption', { period: label(period) })}
          loading={query.isPending}
          selectedIds={selected}
          onToggleSelect={toggle}
          selectLabel={(a) => t('analytics.unused.select', { title: a.title })}
        />
      )}

      {compact
        ? total > items.length && (
            <Link to={`/insights/unused?unused=${encodePeriod(period)}`} className="dam-unused__more">
              {t('analytics.unused.viewAll', { count: total })}
              <CaretRight size={14} weight="bold" aria-hidden="true" />
            </Link>
          )
        : query.hasNextPage && (
            <div className="dam-unused__loadmore">
              <Button variant="secondary" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
                {t('analytics.unused.loadMore')}
              </Button>
            </div>
          )}

      <p className="dam-unused__foot">{t('analytics.unused.foot')}</p>

      <Modal
        open={confirm}
        title={t('analytics.unused.confirmTitle', { count: retirable.length })}
        onClose={() => setConfirm(false)}
      >
        <div className="dam-stack dam-dialogform">
          <p>{t('analytics.unused.confirmBody', { count: retirable.length })}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setConfirm(false)}>
              {t('analytics.unused.cancel')}
            </Button>
            <Button variant="danger" loading={retire.isPending} onClick={doRetire}>
              {t('analytics.unused.confirmAction', { count: retirable.length })}
            </Button>
          </div>
        </div>
      </Modal>
    </section>
  );
}
