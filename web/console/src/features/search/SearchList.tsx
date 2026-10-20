import { Badge } from '@dam/ui-kit';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { CardMenu, FileTypeIcon, ImageWithFallback, Table, type Column } from '../../ui';
import type { useItemActions } from '../library/itemActions';
import { statusTone } from '../library/statusTone';
import { relativeTime } from '../../lib/relativeTime';
import { HitMatchInfo } from './smart/MatchInfo';
import type { SearchHitDto } from './searchApi';

/** The Search screen's list view: the library list's look (40px thumbnail, title link, status, updated, "..." menu) with the
 * "why it matched" chips and spoken moments under the title. */
export function SearchList({
  hits,
  thumbnails,
  actions,
  quietText,
}: {
  hits: SearchHitDto[];
  thumbnails: Record<string, string | null | undefined>;
  actions: ReturnType<typeof useItemActions>;
  quietText: boolean;
}) {
  const { t, i18n } = useTranslation();
  const columns: Column<SearchHitDto>[] = [
    {
      key: 'title',
      header: t('library.columnTitle'),
      render: (h) => (
        <div className="dam-assetcell dam-assetcell--top">
          <ImageWithFallback
            className="dam-table__thumb"
            src={thumbnails[h.id]}
            loading="lazy"
            fallback={
              <span className="dam-table__thumb dam-assetcell__fallback" aria-hidden="true">
                <FileTypeIcon type={h.type} size={18} />
              </span>
            }
          />
          <div className="dam-searchlist__main">
            <Link to={`/library/assets/${h.id}`} className="dam-assetcell__title">
              {h.title}
            </Link>
            <HitMatchInfo hit={h} quietText={quietText} />
          </div>
        </div>
      ),
    },
    { key: 'type', header: t('library.columnType'), className: 'dam-col--hide-sm', render: (h) => t(`library.type.${h.type}`) },
    {
      key: 'status',
      header: t('library.columnStatus'),
      render: (h) => (
        <Badge tone={statusTone(h.status)} size="sm">
          {t(`library.status.${h.status}`)}
        </Badge>
      ),
    },
    {
      key: 'updatedAt',
      header: t('library.columnUpdated'),
      className: 'dam-col--hide-sm',
      render: (h) => (
        <time dateTime={h.updatedAt} title={new Date(h.updatedAt).toLocaleString(i18n.language)}>
          {relativeTime(h.updatedAt, i18n.language)}
        </time>
      ),
    },
    {
      key: 'actions',
      header: <span className="dam-sr-only">{t('library.columnActions')}</span>,
      className: 'dam-col--actions',
      render: (h) => (
        <CardMenu
          label={t('library.card.actionsFor', { title: h.title })}
          actions={actions.assetMenu(h)}
          onOpen={() => actions.prefetchAsset(h)}
        />
      ),
    },
  ];
  return <Table columns={columns} rows={hits} getRowId={(h) => h.id} caption={t('search.resultsLabel')} />;
}
