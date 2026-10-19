import { Archive, Bell, CheckCircle, ClipboardText, Timer, type Icon } from '@phosphor-icons/react';
import { Button } from '../../ui';
import type { MouseEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { relativeTime } from '../../lib/relativeTime';
import { internalPath, useMarkRead, type AppNotification } from './notificationsApi';

/** One icon per server category. An unknown (future) category falls back to the bell, so a new rule never breaks the UI. */
const CATEGORY_ICONS: Record<string, Icon> = {
  workflow_task: ClipboardText,
  workflow_outcome: CheckCircle,
  sla: Timer,
  asset_lifecycle: Archive,
};

export function CategoryIcon({ category, size = 20 }: { category: string; size?: number }) {
  const Glyph = CATEGORY_ICONS[category] ?? Bell;
  return <Glyph size={size} weight="duotone" aria-hidden="true" />;
}

interface Props {
  notification: AppNotification;
  /** `compact` is the bell's panel; `roomy` is the inbox page (adds a per-item "Mark as read" button). */
  variant: 'compact' | 'roomy';
  /** Called when the item is opened (the panel closes itself). */
  onOpen?: () => void;
}

/** A notification as a row: category icon, title, body (clamped), relative time and an unread dot. The whole row is a
 * link to its console path; opening it marks it read first (optimistically), then navigates. */
export function NotificationItem({ notification: n, variant, onOpen }: Props) {
  const { t, i18n } = useTranslation();
  const markRead = useMarkRead();
  const href = internalPath(n.linkUrl);
  const when = new Date(n.createdAt).toLocaleString(i18n.language);

  const open = () => {
    if (!n.read) markRead.mutate(n);
    onOpen?.();
  };

  const content = (
    <>
      <span className="dam-notif__icon" aria-hidden="true">
        <CategoryIcon category={n.category} size={variant === 'roomy' ? 22 : 20} />
      </span>
      <span className="dam-notif__text">
        <span className="dam-notif__title">
          {!n.read && <span className="dam-sr-only">{t('notifications.unreadSr')} </span>}
          {n.title}
        </span>
        {n.body && <span className="dam-notif__body">{n.body}</span>}
        <time className="dam-notif__time" dateTime={n.createdAt} title={when}>
          {relativeTime(n.createdAt, i18n.language)}
        </time>
      </span>
      {!n.read && <span className="dam-notif__dot" aria-hidden="true" />}
    </>
  );

  return (
    <li className="dam-notif" data-variant={variant} data-unread={n.read ? undefined : 'true'} data-category={n.category}>
      {href ? (
        <Link to={href} className="dam-notif__main" onClick={open}>
          {content}
        </Link>
      ) : n.read ? (
        <div className="dam-notif__main">{content}</div>
      ) : (
        <button type="button" className="dam-notif__main" onClick={(e: MouseEvent) => (e.preventDefault(), open())}>
          {content}
        </button>
      )}
      {variant === 'roomy' && !n.read && (
        <Button
          variant="ghost"
          size="sm"
          className="dam-notif__markread"
          loading={markRead.isPending}
          aria-label={t('notifications.markReadNamed', { title: n.title })}
          onClick={() => markRead.mutate(n)}
        >
          {t('notifications.markRead')}
        </Button>
      )}
    </li>
  );
}
