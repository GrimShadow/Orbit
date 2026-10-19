import { Bell, Gear } from '@phosphor-icons/react';
import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useLocation } from 'react-router-dom';
import { Button, Skeleton } from '../../ui';
import { NotificationItem } from './NotificationItem';
import { useLatestNotifications, useMarkAllRead, useUnreadCount } from './notificationsApi';
import './notifications.css';

const PANEL_ITEMS = 8;
const PILL_CAP = 99;

/** The bell in the top bar: an unread-count pill (hidden at zero, `99+` beyond 99) and a popover with the latest
 * notifications. Opens on click; Escape closes it and returns focus to the bell; so does clicking or tabbing away. */
export function NotificationBell() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const location = useLocation();
  const panelId = useId();
  // The panel is open only at the location it was opened at, so navigating anywhere closes it.
  const where = location.pathname + location.search;
  const [openAt, setOpenAt] = useState<string | null>(null);
  const open = openAt === where;
  const setOpen = useCallback(
    (next: boolean | ((o: boolean) => boolean)) =>
      setOpenAt((cur) => ((typeof next === 'function' ? next(cur === where) : next) ? where : null)),
    [where],
  );
  const wrap = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);

  const count = useUnreadCount().data ?? 0;
  const latest = useLatestNotifications(PANEL_ITEMS, open);
  const markAll = useMarkAllRead();

  // New notifications arrived (the count went up): refresh whatever list is on screen.
  const previous = useRef<number | undefined>(undefined);
  useEffect(() => {
    if (previous.current !== undefined && count > previous.current) {
      void qc.invalidateQueries({ queryKey: ['notifications', 'list'] });
    }
    previous.current = count;
  }, [count, qc]);

  useEffect(() => {
    if (!open) return;
    panel.current?.focus();
    const outside = (e: Event) => {
      if (!wrap.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpen(false);
        button.current?.focus();
      }
    };
    document.addEventListener('mousedown', outside);
    document.addEventListener('focusin', outside);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', outside);
      document.removeEventListener('focusin', outside);
      document.removeEventListener('keydown', onKey);
    };
  }, [open, setOpen]);

  const label = count > 0 ? t('notifications.bell.labelUnread', { count }) : t('notifications.bell.label');
  const items = latest.data?.items ?? [];

  return (
    <div className="dam-bell" ref={wrap}>
      <button
        ref={button}
        type="button"
        className="dam-topbar__iconbtn dam-bell__button"
        aria-label={label}
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        aria-haspopup="dialog"
        onClick={() => setOpen((o) => !o)}
      >
        <Bell size={20} weight={count > 0 ? 'fill' : 'regular'} aria-hidden="true" />
        {count > 0 && (
          <span className="dam-bell__pill" aria-hidden="true">
            {count > PILL_CAP ? `${PILL_CAP}+` : count}
          </span>
        )}
      </button>
      {open && (
        <div
          id={panelId}
          ref={panel}
          role="dialog"
          aria-label={t('notifications.panel.title')}
          tabIndex={-1}
          className="dam-bell__panel"
        >
          <div className="dam-bell__head">
            <h2 className="dam-bell__title">{t('notifications.panel.title')}</h2>
            <Button variant="ghost" size="sm" disabled={count === 0} loading={markAll.isPending} onClick={() => markAll.mutate()}>
              {t('notifications.markAllRead')}
            </Button>
            <Link
              to="/notifications?tab=preferences"
              className="dam-bell__gear"
              onClick={() => setOpen(false)}
              aria-label={t('notifications.panel.settings')}
              title={t('notifications.panel.settings')}
            >
              <Gear size={18} aria-hidden="true" />
            </Link>
          </div>

          <div className="dam-bell__body">
            {latest.isPending ? (
              <div role="status" aria-live="polite">
                <span className="dam-sr-only">{t('notifications.loading')}</span>
                <div className="dam-bell__skeleton" aria-hidden="true">
                  {[0, 1, 2].map((i) => (
                    <div key={i} className="dam-notif__skel">
                      <Skeleton width={32} height={32} />
                      <div className="dam-notif__skeltext">
                        <Skeleton width="70%" height={14} />
                        <Skeleton width="90%" height={12} />
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            ) : latest.error ? (
              <div className="dam-bell__note" role="alert">
                <span>{t('notifications.error')}</span>
                <Button variant="secondary" size="sm" onClick={() => void latest.refetch()}>
                  {t('notifications.retry')}
                </Button>
              </div>
            ) : items.length === 0 ? (
              <div className="dam-bell__empty">
                <Bell size={28} weight="duotone" aria-hidden="true" />
                <p className="dam-bell__emptytitle">{t('notifications.empty.caughtUp')}</p>
                <p className="dam-bell__emptyhint">{t('notifications.empty.caughtUpHint')}</p>
              </div>
            ) : (
              <ul className="dam-notiflist" aria-label={t('notifications.listLabel')}>
                {items.map((n) => (
                  <NotificationItem key={n.id} notification={n} variant="compact" onOpen={() => setOpen(false)} />
                ))}
              </ul>
            )}
          </div>

          <div className="dam-bell__foot">
            <Link to="/notifications" className="dam-bell__all" onClick={() => setOpen(false)}>
              {t('notifications.seeAll')}
            </Link>
          </div>
        </div>
      )}
    </div>
  );
}
