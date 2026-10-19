import {
  ArrowBendDownRight,
  ArrowsClockwise,
  ArrowCounterClockwise,
  ChatCircle,
  CheckCircle,
  Circle,
  ClipboardText,
  LockKey,
  PencilSimple,
  Sparkle,
  Stack,
  Timer,
  WarningCircle,
  type Icon,
} from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { relativeTime } from '../../../lib/relativeTime';
import { Button, EmptyState, LoadingRegion, Skeleton } from '../../../ui';
import { useActivity, type ActivityItem } from '../commentsApi';
import { plainText } from './mentionText';
import './comments.css';

/** One glyph per kind the server sends; an unknown (future) kind falls back to a dot, so a new rule never breaks the feed. */
const KIND_ICONS: Record<string, Icon> = {
  comment: ChatCircle,
  reply: ArrowBendDownRight,
  resolved: CheckCircle,
  reopened: ArrowCounterClockwise,
  created: Sparkle,
  version_added: Stack,
  status_changed: ArrowsClockwise,
  metadata_updated: PencilSimple,
  workflow_decision: ClipboardText,
  workflow_overdue: Timer,
  legal_hold: LockKey,
};

function ActivityRow({ item }: { item: ActivityItem }) {
  const { t, i18n } = useTranslation();
  const [, setParams] = useSearchParams();
  const Glyph = KIND_ICONS[item.kind] ?? Circle;
  const exact = new Date(item.occurredAt).toLocaleString(i18n.language);
  const open = () =>
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.set('tab', 'comments');
        next.set('comment', item.commentId!);
        return next;
      },
      { replace: true },
    );
  const text = (
    <>
      <span className="dam-activity__actor">{item.actorName}</span> {item.summary}
    </>
  );
  return (
    <li className="dam-activity__item" data-kind={item.kind}>
      <span className="dam-activity__icon" aria-hidden="true">
        <Glyph size={16} weight="bold" />
      </span>
      <div className="dam-activity__main">
        {item.commentId ? (
          <button type="button" className="dam-activity__link" onClick={open}>
            {text}
          </button>
        ) : (
          <p className="dam-activity__text">{text}</p>
        )}
        {item.detail && <p className="dam-activity__detail">{plainText(item.detail)}</p>}
        <time className="dam-activity__time" dateTime={item.occurredAt} title={exact}>
          {relativeTime(item.occurredAt, i18n.language)}
          {item.versionNo != null && (item.kind === 'comment' || item.kind === 'reply') && (
            <> {t('comments.activity.onVersion', { n: item.versionNo })}</>
          )}
        </time>
      </div>
    </li>
  );
}

/** What happened to this asset, newest first: comments, replies, resolutions, uploads, status and workflow changes. */
export function ActivityTab({ assetId }: { assetId: string }) {
  const { t } = useTranslation();
  const q = useActivity(assetId);
  const items = q.data?.pages.flatMap((p) => p.items) ?? [];

  if (q.isPending)
    return (
      <LoadingRegion label={t('comments.activity.loading')}>
        <div className="dam-activity__loading">
          {[0, 1, 2, 3].map((i) => (
            <div key={i} className="dam-activity__skeleton">
              <Skeleton width={28} height={28} />
              <div>
                <Skeleton width="80%" height={14} />
                <Skeleton width="30%" height={12} />
              </div>
            </div>
          ))}
        </div>
      </LoadingRegion>
    );
  if (q.error)
    return (
      <div role="alert" className="dam-comments__error">
        <WarningCircle size={18} weight="bold" aria-hidden="true" />
        <span>{t('comments.activity.loadFailed')}</span>
        <Button size="sm" onClick={() => void q.refetch()}>
          {t('comments.retry')}
        </Button>
      </div>
    );
  if (items.length === 0)
    return (
      <EmptyState
        icon={<Circle />}
        titleAs="p"
        title={t('comments.activity.empty')}
        description={t('comments.activity.emptyHint')}
      />
    );

  return (
    <div className="dam-activity">
      <ol className="dam-activity__list" aria-label={t('comments.activity.listLabel')}>
        {items.map((item) => (
          <ActivityRow key={item.id} item={item} />
        ))}
      </ol>
      {q.hasNextPage && (
        <Button variant="secondary" size="sm" loading={q.isFetchingNextPage} onClick={() => void q.fetchNextPage()}>
          {t('comments.activity.loadMore')}
        </Button>
      )}
    </div>
  );
}
