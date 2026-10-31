import { ListMagnifyingGlass, Lock, WarningCircle } from '@phosphor-icons/react';
import { useCallback, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Card, EmptyState, PageHeader, useToast } from '../../ui';
import { isForbidden } from '../analytics/analyticsApi';
import { FilterChips, type FilterChip } from '../library/FilterChips';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { AuditDrawer } from './AuditDrawer';
import { AuditFiltersForm } from './AuditFiltersForm';
import { AuditResults } from './AuditResults';
import { ExportMenu } from './ExportMenu';
import { IntegrityCard } from './IntegrityCard';
import { useAuditActions, useAuditEntries, useFetchEntryByChainNo, type AuditEntry } from './auditApi';
import { hasFilters } from './auditFilters';
import { shortId } from './auditFormat';
import { useAuditFilters } from './useAuditFilters';
import './audit.css';
import { useSettingsEyebrow } from '../settings/useSettingsEyebrow';

/** `/settings/audit` - the tamper-evident audit log: integrity check, filters, results, per-entry detail and export. */
export function AuditPage() {
  const { t, i18n } = useTranslation();
  useDocumentTitle(t('audit.title'));
  const eyebrow = useSettingsEyebrow();
  const { push } = useToast();
  const { filters, update, clear } = useAuditFilters();
  const list = useAuditEntries(filters);
  const actionsQuery = useAuditActions();
  const fetchByChainNo = useFetchEntryByChainNo();
  const [selected, setSelected] = useState<AuditEntry | null>(null);

  const entries = useMemo(() => list.data?.pages.flatMap((p) => p.items) ?? [], [list.data]);
  const actors = useMemo(() => {
    const m = new Map<string, string>();
    for (const e of entries) if (e.actorName) m.set(e.actorUserId ?? e.actorId, e.actorName);
    return m;
  }, [entries]);

  const opened = useRef<AuditEntry | null>(null);
  const open = useCallback((entry: AuditEntry) => {
    opened.current = entry;
    setSelected(entry);
  }, []);
  const closeDrawer = useCallback(() => {
    const was = opened.current;
    opened.current = null;
    setSelected(null);
    // A native dialog hands focus back to whatever had it, but a row click may not have focused anything: aim for the row's button.
    if (was) requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-entry-id="${CSS.escape(was.id)}"]`)?.focus());
  }, []);

  const filterActor = (e: AuditEntry) => {
    update({ actor: e.actorUserId ?? e.actorId });
    opened.current = null;
    setSelected(null);
  };

  const showEntry = async (chainNo: number) => {
    try {
      const entry = await fetchByChainNo(chainNo);
      if (entry) open(entry);
      else push({ title: t('audit.verify.entryMissing', { chainNo }), tone: 'error' });
    } catch {
      push({ title: t('audit.verify.entryFailed'), tone: 'error' });
    }
  };

  const fmtDate = (iso: string) => new Date(iso).toLocaleString(i18n.language, { dateStyle: 'medium', timeStyle: 'short' });
  const chips: FilterChip[] = [];
  const chip = (key: keyof typeof filters, group: string, label: string) =>
    chips.push({ id: key, group, label, onRemove: () => update({ [key]: undefined }) });
  if (filters.from) chip('from', t('audit.filters.from'), fmtDate(filters.from));
  if (filters.to) chip('to', t('audit.filters.to'), fmtDate(filters.to));
  if (filters.actor) chip('actor', t('audit.filters.actor'), actors.get(filters.actor) ?? shortId(filters.actor));
  if (filters.action) chip('action', t('audit.filters.action'), filters.action);
  if (filters.entityType) chip('entityType', t('audit.filters.entityType'), filters.entityType);
  if (filters.entityId) chip('entityId', t('audit.filters.entityId'), shortId(filters.entityId));
  if (filters.q) chip('q', t('audit.filters.q'), filters.q);

  const header = <PageHeader title={t('audit.title')} eyebrow={eyebrow} description={t('audit.desc')} />;

  if (list.error && isForbidden(list.error)) {
    return (
      <div className="dam-page dam-audit">
        {header}
        <EmptyState icon={<Lock />} title={t('audit.noAccessTitle')} description={t('audit.noAccessBody')} />
      </div>
    );
  }

  const filtered = hasFilters(filters);
  const initialError = list.isError && !list.data;
  const empty = !list.isPending && !initialError && entries.length === 0;

  return (
    <div className="dam-page dam-audit">
      {header}

      <IntegrityCard filters={filters} onShowEntry={(n) => void showEntry(n)} />

      <Card className="dam-audit__filters">
        <AuditFiltersForm filters={filters} update={update} actions={actionsQuery.data ?? []} actors={actors} />
        <FilterChips chips={chips} onClear={clear} />
      </Card>

      <section className="dam-audit__results" aria-labelledby="audit-results-title">
        <div className="dam-audit__bar">
          <div>
            <h2 id="audit-results-title" className="dam-audit__h">
              {t('audit.results')}
            </h2>
            <p className="dam-audit__count" aria-live="polite">
              {list.isPending
                ? t('audit.loading')
                : initialError
                  ? ''
                  : `${t('audit.showing', { count: entries.length })}${list.hasNextPage ? ` ${t('audit.moreAvailable')}` : ''}`}
            </p>
          </div>
          <ExportMenu filters={filters} disabled={list.isPending || initialError || empty} />
        </div>
        <p className="dam-audit__exporthint">{t('audit.export.hint')}</p>

        {initialError ? (
          <div className="dam-audit__error" role="alert">
            <WarningCircle size={22} weight="fill" aria-hidden="true" />
            <div>
              <strong>{t('audit.error.title')}</strong>
              <p>{t('audit.error.body')}</p>
            </div>
            <Button variant="secondary" size="sm" loading={list.isFetching} onClick={() => void list.refetch()}>
              {t('audit.retry')}
            </Button>
          </div>
        ) : empty ? (
          <EmptyState
            titleAs="p"
            icon={<ListMagnifyingGlass />}
            title={filtered ? t('audit.empty.filteredTitle') : t('audit.empty.title')}
            description={filtered ? t('audit.empty.filteredBody') : t('audit.empty.body')}
            action={
              filtered ? (
                <Button variant="secondary" onClick={clear}>
                  {t('library.clearFilters')}
                </Button>
              ) : undefined
            }
          />
        ) : (
          <AuditResults entries={entries} loading={list.isPending} onOpen={open} onFilterActor={filterActor} />
        )}

        {list.isFetchNextPageError && (
          <p className="dam-audit__inlineerror" role="alert">
            {t('audit.error.more')}
          </p>
        )}
        {list.hasNextPage && (
          <div className="dam-audit__loadmore">
            <Button variant="secondary" loading={list.isFetchingNextPage} onClick={() => void list.fetchNextPage()}>
              {list.isFetchNextPageError ? t('audit.retry') : t('audit.loadMore')}
            </Button>
          </div>
        )}
      </section>

      <AuditDrawer entry={selected} onClose={closeDrawer} onFilterActor={filterActor} />
    </div>
  );
}
