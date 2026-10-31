import { ArrowsClockwise, ClockCounterClockwise, Info, WarningCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Drawer, EmptyState, Skeleton } from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import { errorText, isForbidden, useActivity } from './integrationsApi';
import type { ActivityDto, ConnectionDto } from './integrationsTypes';

function Entry({ call }: { call: ActivityDto }) {
  const { t, i18n } = useTranslation();
  return (
    <li className="dam-intact__item">
      <div className="dam-intact__line">
        <Badge size="sm" tone={call.ok ? 'success' : 'danger'}>
          {call.ok ? t('integrations.activityPanel.ok') : t('integrations.activityPanel.failed')}
        </Badge>
        <span className="dam-intact__method">{call.method}</span>
        <code className="dam-intact__target">{call.target}</code>
      </div>
      <div className="dam-intact__facts">
        <time dateTime={call.createdAt} title={new Date(call.createdAt).toLocaleString(i18n.language)}>
          {relativeTime(call.createdAt, i18n.language)}
        </time>
        <span>{t(`integrations.activityPanel.kind.${call.kind}`, { defaultValue: call.kind })}</span>
        <span>
          {call.statusCode
            ? t('integrations.activityPanel.http', { code: call.statusCode })
            : t('integrations.activityPanel.noResponse')}
        </span>
        <span>{t('integrations.ms', { ms: call.durationMs })}</span>
      </div>
      {call.error && <p className="dam-intact__error">{call.error}</p>}
    </li>
  );
}

/** A connection's recent calls, newest first, with "Load more". Bodies and credentials are never recorded, and the panel says so. */
export function ActivityDrawer({ connection, onClose }: { connection: ConnectionDto | null; onClose: () => void }) {
  const { t } = useTranslation();
  const query = useActivity(connection?.id ?? null);
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  let body;
  if (query.isPending) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('integrations.loading')}</span>
        <div className="dam-stack dam-stack--fill" aria-hidden="true">
          <Skeleton height={48} />
          <Skeleton height={48} />
          <Skeleton height={48} />
        </div>
      </div>
    );
  } else if (query.isError) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          titleAs="p"
          title={isForbidden(query.error) ? t('integrations.forbidden.title') : t('integrations.activityPanel.loadFailed')}
          description={errorText(query.error) ?? t('integrations.loadFailedHint')}
          action={<Button onClick={() => void query.refetch()}>{t('integrations.retry')}</Button>}
        />
      </div>
    );
  } else if (items.length === 0) {
    body = (
      <EmptyState
        icon={<ClockCounterClockwise size={28} />}
        titleAs="p"
        title={t('integrations.activityPanel.empty.title')}
        description={t('integrations.activityPanel.empty.description')}
      />
    );
  } else {
    body = (
      <>
        <ol className="dam-intact" aria-label={t('integrations.activityPanel.list')}>
          {items.map((c) => (
            <Entry key={c.id} call={c} />
          ))}
        </ol>
        {query.hasNextPage && (
          <div className="dam-intact__more">
            <Button loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
              {t('integrations.activityPanel.more')}
            </Button>
          </div>
        )}
      </>
    );
  }

  return (
    <Drawer
      open={connection !== null}
      title={t('integrations.activityPanel.title', { name: connection?.name ?? '' })}
      closeLabel={t('integrations.close')}
      onClose={onClose}
    >
      <div className="dam-intdialog">
        <p className="dam-intnote dam-intnote--info">
          <Info size={18} aria-hidden="true" />
          <span>{t('integrations.activityPanel.note')}</span>
        </p>
        <div className="dam-intact__bar">
          <Button
            size="sm"
            variant="ghost"
            loading={query.isRefetching && !query.isFetchingNextPage}
            onClick={() => void query.refetch()}
          >
            <ArrowsClockwise size={16} aria-hidden="true" />
            {t('integrations.activityPanel.refresh')}
          </Button>
        </div>
        {body}
      </div>
    </Drawer>
  );
}
