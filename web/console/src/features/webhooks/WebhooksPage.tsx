import { Lock } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { Button, EmptyState, PageHeader, Skeleton, Tabs, WarningCircle, type TabItem } from '../../ui';
import { useMe } from '../../api/me';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { ChannelsTab } from './ChannelsTab';
import { DeliveriesTab } from './DeliveriesTab';
import { WebhooksTab } from './WebhooksTab';
import './webhooks.css';

type TabId = 'webhooks' | 'deliveries' | 'channels';

/** `/admin/webhooks`: signed webhooks and their delivery log (`webhooks.manage`), and chat channels (`notifications.manage`). */
export function WebhooksPage() {
  const { t } = useTranslation();
  useDocumentTitle(t('webhooks.title'));
  const me = useMe();
  const [params, setParams] = useSearchParams();
  const permissions = me.data?.permissions ?? [];
  const canHooks = permissions.includes('webhooks.manage');
  const canChannels = permissions.includes('notifications.manage');

  const select = (id: string, extra: Record<string, string> = {}) =>
    // Leaving a tab drops its filters and any open delivery, so a link always shows what it says.
    setParams({ tab: id, ...extra });

  const tabs: TabItem[] = [];
  if (canHooks) {
    tabs.push({
      id: 'webhooks',
      label: t('webhooks.tabs.webhooks'),
      content: <WebhooksTab onViewDeliveries={(webhook) => select('deliveries', { webhook })} />,
    });
    tabs.push({ id: 'deliveries', label: t('webhooks.tabs.deliveries'), content: <DeliveriesTab /> });
  }
  if (canChannels) tabs.push({ id: 'channels', label: t('webhooks.tabs.channels'), content: <ChannelsTab /> });

  const wanted = params.get('tab');
  const active = (tabs.find((x) => x.id === wanted) ?? tabs[0])?.id;

  return (
    <div className="dam-page dam-whpage">
      <PageHeader title={t('webhooks.title')} description={t('webhooks.description')} />
      {me.isPending ? (
        <div role="status" aria-live="polite" className="dam-stack dam-stack--fill">
          <span className="dam-sr-only">{t('webhooks.loading')}</span>
          <Skeleton height={36} width="50%" />
          <Skeleton height={140} />
        </div>
      ) : me.isError ? (
        <div role="alert">
          <EmptyState
            icon={<WarningCircle size={28} />}
            title={t('webhooks.loadFailed')}
            description={t('webhooks.loadFailedHint')}
            action={<Button onClick={() => void me.refetch()}>{t('webhooks.retry')}</Button>}
          />
        </div>
      ) : tabs.length === 0 ? (
        <EmptyState
          icon={<Lock size={28} />}
          title={t('webhooks.forbidden.title')}
          description={t('webhooks.forbidden.description')}
        />
      ) : (
        <Tabs label={t('webhooks.title')} tabs={tabs} value={active as TabId} onChange={(id) => select(id)} />
      )}
    </div>
  );
}
