import { ArrowsClockwise, WarningCircle } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Drawer, Modal, Skeleton, useToast } from '../../ui';
import { CodeBlock } from './CodeBlock';
import { CopyButton } from './CopyButton';
import { RetryText, StatusBadge } from './DeliveryStatus';
import { deliveryState, prettyJson, useNow } from './format';
import { errorText, isForbidden, useDelivery, useReplayDelivery, type DeliveryDetail } from './webhooksApi';

function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt>{label}</dt>
      <dd>{children}</dd>
    </>
  );
}

interface Note {
  forId: string;
  tone: 'ok' | 'info' | 'danger';
  text: string;
}

function Body({
  d,
  note,
  onOpen,
  onReplay,
}: {
  d: DeliveryDetail;
  note: Note | null;
  onOpen: (id: string) => void;
  onReplay: () => void;
}) {
  const { t, i18n } = useTranslation();
  const now = useNow(15_000);
  const payload = prettyJson(d.payload);
  const when = (iso: string) => new Date(iso).toLocaleString(i18n.language);

  return (
    <div className="dam-whd">
      {/* The replay outcome, repeated from the toast: a toast sits beneath an open drawer and would go unseen. */}
      {note?.forId === d.id && (
        <p role={note.tone === 'danger' ? 'alert' : 'status'} className={`dam-whwarn dam-whwarn--${note.tone}`}>
          {note.text}
        </p>
      )}
      <div className="dam-whd__top">
        <StatusBadge state={deliveryState(d)} size="md" />
        <RetryText delivery={d} now={now} />
        <Button className="dam-whd__replay" onClick={onReplay}>
          <ArrowsClockwise size={16} aria-hidden="true" />
          {t('webhooks.replayAction')}
        </Button>
      </div>

      <dl className="dam-dl dam-whd__facts">
        <Fact label={t('webhooks.deliveries.col.event')}>
          <code>{d.eventType}</code>
        </Fact>
        <Fact label={t('webhooks.deliveries.col.webhook')}>{d.webhookName}</Fact>
        <Fact label={t('webhooks.deliveries.col.attempt')}>
          {d.attempt > 0 ? t('webhooks.deliveries.attemptOf', { attempt: d.attempt, max: d.maxAttempts }) : '—'}
        </Fact>
        <Fact label={t('webhooks.detail.response')}>
          {d.lastStatusCode !== null ? <span className="dam-whnum">HTTP {d.lastStatusCode}</span> : '—'}
        </Fact>
        <Fact label={t('webhooks.deliveries.col.duration')}>
          {d.lastDurationMs !== null ? t('webhooks.deliveries.ms', { ms: d.lastDurationMs }) : '—'}
        </Fact>
        <Fact label={t('webhooks.detail.created')}>{when(d.createdAt)}</Fact>
        {d.deliveredAt && <Fact label={t('webhooks.detail.delivered')}>{when(d.deliveredAt)}</Fact>}
        {d.status === 'pending' && d.nextAttemptAt && (
          <Fact label={t('webhooks.detail.nextAttempt')}>{when(d.nextAttemptAt)}</Fact>
        )}
        {d.replayOf && (
          <Fact label={t('webhooks.deliveries.replay')}>
            <button type="button" className="dam-whlink" onClick={() => onOpen(d.replayOf!)}>
              {t('webhooks.detail.replayOf')}
            </button>
          </Fact>
        )}
        <Fact label={t('webhooks.detail.deliveryId')}>
          <code className="dam-whid">{d.id}</code>
        </Fact>
        <Fact label={t('webhooks.detail.eventId')}>
          <code className="dam-whid">{d.eventId}</code>
        </Fact>
      </dl>

      {d.lastError && (
        <div className="dam-whd__block">
          <h3 id="whd-error" className="dam-whd__h">
            {t('webhooks.detail.lastError')}
          </h3>
          <p className="dam-whwarn dam-whwarn--danger">
            <WarningCircle size={16} aria-hidden="true" />
            <span>{d.lastError}</span>
          </p>
        </div>
      )}

      <div className="dam-whd__block">
        <h3 id="whd-response" className="dam-whd__h">
          {t('webhooks.detail.responseBody')}
        </h3>
        {d.responseExcerpt ? (
          <CodeBlock label={t('webhooks.detail.responseBody')}>{d.responseExcerpt}</CodeBlock>
        ) : (
          <p className="dam-hint">{t('webhooks.detail.noResponse')}</p>
        )}
      </div>

      <div className="dam-whd__block">
        <div className="dam-whd__hrow">
          <h3 id="whd-payload" className="dam-whd__h">
            {t('webhooks.detail.payload')}
          </h3>
          <CopyButton text={payload} label={t('webhooks.detail.copyPayload')} />
        </div>
        <CodeBlock label={t('webhooks.detail.payload')}>{payload}</CodeBlock>
      </div>

      <p className="dam-hint">{t('webhooks.detail.retryNote', { max: d.maxAttempts })}</p>
    </div>
  );
}

/** The side panel for one delivery: outcome, payload, the receiver's answer, and Replay. Open while `id` is set. */
export function DeliveryDrawer({
  id,
  onClose,
  onOpen,
}: {
  id: string | null;
  onClose: () => void;
  onOpen: (id: string) => void;
}) {
  const { t } = useTranslation();
  const { push } = useToast();
  const query = useDelivery(id);
  const replay = useReplayDelivery();
  const [confirm, setConfirm] = useState(false);
  const [note, setNote] = useState<Note | null>(null);
  const d = query.data;

  const doReplay = () =>
    replay.mutate(d!.id, {
      onSuccess: (r) => {
        setConfirm(false);
        const detail = r.lastError ?? (r.lastStatusCode ? t('webhooks.ping.httpStatus', { code: r.lastStatusCode }) : '');
        setNote({
          forId: r.id,
          tone: r.status === 'succeeded' ? 'ok' : r.status === 'failed' ? 'danger' : 'info',
          text: [
            t(`webhooks.replayDone.${r.status === 'succeeded' ? 'ok' : r.status === 'failed' ? 'failed' : 'retrying'}`),
            detail,
          ]
            .filter(Boolean)
            .join(' '),
        });
        if (r.status === 'succeeded')
          push({
            title: t('webhooks.replayDone.ok'),
            message: t('webhooks.ping.okDetail', { code: r.lastStatusCode, ms: r.lastDurationMs ?? 0 }),
            tone: 'success',
          });
        else
          push({
            title: r.status === 'failed' ? t('webhooks.replayDone.failed') : t('webhooks.replayDone.retrying'),
            message: r.lastError ?? (r.lastStatusCode ? t('webhooks.ping.httpStatus', { code: r.lastStatusCode }) : undefined),
            tone: r.status === 'failed' ? 'error' : 'info',
          });
        onOpen(r.id);
      },
      onError: (e) => {
        setConfirm(false);
        setNote({ forId: d!.id, tone: 'danger', text: [t('webhooks.replayDone.error'), errorText(e)].filter(Boolean).join(' ') });
        push({ title: t('webhooks.replayDone.error'), message: errorText(e), tone: 'error' });
      },
    });

  return (
    <>
      <Drawer open={id !== null} title={t('webhooks.detail.title')} closeLabel={t('webhooks.close')} onClose={onClose}>
        {query.isPending ? (
          <div role="status" aria-live="polite" className="dam-stack dam-stack--fill">
            <span className="dam-sr-only">{t('webhooks.loading')}</span>
            <Skeleton height={24} width="40%" />
            <Skeleton height={120} />
            <Skeleton height={180} />
          </div>
        ) : query.error ? (
          <div role="alert" className="dam-stack">
            <p>{isForbidden(query.error) ? t('webhooks.forbidden.description') : t('webhooks.detail.loadFailed')}</p>
            <Button onClick={() => void query.refetch()}>{t('webhooks.retry')}</Button>
          </div>
        ) : (
          <Body key={query.data.id} d={query.data} note={note} onOpen={onOpen} onReplay={() => setConfirm(true)} />
        )}
      </Drawer>
      {/* A sibling of the drawer, not a child: a dialog's close event bubbles through React, so a nested confirmation
          would close the drawer behind it. */}
      <Modal
        open={confirm && d !== undefined}
        title={t('webhooks.replay.title')}
        closeLabel={t('webhooks.close')}
        onClose={() => setConfirm(false)}
      >
        <div className="dam-stack dam-dialogform">
          <p>{t('webhooks.replay.body', { event: d?.eventType, name: d?.webhookName })}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setConfirm(false)}>
              {t('webhooks.cancel')}
            </Button>
            <Button variant="primary" loading={replay.isPending} onClick={doReplay}>
              {t('webhooks.replay.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
}
