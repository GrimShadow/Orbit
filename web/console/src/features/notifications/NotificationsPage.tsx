import { BellSlash, CheckCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import { Button, EmptyState, LoadingRegion, PageHeader, SegmentedControl, Skeleton, useToast } from '../../ui';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { NotificationItem } from './NotificationItem';
import { NotificationPreferences } from './NotificationPreferences';
import { useMarkAllRead, useNotifications, useUnreadCount } from './notificationsApi';
import './notifications.css';

type Tab = 'inbox' | 'preferences';
type Filter = 'all' | 'unread';

function ListSkeleton() {
  return (
    <div className="dam-notiflist dam-notiflist--roomy" aria-hidden="true">
      {[0, 1, 2, 3].map((i) => (
        <div key={i} className="dam-notif__skel dam-notif__skel--roomy">
          <Skeleton width={40} height={40} />
          <div className="dam-notif__skeltext">
            <Skeleton width="45%" height={16} />
            <Skeleton width="80%" height={12} />
          </div>
        </div>
      ))}
    </div>
  );
}

function Inbox({ filter }: { filter: Filter }) {
  const { t } = useTranslation();
  const query = useNotifications(filter === 'unread');
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  if (query.isPending)
    return (
      <LoadingRegion label={t('notifications.loading')}>
        <ListSkeleton />
      </LoadingRegion>
    );
  if (query.error)
    return (
      <div className="dam-notifs__error" role="alert">
        <span>{t('notifications.error')}</span>
        <Button variant="secondary" size="sm" onClick={() => void query.refetch()}>
          {t('notifications.retry')}
        </Button>
      </div>
    );
  if (items.length === 0)
    return filter === 'unread' ? (
      <EmptyState
        icon={<CheckCircle />}
        title={t('notifications.empty.caughtUp')}
        description={t('notifications.empty.unreadHint')}
      />
    ) : (
      <EmptyState
        icon={<BellSlash />}
        title={t('notifications.empty.noneTitle')}
        description={t('notifications.empty.noneHint')}
      />
    );

  return (
    <>
      <ul className="dam-notiflist dam-notiflist--roomy" aria-label={t('notifications.listLabel')}>
        {items.map((n) => (
          <NotificationItem key={n.id} notification={n} variant="roomy" />
        ))}
      </ul>
      {query.hasNextPage && (
        <div className="dam-notifs__more">
          <Button variant="secondary" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
            {t('notifications.loadMore')}
          </Button>
        </div>
      )}
    </>
  );
}

/** `/notifications` - my notification inbox (all or unread, paged) and, on a second tab, my delivery preferences. */
export function NotificationsPage() {
  const { t } = useTranslation();
  useDocumentTitle(t('notifications.title'));
  const { push } = useToast();
  const [params, setParams] = useSearchParams();
  const tab: Tab = params.get('tab') === 'preferences' ? 'preferences' : 'inbox';
  const filter: Filter = params.get('filter') === 'unread' ? 'unread' : 'all';
  const unread = useUnreadCount().data ?? 0;
  const markAll = useMarkAllRead();

  const setFilter = (v: string) =>
    setParams(
      (p) => {
        const next = new URLSearchParams(p);
        if (v === 'unread') next.set('filter', 'unread');
        else next.delete('filter');
        return next;
      },
      { replace: true },
    );

  const onMarkAll = () =>
    markAll.mutate(undefined, {
      onSuccess: ({ marked }) => {
        if (marked > 0) push({ title: t('notifications.markedAll', { count: marked }), tone: 'success' });
      },
      onError: () => push({ title: t('notifications.markAllFailed'), tone: 'error' }),
    });

  return (
    <div className="dam-page dam-notifs">
      <PageHeader title={t('notifications.title')} description={t('notifications.description')}>
        <nav className="dam-notifs__tabs" aria-label={t('notifications.tabsLabel')}>
          <Link to="/notifications" className="dam-notifs__tab" aria-current={tab === 'inbox' ? 'page' : undefined}>
            {t('notifications.tabs.inbox')}
            {unread > 0 && <span className="dam-notifs__count">{unread > 99 ? '99+' : unread}</span>}
          </Link>
          <Link
            to="/notifications?tab=preferences"
            className="dam-notifs__tab"
            aria-current={tab === 'preferences' ? 'page' : undefined}
          >
            {t('notifications.tabs.preferences')}
          </Link>
        </nav>
      </PageHeader>

      {tab === 'inbox' ? (
        <>
          <div className="dam-notifs__controls">
            <SegmentedControl
              label={t('notifications.filterLabel')}
              value={filter}
              onChange={setFilter}
              options={[
                { value: 'all', label: t('notifications.filter.all') },
                { value: 'unread', label: t('notifications.filter.unread') },
              ]}
            />
            <Button variant="secondary" disabled={unread === 0} loading={markAll.isPending} onClick={onMarkAll}>
              {t('notifications.markAllRead')}
            </Button>
          </div>
          <Inbox filter={filter} />
        </>
      ) : (
        <NotificationPreferences />
      )}
    </div>
  );
}
