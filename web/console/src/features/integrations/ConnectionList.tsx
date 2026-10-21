import { ClockCounterClockwise, Flask, PencilSimple, PlugsConnected } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Card, EmptyState, Modal, Skeleton, Trash, WarningCircle, useToast } from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import { Switch } from '../webhooks/Switch';
import { ActivityDrawer } from './ActivityDrawer';
import { TestPanel } from './TestPanel';
import { canTestConnector } from './formModel';
import { ConnectionStatusBadge } from './badges';
import { errorText, isForbidden, useDeleteConnection, useTestConnection, useToggleConnection } from './integrationsApi';
import type { ConnectionDto, ConnectorDto } from './integrationsTypes';

const isPast = (iso: string) => new Date(iso).getTime() < Date.now();

interface CardProps {
  connection: ConnectionDto;
  connectorName: string;
  /** Whether the connector has Test connection (a connector that only stores settings does not, and shows "Saved"). */
  canTest: boolean;
  canEdit: boolean;
  onToggle: (enabled: boolean) => void;
  onTest: () => void;
  onEdit: () => void;
  onActivity: () => void;
  onDelete: () => void;
}

function Time({ iso, prefixKey }: { iso: string; prefixKey: string }) {
  const { t, i18n } = useTranslation();
  return (
    <span>
      {t(prefixKey)}{' '}
      <time dateTime={iso} title={new Date(iso).toLocaleString(i18n.language)}>
        {relativeTime(iso, i18n.language)}
      </time>
    </span>
  );
}

function ConnectionCard({
  connection: c,
  connectorName,
  canTest,
  canEdit,
  onToggle,
  onTest,
  onEdit,
  onActivity,
  onDelete,
}: CardProps) {
  const { t, i18n } = useTranslation();
  const expiry = c.tokenExpiresAt ? new Date(c.tokenExpiresAt) : null;
  const expired = c.tokenExpiresAt ? isPast(c.tokenExpiresAt) : false;
  return (
    <li>
      <Card as="article" aria-labelledby={`int-conn-${c.id}`} className={`dam-intcard${c.enabled ? '' : ' dam-intcard--off'}`}>
        <div className="dam-intcard__head">
          <div className="dam-intcard__titles">
            <h3 id={`int-conn-${c.id}`} className="dam-intcard__name">
              {c.name}
            </h3>
            <span className="dam-hint">{connectorName}</span>
          </div>
          <Switch
            checked={c.enabled}
            onChange={onToggle}
            label={t('integrations.enabled')}
            ariaLabel={t('integrations.enabledNamed', { name: c.name })}
          />
        </div>

        <div className="dam-intcard__status">
          <ConnectionStatusBadge status={c.status} saved={!canTest} />
          {canTest && c.statusDetail && <span className="dam-intcard__detail">{c.statusDetail}</span>}
        </div>

        {canTest ? (
          <p className="dam-intcard__facts">
            {c.lastTestedAt ? (
              <Time iso={c.lastTestedAt} prefixKey="integrations.lastTested" />
            ) : (
              <span>{t('integrations.neverTested')}</span>
            )}
            {expiry && c.tokenExpiresAt && (
              <span>
                {t(expired ? 'integrations.tokenExpired' : 'integrations.tokenExpires')}{' '}
                <time dateTime={c.tokenExpiresAt} title={expiry.toLocaleString(i18n.language)}>
                  {relativeTime(c.tokenExpiresAt, i18n.language)}
                </time>
              </span>
            )}
          </p>
        ) : (
          <p className="dam-intcard__facts">
            <span>{t('integrations.storedOnlyNote', { name: connectorName })}</span>
          </p>
        )}

        <div className="dam-intcard__actions">
          {canTest && (
            <Button size="sm" aria-label={t('integrations.testNamed', { name: c.name })} onClick={onTest}>
              <Flask size={16} aria-hidden="true" />
              {t('integrations.testAction')}
            </Button>
          )}
          <Button
            size="sm"
            aria-label={t('integrations.editNamed', { name: c.name })}
            disabled={!canEdit}
            title={canEdit ? undefined : t('integrations.connectorMissing')}
            onClick={onEdit}
          >
            <PencilSimple size={16} aria-hidden="true" />
            {t('integrations.edit')}
          </Button>
          <Button size="sm" aria-label={t('integrations.activityNamed', { name: c.name })} onClick={onActivity}>
            <ClockCounterClockwise size={16} aria-hidden="true" />
            {t('integrations.activity')}
          </Button>
          <Button
            size="sm"
            variant="ghost"
            className="dam-intdanger"
            aria-label={t('integrations.deleteNamed', { name: c.name })}
            onClick={onDelete}
          >
            <Trash size={16} aria-hidden="true" />
            {t('integrations.delete')}
          </Button>
        </div>
      </Card>
    </li>
  );
}

interface Props {
  connections: ConnectionDto[] | undefined;
  connectors: ConnectorDto[] | undefined;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  onEdit: (connection: ConnectionDto, connector: ConnectorDto) => void;
}

/** The tenant's connections, with Test, Edit, Activity and Delete, the enabled switch and the dialogs those open. */
export function ConnectionList({ connections, connectors, loading, error, onRetry, onEdit }: Props) {
  const { t } = useTranslation();
  const { push } = useToast();
  const toggle = useToggleConnection();
  const remove = useDeleteConnection();
  const test = useTestConnection();
  const [testing, setTesting] = useState<ConnectionDto | null>(null);
  const [activity, setActivity] = useState<ConnectionDto | null>(null);
  const [toDelete, setToDelete] = useState<ConnectionDto | null>(null);

  const connectorOf = (c: ConnectionDto) => connectors?.find((x) => x.key === c.connector);
  const runTest = (c: ConnectionDto) => {
    setTesting(c);
    test.mutate(c.id);
  };
  const closeTest = () => {
    setTesting(null);
    test.reset();
  };

  let body;
  if (loading) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('integrations.loading')}</span>
        <ul className="dam-intgrid" aria-hidden="true">
          {[0, 1].map((i) => (
            <li key={i}>
              <Card>
                <div className="dam-stack dam-stack--fill">
                  <Skeleton height={20} width="40%" />
                  <Skeleton height={20} width="30%" />
                  <Skeleton height={14} width="60%" />
                  <Skeleton height={32} />
                </div>
              </Card>
            </li>
          ))}
        </ul>
      </div>
    );
  } else if (error) {
    const forbidden = isForbidden(error);
    body = (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          titleAs="p"
          title={forbidden ? t('integrations.forbidden.title') : t('integrations.connections.loadFailed')}
          description={forbidden ? t('integrations.forbidden.description') : t('integrations.loadFailedHint')}
          action={forbidden ? undefined : <Button onClick={onRetry}>{t('integrations.retry')}</Button>}
        />
      </div>
    );
  } else if (!connections || connections.length === 0) {
    body = (
      <EmptyState
        icon={<PlugsConnected size={28} />}
        titleAs="p"
        title={t('integrations.connections.empty.title')}
        description={t('integrations.connections.empty.description')}
      />
    );
  } else {
    body = (
      <ul className="dam-intgrid dam-intgrid--wide" aria-label={t('integrations.connections.title')}>
        {connections.map((c) => {
          const connector = connectorOf(c);
          return (
            <ConnectionCard
              key={c.id}
              connection={c}
              connectorName={connector?.name ?? c.connector}
              canTest={connector ? canTestConnector(connector) : true}
              canEdit={!!connector}
              onToggle={(enabled) =>
                toggle.mutate(
                  { connection: c, enabled },
                  {
                    onError: (e) =>
                      push({ title: t('integrations.toggleFailed', { name: c.name }), message: errorText(e), tone: 'error' }),
                  },
                )
              }
              onTest={() => runTest(c)}
              onEdit={() => connector && onEdit(c, connector)}
              onActivity={() => setActivity(c)}
              onDelete={() => setToDelete(c)}
            />
          );
        })}
      </ul>
    );
  }

  return (
    <>
      {body}

      <Modal
        open={testing !== null}
        title={t('integrations.test.title', { name: testing?.name ?? '' })}
        closeLabel={t('integrations.close')}
        onClose={closeTest}
      >
        <div className="dam-intdialog">
          <TestPanel
            state={test.isPending ? 'pending' : test.isError ? 'error' : test.isSuccess ? 'done' : 'idle'}
            result={test.data}
            error={test.error}
            onRetry={testing ? () => test.mutate(testing.id) : undefined}
          />
          <div className="dam-intdialog__actions">
            <Button variant="secondary" onClick={closeTest}>
              {t('integrations.done')}
            </Button>
          </div>
        </div>
      </Modal>

      <ActivityDrawer connection={activity} onClose={() => setActivity(null)} />

      <Modal
        open={toDelete !== null}
        title={t('integrations.deleteDialog.title')}
        closeLabel={t('integrations.close')}
        onClose={() => setToDelete(null)}
      >
        <div className="dam-intdialog">
          <p>{t('integrations.deleteDialog.body', { name: toDelete?.name ?? '' })}</p>
          <div className="dam-intdialog__actions">
            <Button variant="ghost" onClick={() => setToDelete(null)}>
              {t('integrations.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={remove.isPending}
              onClick={() =>
                remove.mutate(toDelete!.id, {
                  onSuccess: () => {
                    const name = toDelete!.name;
                    setToDelete(null);
                    push({ title: t('integrations.deleteDialog.done', { name }), tone: 'success' });
                  },
                  onError: (e) => {
                    setToDelete(null);
                    push({ title: t('integrations.deleteDialog.failed'), message: errorText(e), tone: 'error' });
                  },
                })
              }
            >
              {t('integrations.deleteDialog.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
}
