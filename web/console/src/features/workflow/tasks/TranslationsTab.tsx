import { Translate } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Badge, Button, EmptyState, FileTypeIcon, ImageWithFallback, LoadingRegion, Skeleton } from '../../../ui';
import { relativeTime } from '../../../lib/relativeTime';
import { hasThumbnail } from '../../../lib/thumbnails';
import { useThumbnailLinks } from '../../asset/assetApi';
import { languageName } from '../../translation/languages';
import { reviewHref } from '../../translation/reviewLink';
import { useAiReviews } from '../../translation/translationApi';
import type { AiReviewItem } from '../../translation/translationTypes';
import './tasks.css';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function Row({ item, thumb }: { item: AiReviewItem; thumb: string | null | undefined }) {
  const { t, i18n } = useTranslation();
  const href = reviewHref(item.assetId, item.assetType);
  // The requester may come as a name or only as an id; an id tells the reader nothing.
  const by = item.requestedBy && !UUID.test(item.requestedBy) ? item.requestedBy : null;
  return (
    <li className="dam-taskrow dam-taskrow--review">
      <ImageWithFallback
        className="dam-taskrow__thumb"
        src={thumb}
        loading="lazy"
        fallback={
          <span className="dam-taskrow__thumb dam-taskrow__thumb--empty" aria-hidden="true">
            <FileTypeIcon type={item.assetType} size={22} />
          </span>
        }
      />
      <div className="dam-taskrow__main">
        <Link to={href} className="dam-taskrow__title">
          {item.title}
        </Link>
        <p className="dam-taskrow__step">
          {t('tasks.translations.line', {
            type: t(`library.type.${item.assetType}`, { defaultValue: item.assetType }),
            fields: item.fields.join(', '),
          })}
        </p>
        <div className="dam-taskrow__badges">
          <Badge tone="brand" size="sm">
            {t('tasks.translations.pending', { count: item.pending })}
          </Badge>
          <ul className="dam-taskrow__langs" aria-label={t('tasks.translations.languages')}>
            {item.languages.map((l) => (
              <li key={l}>
                <Badge size="sm" title={languageName(l, i18n.language)}>
                  {l.toUpperCase()}
                </Badge>
              </li>
            ))}
          </ul>
          <span className="dam-taskrow__assignees">
            {by ? `${t('tasks.translations.requestedBy', { name: by })} · ` : ''}
            <time dateTime={item.oldestAt} title={new Date(item.oldestAt).toLocaleString(i18n.language)}>
              {t('tasks.translations.waiting', { when: relativeTime(item.oldestAt, i18n.language) })}
            </time>
          </span>
        </div>
      </div>
      <div className="dam-taskrow__actions">
        <Link
          to={href}
          className="dam-btn dam-btn--primary dam-btn--sm"
          aria-label={t('tasks.translations.reviewLabel', { title: item.title })}
        >
          {t('tasks.translations.review')}
        </Link>
      </div>
    </li>
  );
}

/** The reviewer inbox for translations: assets with AI translations waiting to be accepted, edited or rejected. These are
 * not approval tasks (there is no workflow behind them; ADR-0036), so they have their own list. */
export function TranslationsTab() {
  const { t } = useTranslation();
  const query = useAiReviews();
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];
  const thumbs = useThumbnailLinks(items.filter((x) => hasThumbnail(x.assetType)).map((x) => x.assetId));

  if (query.isPending)
    return (
      <LoadingRegion label={t('tasks.translations.loading')}>
        <div className="dam-tasklist dam-tasklist--skeleton">
          {[0, 1, 2].map((i) => (
            <div key={i} className="dam-taskrow" aria-hidden="true">
              <Skeleton width={56} height={56} />
              <div className="dam-taskrow__main">
                <Skeleton width="40%" height={16} />
                <Skeleton width="25%" height={12} />
              </div>
            </div>
          ))}
        </div>
      </LoadingRegion>
    );
  if (query.error)
    return (
      <div className="dam-tasks__error" role="alert">
        <span>{t('tasks.translations.error')}</span>
        <Button variant="secondary" size="sm" onClick={() => void query.refetch()}>
          {t('tasks.retry')}
        </Button>
      </div>
    );
  if (items.length === 0)
    return (
      <EmptyState
        icon={<Translate />}
        title={t('tasks.translations.emptyTitle')}
        description={t('tasks.translations.emptyBody')}
        titleAs="h2"
      />
    );
  return (
    <>
      <ul className="dam-tasklist" aria-label={t('tasks.translations.list')}>
        {items.map((item) => (
          <Row key={item.assetId} item={item} thumb={thumbs.data?.[item.assetId]} />
        ))}
      </ul>
      {query.hasNextPage && (
        <div className="dam-tasks__more">
          <Button variant="secondary" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
            {t('tasks.loadMore')}
          </Button>
        </div>
      )}
    </>
  );
}
