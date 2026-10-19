import { MicrosoftTeamsLogo, PaperPlaneTilt, PencilSimple, SlackLogo, ChatCircleText } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Card, EmptyState, Modal, Plus, Skeleton, Trash, WarningCircle, useToast } from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import { ChannelForm } from './ChannelForm';
import { Switch } from './Switch';
import { EventChips } from './WebhooksTab';
import {
  errorText,
  isForbidden,
  useChannels,
  useDeleteChannel,
  useTestChannel,
  useToggleChannel,
  useWebhookEvents,
  type ChannelDto,
  type ChannelTestResult,
} from './webhooksApi';

interface TestOutcome {
  result: ChannelTestResult;
  at: string;
}

export function ChannelsTab() {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const query = useChannels();
  const catalogue = useWebhookEvents();
  const toggle = useToggleChannel();
  const test = useTestChannel();
  const remove = useDeleteChannel();
  const [form, setForm] = useState<{ channel: ChannelDto | null } | null>(null);
  const [formKey, setFormKey] = useState(0);
  const [toDelete, setToDelete] = useState<ChannelDto | null>(null);
  const [testing, setTesting] = useState<string | null>(null);
  const [outcomes, setOutcomes] = useState<Record<string, TestOutcome>>({});
  const channels = query.data ?? [];

  const openForm = (channel: ChannelDto | null) => {
    setFormKey((k) => k + 1);
    setForm({ channel });
  };

  const runTest = (c: ChannelDto) => {
    setTesting(c.id);
    test.mutate(c.id, {
      onSettled: () => setTesting(null),
      onSuccess: (result) => {
        setOutcomes((o) => ({ ...o, [c.id]: { result, at: new Date().toISOString() } }));
        if (result.ok)
          push({
            title: t('webhooks.channels.testOk', { name: c.name }),
            message: t('webhooks.channels.testOkDetail', { code: result.statusCode ?? 200, ms: result.durationMs }),
            tone: 'success',
          });
        else
          push({
            title: t('webhooks.channels.testFailed', { name: c.name }),
            message: result.error ?? (result.statusCode ? t('webhooks.ping.httpStatus', { code: result.statusCode }) : undefined),
            tone: 'error',
          });
      },
      onError: (e) => push({ title: t('webhooks.channels.testError'), message: errorText(e), tone: 'error' }),
    });
  };

  const addButton = (
    <Button variant="primary" onClick={() => openForm(null)}>
      <Plus size={16} weight="bold" aria-hidden="true" />
      {t('webhooks.channels.new')}
    </Button>
  );

  let body;
  if (query.isPending) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('webhooks.channels.loading')}</span>
        <ul className="dam-whlist" aria-hidden="true">
          {[0, 1].map((i) => (
            <li key={i}>
              <Card>
                <div className="dam-stack dam-stack--fill">
                  <Skeleton height={20} width="40%" />
                  <Skeleton height={16} width="60%" />
                  <Skeleton height={22} width="55%" />
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
        title={t('webhooks.channels.forbidden.title')}
        description={t('webhooks.channels.forbidden.description')}
      />
    );
  } else if (query.error) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('webhooks.channels.loadFailed')}
          description={t('webhooks.loadFailedHint')}
          action={<Button onClick={() => void query.refetch()}>{t('webhooks.retry')}</Button>}
        />
      </div>
    );
  } else if (channels.length === 0) {
    body = (
      <EmptyState
        icon={<ChatCircleText size={28} />}
        title={t('webhooks.channels.empty.title')}
        description={t('webhooks.channels.empty.description')}
        action={addButton}
      />
    );
  } else {
    body = (
      <ul className="dam-whlist" aria-label={t('webhooks.tabs.channels')}>
        {channels.map((c) => {
          const Logo = c.kind === 'teams' ? MicrosoftTeamsLogo : SlackLogo;
          const outcome = outcomes[c.id];
          return (
            <li key={c.id}>
              <Card as="article" className={`dam-whcard${c.active ? '' : ' dam-whcard--off'}`} aria-labelledby={`ch-${c.id}`}>
                <div className="dam-whcard__head">
                  <div className="dam-whcard__title">
                    <Badge tone="brand" size="sm">
                      <Logo size={12} weight="fill" aria-hidden="true" />{' '}
                      {c.kind === 'teams' ? t('webhooks.channels.teams') : t('webhooks.channels.slack')}
                    </Badge>
                    <h2 id={`ch-${c.id}`} className="dam-whcard__name">
                      {c.name}
                    </h2>
                  </div>
                  <div className="dam-whcard__controls">
                    <Switch
                      checked={c.active}
                      onChange={(active) =>
                        toggle.mutate(
                          { channel: c, active },
                          {
                            onError: (e) =>
                              push({ title: t('webhooks.toggleFailed', { name: c.name }), message: errorText(e), tone: 'error' }),
                          },
                        )
                      }
                      label={t('webhooks.active')}
                      ariaLabel={t('webhooks.activeNamed', { name: c.name })}
                    />
                  </div>
                </div>
                <p className="dam-whcard__url">
                  <code className="dam-whurl" title={t('webhooks.channels.urlStored')}>
                    {c.urlHint}
                  </code>
                </p>
                <EventChips events={c.events} label={t('webhooks.events.listFor', { name: c.name })} />
                {outcome && (
                  <p className="dam-whcard__test" role="status">
                    <Badge tone={outcome.result.ok ? 'success' : 'danger'} size="sm">
                      {outcome.result.ok ? t('webhooks.channels.lastTestOk') : t('webhooks.channels.lastTestFailed')}
                    </Badge>
                    <span className="dam-hint">
                      {outcome.result.ok
                        ? t('webhooks.channels.testOkDetail', {
                            code: outcome.result.statusCode ?? 200,
                            ms: outcome.result.durationMs,
                          })
                        : (outcome.result.error ?? t('webhooks.ping.httpStatus', { code: outcome.result.statusCode }))}{' '}
                      · {relativeTime(outcome.at, i18n.language)}
                    </span>
                  </p>
                )}
                <div className="dam-whcard__actions">
                  <Button
                    size="sm"
                    onClick={() => runTest(c)}
                    loading={testing === c.id}
                    aria-label={t('webhooks.channels.testNamed', { name: c.name })}
                  >
                    <PaperPlaneTilt size={14} aria-hidden="true" />
                    {t('webhooks.channels.test')}
                  </Button>
                  <Button size="sm" onClick={() => openForm(c)} aria-label={t('webhooks.channels.editNamed', { name: c.name })}>
                    <PencilSimple size={14} aria-hidden="true" />
                    {t('webhooks.menu.edit')}
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="dam-whdanger"
                    onClick={() => setToDelete(c)}
                    aria-label={t('webhooks.channels.deleteNamed', { name: c.name })}
                  >
                    <Trash size={14} aria-hidden="true" />
                    {t('webhooks.menu.delete')}
                  </Button>
                </div>
              </Card>
            </li>
          );
        })}
      </ul>
    );
  }

  return (
    <div className="dam-whtab">
      {!query.isPending && !query.error && channels.length > 0 && (
        <div className="dam-whtab__bar">
          <p className="dam-hint">{t('webhooks.channels.intro')}</p>
          {addButton}
        </div>
      )}
      {body}

      <ChannelForm
        key={formKey}
        open={form !== null}
        channel={form?.channel ?? null}
        events={catalogue.data ?? []}
        eventsLoading={catalogue.isPending}
        onClose={() => setForm(null)}
        onSaved={(channel) => {
          setForm(null);
          push({ title: t('webhooks.channels.saved', { name: channel.name }), tone: 'success' });
        }}
      />

      <Modal
        open={toDelete !== null}
        title={t('webhooks.channels.deleteTitle')}
        closeLabel={t('webhooks.close')}
        onClose={() => setToDelete(null)}
      >
        <div className="dam-stack dam-dialogform">
          <p>{t('webhooks.channels.deleteBody', { name: toDelete?.name })}</p>
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
                    push({ title: t('webhooks.channels.deleted', { name }), tone: 'success' });
                  },
                  onError: (e) => {
                    setToDelete(null);
                    push({ title: t('webhooks.channels.deleteFailed'), message: errorText(e), tone: 'error' });
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
