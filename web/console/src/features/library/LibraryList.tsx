// The console's local `Badge` shim in ui.ts has no tones; the design system's does.
import { Badge } from '@dam/ui-kit';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { CardMenu, FileTypeIcon, Table, type Column } from '../../ui';
import type { useItemActions } from './itemActions';
import type { AssetDto } from './assets';
import { relativeTime } from '../../lib/relativeTime';
import { statusTone } from './statusTone';

/** The library's list view: a real table with a 40px thumbnail beside a keyboard-reachable title link, the status as a
 * toned badge, a relative "updated" time (the exact moment on hover) and the same "..." menu a card has. */
export function LibraryList({
  assets,
  thumbnails,
  loading,
  selectedIds,
  onToggleSelect,
  actions,
  emptyMessage,
}: {
  assets: AssetDto[];
  thumbnails: Record<string, string | null | undefined>;
  loading?: boolean;
  selectedIds: string[];
  onToggleSelect: (id: string, selected: boolean) => void;
  actions: ReturnType<typeof useItemActions>;
  emptyMessage: string;
}) {
  const { t, i18n } = useTranslation();
  const columns: Column<AssetDto>[] = [
    {
      key: 'title',
      header: t('library.columnTitle'),
      render: (a) => (
        <div className="dam-assetcell">
          {thumbnails[a.id] ? (
            <img className="dam-table__thumb" src={thumbnails[a.id]!} alt="" loading="lazy" />
          ) : (
            <span className="dam-table__thumb dam-assetcell__fallback" aria-hidden="true">
              <FileTypeIcon type={a.type} size={18} />
            </span>
          )}
          <Link to={`/library/assets/${a.id}`} className="dam-assetcell__title">
            {a.title}
          </Link>
        </div>
      ),
    },
    { key: 'type', header: t('library.columnType'), className: 'dam-col--hide-sm', render: (a) => t(`library.type.${a.type}`) },
    {
      key: 'status',
      header: t('library.columnStatus'),
      render: (a) => (
        <Badge tone={statusTone(a.status)} size="sm">
          {t(`library.status.${a.status}`)}
        </Badge>
      ),
    },
    {
      key: 'updatedAt',
      header: t('library.columnUpdated'),
      className: 'dam-col--hide-sm',
      render: (a) => (
        <time dateTime={a.updatedAt} title={new Date(a.updatedAt).toLocaleString(i18n.language)}>
          {relativeTime(a.updatedAt, i18n.language)}
        </time>
      ),
    },
    {
      key: 'actions',
      header: <span className="dam-sr-only">{t('library.columnActions')}</span>,
      className: 'dam-col--actions',
      render: (a) => (
        <CardMenu
          label={t('library.card.actionsFor', { title: a.title })}
          actions={actions.assetMenu(a)}
          onOpen={() => actions.prefetchAsset(a)}
        />
      ),
    },
  ];
  return (
    <Table
      columns={columns}
      rows={assets}
      getRowId={(a) => a.id}
      caption={t('library.assetsLabel')}
      emptyMessage={emptyMessage}
      loading={loading}
      selectedIds={selectedIds}
      onToggleSelect={onToggleSelect}
      selectLabel={(a) => t('library.selectAsset', { title: a.title })}
    />
  );
}
