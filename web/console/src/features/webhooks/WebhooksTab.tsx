import {
  ArrowsClockwise,
  Clock,
  ListMagnifyingGlass,
  PaperPlaneTilt,
  PencilSimple,
  Key,
  WebhooksLogo,
} from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Button,
  Card,
  DotsThree,
  EmptyState,
  IconButton,
  Modal,
  Plus,
  PopoverMenu,
  Skeleton,
  Trash,
  WarningCircle,
  useToast,
  type MenuItem,
} from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import { CopyButton } from './CopyButton';
import { StatusBadge } from './DeliveryStatus';
import { SecretDialog } from './SecretDialog';
import { Switch } from './Switch';
import { WebhookForm } from './WebhookForm';
import { deliveryState } from './format';
import {
  errorText,
  isForbidden,
  useDeleteWebhook,
  usePingWebhook,
  useRotateSecret,
  useToggleWebhook,
  useWebhookEvents,
  useWebhooks,
  type WebhookDto,
} from './webhooksApi';

const MAX_CHIPS = 3;

/** Event patterns as small chips; past three they collapse into "+N" (the full list is its tooltip). */
export function EventChips({ events, label }: { events: string[]; label: string }) {
  const { t } = useTranslation();
  const shown = events.slice(0, MAX_CHIPS);
  const more = events.length - shown.length;
  return (
    <ul className="dam-whchips" aria-label={label}>
      {shown.map((e) => (
        <li key={e} className="dam-whchip">
          {e === '*' ? t('webhooks.events.all') : e}
        </li>
      ))}
      {more > 0 && (
        <li className="dam-whchip dam-whchip--more" title={events.slice(MAX_CHIPS).join(', ')}>
          <span aria-hidden="true">+{more}</span>
          <span className="dam-sr-only">
            {t('webhooks.events.more', { count: more, list: events.slice(MAX_CHIPS).join(', ') })}
          </span>
        </li>
      )}
    </ul>
  );
}

interface CardProps {
  webhook: WebhookDto;
  busy: boolean;
  onToggle: (active: boolean) => void;
  onEdit: () => void;
  onPing: () => void;
  onRotate: () => void;
  onDeliveries: () => void;
  onDelete: () => void;
}

function WebhookCard({ webhook: w, busy, onToggle, onEdit, onPing, onRotate, onDeliveries, onDelete }: CardProps) {
  const { t, i18n } = useTranslation();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const { stats } = w;
  const items: MenuItem[] = [
    { key: 'edit', label: t('webhooks.menu.edit'), icon: <PencilSimple size={16} />, onSelect: onEdit },
    {
      key: 'ping',
      label: t('webhooks.menu.ping'),
      icon: <PaperPlaneTilt size={16} />,
      disabled: !w.active || busy,
      onSelect: onPing,
    },
    { key: 'deliveries', label: t('webhooks.menu.deliveries'), icon: <ListMagnifyingGlass size={16} />, onSelect: onDeliveries },
    { key: 'rotate', label: t('webhooks.menu.rotate'), icon: <Key size={16} />, onSelect: onRotate },
    {
      key: 'delete',
      label: t('webhooks.menu.delete'),
      icon: <Trash size={16} />,
      tone: 'danger',
      separatorBefore: true,
      onSelect: onDelete,
    },
  ];
  const statBadge = (n: number, tone: 'success' | 'danger' | 'warning', key: string) => (
    <Badge tone={n > 0 ? tone : 'neutral'} size="sm">
      {t(key, { count: n })}
    </Badge>
  );
  return (
    <li>
      <Card as="article" className={`dam-whcard${w.active ? '' : ' dam-whcard--off'}`} aria-labelledby={`wh-${w.id}`}>
        <div className="dam-whcard__head">
          <div className="dam-whcard__title">
            <h2 id={`wh-${w.id}`} className="dam-whcard__name">
              {w.name}
            </h2>
            {!w.active && <Badge size="sm">{t('webhooks.paused')}</Badge>}
          </div>
          <div className="dam-whcard__controls">
            <Switch
              checked={w.active}
              onChange={onToggle}
              label={t('webhooks.active')}
              ariaLabel={t('webhooks.activeNamed', { name: w.name })}
            />
            <IconButton
              label={t('webhooks.menu.label', { name: w.name })}
              size="sm"
              aria-haspopup="menu"
              aria-expanded={anchor !== null}
              onClick={(e) => setAnchor(anchor ? null : e.currentTarget)}
            >
              <DotsThree size={20} weight="bold" aria-hidden="true" />
            </IconButton>
            {anchor && (
              <PopoverMenu
                anchor={anchor}
                label={t('webhooks.menu.label', { name: w.name })}
                items={items}
                onClose={() => setAnchor(null)}
              />
            )}
          </div>
        </div>

        <div className="dam-whcard__url">
          <code className="dam-whurl" title={w.url}>
            {w.url}
          </code>
          <CopyButton text={w.url} label={t('webhooks.copyUrl', { name: w.name })} />
        </div>

        <EventChips events={w.events} label={t('webhooks.events.listFor', { name: w.name })} />

        <div className="dam-whcard__stats">
          <span className="dam-whcard__statslabel">{t('webhooks.last24h')}</span>
          {statBadge(stats.succeeded24h, 'success', 'webhooks.stats.succeeded')}
          {statBadge(stats.failed24h, 'danger', 'webhooks.stats.failed')}
          {statBadge(stats.pending, 'warning', 'webhooks.stats.pending')}
        </div>

        <div className="dam-whcard__foot">
          <span className="dam-whcard__last">
            <Clock size={14} aria-hidden="true" />
            {stats.lastDeliveryAt ? (
              <>
                <span>{t('webhooks.lastDelivery')}</span>
                {stats.lastStatus && (
                  <StatusBadge state={deliveryState({ status: stats.lastStatus, lastStatusCode: null, lastError: null })} />
                )}
                <time dateTime={stats.lastDeliveryAt} title={new Date(stats.lastDeliveryAt).toLocaleString(i18n.language)}>
                  {relativeTime(stats.lastDeliveryAt, i18n.language)}
                </time>
              </>
            ) : (
              <span>{t('webhooks.noDeliveries')}</span>
            )}
          </span>
          <span className="dam-whcard__secret">
            {t('webhooks.secretLabel')} <code>whsec_…{w.secretLast4}</code>
          </span>
        </div>
      </Card>
    </li>
  );
}

/** The webhook list and everything you can do to a webhook. */
export function WebhooksTab({ onViewDeliveries }: { onViewDeliveries: (webhookId: string) => void }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const query = useWebhooks();
  const catalogue = useWebhookEvents();
  const toggle = useToggleWebhook();
  const ping = usePingWebhook();
  const rotate = useRotateSecret();
  const remove = useDeleteWebhook();
  const [form, setForm] = useState<{ webhook: WebhookDto | null } | null>(null);
  const [toDelete, setToDelete] = useState<WebhookDto | null>(null);
  const [toRotate, setToRotate] = useState<WebhookDto | null>(null);
  const [reveal, setReveal] = useState<{ secret: string; name: string; reason: 'created' | 'rotated' } | null>(null);
  const [formKey, setFormKey] = useState(0);

  const openForm = (webhook: WebhookDto | null) => {
    setFormKey((k) => k + 1);
    setForm({ webhook });
  };
  const webhooks = query.data ?? [];

  const doPing = (w: WebhookDto) =>
    ping.mutate(w.id, {
      onSuccess: (d) =>
        d.status === 'succeeded'
          ? push({
              title: t('webhooks.ping.ok', { name: w.name }),
              message: t('webhooks.ping.okDetail', { code: d.lastStatusCode, ms: d.lastDurationMs ?? 0 }),
              tone: 'success',
            })
          : push({
              title: t('webhooks.ping.failed', { name: w.name }),
              message: d.lastError ?? (d.lastStatusCode ? t('webhooks.ping.httpStatus', { code: d.lastStatusCode }) : undefined),
              tone: 'error',
            }),
      onError: (e) => push({ title: t('webhooks.ping.error'), message: errorText(e), tone: 'error' }),
    });

  const addButton = (
    <Button variant="primary" onClick={() => openForm(null)}>
      <Plus size={16} weight="bold" aria-hidden="true" />
      {t('webhooks.new')}
    </Button>
  );

  let body;
  if (query.isPending) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('webhooks.loading')}</span>
        <ul className="dam-whlist" aria-hidden="true">
          {[0, 1].map((i) => (
            <li key={i}>
              <Card>
                <div className="dam-stack dam-stack--fill">
                  <Skeleton height={20} width="40%" />
                  <Skeleton height={16} width="75%" />
                  <Skeleton height={22} width="60%" />
                  <Skeleton height={16} width="50%" />
                </div>
              </Card>
            </li>
          ))}
        </ul>
      </div>
    );
  } else if (isForbidden(query.error)) {
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
          title={t('webhooks.loadFailed')}
          description={t('webhooks.loadFailedHint')}
          action={<Button onClick={() => void query.refetch()}>{t('webhooks.retry')}</Button>}
        />
      </div>
    );
  } else if (webhooks.length === 0) {
    body = (
      <EmptyState
        icon={<WebhooksLogo size={28} />}
        title={t('webhooks.empty.title')}
        description={t('webhooks.empty.description')}
        action={addButton}
        titleAs="h2"
      />
    );
  } else {
    body = (
      <ul className="dam-whlist" aria-label={t('webhooks.tabs.webhooks')}>
        {webhooks.map((w) => (
          <WebhookCard
            key={w.id}
            webhook={w}
            busy={ping.isPending}
            onToggle={(active) =>
              toggle.mutate(
                { webhook: w, active },
                {
                  onError: (e) =>
                    push({ title: t('webhooks.toggleFailed', { name: w.name }), message: errorText(e), tone: 'error' }),
                },
              )
            }
            onEdit={() => openForm(w)}
            onPing={() => doPing(w)}
            onRotate={() => setToRotate(w)}
            onDeliveries={() => onViewDeliveries(w.id)}
            onDelete={() => setToDelete(w)}
          />
        ))}
      </ul>
    );
  }

  const failed = query.isError && !isForbidden(query.error);
  return (
    <div className="dam-whtab">
      {!query.isPending && !failed && !isForbidden(query.error) && webhooks.length > 0 && (
        <div className="dam-whtab__bar">
          <p className="dam-hint">{t('webhooks.intro')}</p>
          {addButton}
        </div>
      )}
      {body}

      <WebhookForm
        key={formKey}
        open={form !== null}
        webhook={form?.webhook ?? null}
        events={catalogue.data ?? []}
        eventsLoading={catalogue.isPending}
        onClose={() => setForm(null)}
        onSaved={({ webhook, secret }) => {
          setForm(null);
          if (secret) setReveal({ secret, name: webhook.name, reason: 'created' });
          else push({ title: t('webhooks.saved', { name: webhook.name }), tone: 'success' });
        }}
      />

      <SecretDialog
        secret={reveal?.secret ?? null}
        webhookName={reveal?.name ?? ''}
        reason={reveal?.reason ?? 'created'}
        onClose={() => setReveal(null)}
      />

      <Modal
        open={toRotate !== null}
        title={t('webhooks.rotate.title')}
        closeLabel={t('webhooks.close')}
        onClose={() => setToRotate(null)}
      >
        <div className="dam-stack dam-dialogform">
          <p>{t('webhooks.rotate.body', { name: toRotate?.name })}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setToRotate(null)}>
              {t('webhooks.cancel')}
            </Button>
            <Button
              variant="primary"
              loading={rotate.isPending}
              onClick={() =>
                rotate.mutate(toRotate!.id, {
                  onSuccess: (r) => {
                    setToRotate(null);
                    setReveal({ secret: r.secret, name: r.webhook.name, reason: 'rotated' });
                  },
                  onError: (e) => {
                    setToRotate(null);
                    push({ title: t('webhooks.rotate.failed'), message: errorText(e), tone: 'error' });
                  },
                })
              }
            >
              <ArrowsClockwise size={16} aria-hidden="true" />
              {t('webhooks.rotate.confirm')}
            </Button>
          </div>
        </div>
      </Modal>

      <Modal
        open={toDelete !== null}
        title={t('webhooks.delete.title')}
        closeLabel={t('webhooks.close')}
        onClose={() => setToDelete(null)}
      >
        <div className="dam-stack dam-dialogform">
          <p>{t('webhooks.delete.body', { name: toDelete?.name })}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setToDelete(null)}>
              {t('webhooks.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={remove.isPending}
              onClick={() =>
                remove.mutate(toDelete!.id, {
                  onSuccess: () => {
                    const name = toDelete!.name;
                    setToDelete(null);
                    push({ title: t('webhooks.delete.done', { name }), tone: 'success' });
                  },
                  onError: (e) => {
                    setToDelete(null);
                    push({ title: t('webhooks.delete.failed'), message: errorText(e), tone: 'error' });
                  },
                })
              }
            >
              {t('webhooks.delete.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
