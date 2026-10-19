import { ArrowClockwise, ListMagnifyingGlass, WarningCircle } from '@phosphor-icons/react';
import { useMemo, type MouseEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { Badge, Button, EmptyState, Input, Select, Table, type Column } from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import { DeliveryDrawer } from './DeliveryDrawer';
import { prefixWildcards } from './EventPicker';
import { RetryText, StatusBadge } from './DeliveryStatus';
import { deliveryState, useNow } from './format';
import { isForbidden, useDeliveries, useWebhookEvents, useWebhooks, type DeliveryDto, type DeliveryFilter } from './webhooksApi';

/** The URL parameters this tab owns. */
const PARAMS = ['webhook', 'status', 'event', 'from', 'to'] as const;

/** A local calendar day as an instant: its first millisecond (`from`) or its last (`to`). */
function dayBoundary(day: string, end: boolean): string | undefined {
  const d = new Date(`${day}T${end ? '23:59:59.999' : '00:00:00'}`);
  return Number.isNaN(d.getTime()) ? undefined : d.toISOString();
}

export function DeliveriesTab() {
  const { t, i18n } = useTranslation();
  const [params, setParams] = useSearchParams();
  const now = useNow(15_000);
  const webhook = params.get('webhook') ?? '';
  const status = params.get('status') ?? '';
  const event = params.get('event') ?? '';
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';
  const openId = params.get('delivery');

  const filter: DeliveryFilter = useMemo(
    () => ({
      webhookId: webhook || undefined,
      status: status || undefined,
      eventType: event || undefined,
      from: from ? dayBoundary(from, false) : undefined,
      to: to ? dayBoundary(to, true) : undefined,
    }),
    [webhook, status, event, from, to],
  );
  const query = useDeliveries(filter);
  const hooks = useWebhooks();
  const catalogue = useWebhookEvents();
  const rows = useMemo(() => query.data?.pages.flatMap((p) => p.items) ?? [], [query.data]);
  const anyPending = rows.some((r) => r.status === 'pending');
  const filtered = PARAMS.some((k) => params.get(k));

  const setParam = (key: string, value: string) =>
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        if (value) next.set(key, value);
        else next.delete(key);
        next.delete('delivery');
        return next;
      },
      { replace: true },
    );
  const clear = () =>
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        for (const k of PARAMS) next.delete(k);
        next.delete('delivery');
        return next;
      },
      { replace: true },
    );
  const open = (id: string | null) =>
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        if (id) next.set('delivery', id);
        else next.delete('delivery');
        return next;
      },
      { replace: id === null },
    );

  const eventOptions = useMemo(() => {
    const events = catalogue.data ?? [];
    const opts = [
      { value: '', label: t('webhooks.deliveries.allEvents') },
      ...prefixWildcards(events).map((w) => ({
        value: w.pattern,
        label: t('webhooks.deliveries.allOfCategory', { pattern: w.pattern }),
      })),
      ...events.map((e) => ({ value: e.name, label: e.name })),
    ];
    // A pattern from a shared link that the catalogue does not list still shows as the current choice.
    return event && !opts.some((o) => o.value === event) ? [...opts, { value: event, label: event }] : opts;
  }, [catalogue.data, event, t]);

  const webhookOptions = [
    { value: '', label: t('webhooks.deliveries.allWebhooks') },
    ...(hooks.data ?? []).map((w) => ({ value: w.id, label: w.name })),
  ];
  const statusOptions = [
    { value: '', label: t('webhooks.deliveries.anyStatus') },
    { value: 'succeeded', label: t('webhooks.status.succeeded') },
    { value: 'pending', label: t('webhooks.status.pendingOrRetrying') },
    { value: 'failed', label: t('webhooks.status.failed') },
  ];

  const columns: Column<DeliveryDto>[] = [
    {
      key: 'status',
      header: t('webhooks.deliveries.col.status'),
      render: (d) => (
        <span className="dam-whdelstatus">
          <StatusBadge state={deliveryState(d)} />
          <RetryText delivery={d} now={now} />
        </span>
      ),
    },
    {
      key: 'event',
      header: t('webhooks.deliveries.col.event'),
      render: (d) => (
        <span className="dam-whevent">
          <button
            type="button"
            className="dam-whlink"
            data-delivery={d.id}
            onClick={() => open(d.id)}
            aria-label={t('webhooks.deliveries.open', { event: d.eventType, webhook: d.webhookName })}
          >
            {d.eventType}
          </button>
          {d.replayOf && (
            <Badge size="sm" tone="info" title={t('webhooks.deliveries.replayOf', { id: d.replayOf })}>
              {t('webhooks.deliveries.replay')}
            </Badge>
          )}
        </span>
      ),
    },
    {
      key: 'webhook',
      header: t('webhooks.deliveries.col.webhook'),
      className: 'dam-col--hide-sm',
      render: (d) => <span className="dam-whtrunc">{d.webhookName}</span>,
    },
    {
      key: 'attempt',
      header: t('webhooks.deliveries.col.attempt'),
      className: 'dam-col--hide-sm',
      render: (d) => (d.attempt > 0 ? t('webhooks.deliveries.attemptOf', { attempt: d.attempt, max: d.maxAttempts }) : '—'),
    },
    {
      key: 'http',
      header: t('webhooks.deliveries.col.http'),
      render: (d) => <span className="dam-whnum">{d.lastStatusCode ?? '—'}</span>,
    },
    {
      key: 'duration',
      header: t('webhooks.deliveries.col.duration'),
      className: 'dam-col--hide-sm',
      render: (d) => (
        <span className="dam-whnum">
          {d.lastDurationMs !== null ? t('webhooks.deliveries.ms', { ms: d.lastDurationMs }) : '—'}
        </span>
      ),
    },
    {
      key: 'time',
      header: t('webhooks.deliveries.col.time'),
      render: (d) => (
        <time dateTime={d.createdAt} title={new Date(d.createdAt).toLocaleString(i18n.language)}>
          {relativeTime(d.createdAt, i18n.language)}
        </time>
      ),
    },
  ];

  // Clicking anywhere in a row opens it: the event button is the keyboard route, this is the pointer convenience.
  const rowClick = (e: MouseEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    if (target.closest('button, a, input, select')) return;
    target.closest('tbody tr')?.querySelector<HTMLButtonElement>('button[data-delivery]')?.click();
  };

  let body;
  if (isForbidden(query.error)) {
    body = (
      <EmptyState
        icon={<WarningCircle size={28} />}
        title={t('webhooks.forbidden.title')}
        description={t('webhooks.forbidden.description')}
      />
    );
  } else if (query.error) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('webhooks.deliveries.loadFailed')}
          description={t('webhooks.loadFailedHint')}
          action={<Button onClick={() => void query.refetch()}>{t('webhooks.retry')}</Button>}
        />
      </div>
    );
  } else if (!query.isPending && rows.length === 0) {
    body = (
      <EmptyState
        icon={<ListMagnifyingGlass size={28} />}
        title={filtered ? t('webhooks.deliveries.noMatch') : t('webhooks.deliveries.empty')}
        description={filtered ? t('webhooks.deliveries.noMatchHint') : t('webhooks.deliveries.emptyHint')}
      />
    );
  } else {
    body = (
      <>
        {/* Pointer convenience only (see rowClick); the event button in each row is the accessible control. */}
        {/* eslint-disable-next-line jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events */}
        <div className="dam-whtable" onClick={rowClick}>
          <Table
            caption={t('webhooks.deliveries.caption')}
            columns={columns}
            rows={rows}
            getRowId={(d) => d.id}
            loading={query.isPending}
          />
        </div>
        {query.hasNextPage && (
          <div className="dam-whmore">
            <Button onClick={() => void query.fetchNextPage()} loading={query.isFetchingNextPage}>
              {t('webhooks.deliveries.loadMore')}
            </Button>
          </div>
        )}
      </>
    );
  }

  return (
    <div className="dam-whtab">
      <div className="dam-whfilters" role="search" aria-label={t('webhooks.deliveries.filters')}>
        <Select
          label={t('webhooks.deliveries.webhook')}
          value={webhook}
          options={webhookOptions}
          onChange={(e) => setParam('webhook', e.target.value)}
        />
        <Select
          label={t('webhooks.deliveries.status')}
          value={status}
          options={statusOptions}
          onChange={(e) => setParam('status', e.target.value)}
        />
        <Select
          label={t('webhooks.deliveries.eventType')}
          value={event}
          options={eventOptions}
          onChange={(e) => setParam('event', e.target.value)}
        />
        <Input
          label={t('webhooks.deliveries.from')}
          type="date"
          value={from}
          max={to || undefined}
          onChange={(e) => setParam('from', e.target.value)}
        />
        <Input
          label={t('webhooks.deliveries.to')}
          type="date"
          value={to}
          min={from || undefined}
          onChange={(e) => setParam('to', e.target.value)}
        />
        <div className="dam-whfilters__actions">
          {filtered && (
            <Button variant="ghost" onClick={clear}>
              {t('webhooks.deliveries.clear')}
            </Button>
          )}
          <Button onClick={() => void query.refetch()} loading={query.isRefetching && !query.isFetchingNextPage}>
            <ArrowClockwise size={16} aria-hidden="true" />
            {t('webhooks.deliveries.refresh')}
          </Button>
        </div>
      </div>
      {anyPending && (
        <p className="dam-hint dam-whlive" role="status">
          {t('webhooks.deliveries.autoRefresh')}
        </p>
      )}
      {body}
      <DeliveryDrawer id={openId} onClose={() => open(null)} onOpen={(id) => open(id)} />
    </div>
  );
}
