// The console's local `Badge` shim in ui.ts has no tones; the design system's does.
import { Badge } from '@dam/ui-kit';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import {
  BookmarkSimple,
  Button,
  CardMenu,
  EmptyState,
  Modal,
  PageHeader,
  Plus,
  Skeleton,
  Trash,
  useToast,
  type MenuItem,
} from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import { useThumbnailMap } from '../library/assets';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { CollectionCover } from './CollectionCover';
import { NameCollectionDialog } from './NameCollectionDialog';
import { useCollections, useCreateStaticCollection, useDeleteCollection, type CollectionDto } from './collectionsApi';
import './collections.css';

const COVER_IMAGES = 3;

function CollectionCardSkeleton() {
  return (
    <li className="dam-collcard dam-collcard--skeleton" aria-hidden="true">
      <div className="dam-collcard__skelcover">
        <Skeleton height="100%" />
      </div>
      <div className="dam-collcard__body">
        <Skeleton height={16} width="65%" />
        <Skeleton height={20} width="45%" />
        <Skeleton height={12} width="55%" />
      </div>
    </li>
  );
}

/** Lists both collection kinds together (spec Step 1.8e: "collections (static + smart); saved searches" - a saved
 * search is just a named smart collection, ADR-0018, so there is no separate list for it). */
export function CollectionsPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const { push } = useToast();
  useDocumentTitle(t('collections.title'));
  const collectionsQuery = useCollections();
  const create = useCreateStaticCollection();
  const deleteCollection = useDeleteCollection();
  const [pendingDelete, setPendingDelete] = useState<CollectionDto | null>(null);
  const [creating, setCreating] = useState(false);

  const collections = collectionsQuery.data ?? [];
  // A static collection's cover comes from its first members; one batch request covers every card on the page.
  const thumbnails = useThumbnailMap(collections.flatMap((c) => (c.type === 'static' ? c.assetIds.slice(0, COVER_IMAGES) : [])));
  const coverOf = (c: CollectionDto) =>
    c.type === 'static'
      ? c.assetIds
          .slice(0, COVER_IMAGES)
          .map((id) => thumbnails[id])
          .filter((u): u is string => Boolean(u))
      : [];

  const menuFor = (c: CollectionDto): MenuItem[] => [
    { key: 'open', label: t('collections.open'), onSelect: () => navigate(`/collections/${c.id}`) },
    {
      key: 'delete',
      label: t('collections.deleteItem'),
      icon: <Trash size={16} aria-hidden="true" />,
      tone: 'danger',
      separatorBefore: true,
      onSelect: () => setPendingDelete(c),
    },
  ];

  const newButton = (
    <Button variant="primary" onClick={() => setCreating(true)}>
      <Plus size={16} weight="bold" aria-hidden="true" />
      {t('collections.new')}
    </Button>
  );

  return (
    <div className="dam-page dam-collections">
      <PageHeader
        title={t('collections.title')}
        description={
          collectionsQuery.isSuccess && collections.length > 0 ? t('collections.count', { count: collections.length }) : undefined
        }
        actions={newButton}
      />

      {collectionsQuery.isPending ? (
        <div role="status" aria-live="polite">
          <span className="dam-sr-only">{t('collections.loading')}</span>
          <ul className="dam-collgrid">
            {Array.from({ length: 6 }, (_, i) => (
              <CollectionCardSkeleton key={i} />
            ))}
          </ul>
        </div>
      ) : collectionsQuery.error ? (
        <div role="alert" className="dam-collections__error">
          <EmptyState
            title={t('collections.loadFailed')}
            description={t('collections.loadFailedHint')}
            action={<Button onClick={() => void collectionsQuery.refetch()}>{t('library.retry')}</Button>}
          />
        </div>
      ) : collections.length === 0 ? (
        <EmptyState
          icon={<BookmarkSimple size={28} />}
          title={t('collections.empty.title')}
          description={t('collections.empty.description')}
          action={newButton}
        />
      ) : (
        <ul className="dam-collgrid" aria-label={t('collections.title')}>
          {collections.map((c) => (
            <li key={c.id} className="dam-collcard">
              <CollectionCover images={coverOf(c)} smart={c.type === 'smart'} />
              <CardMenu label={t('collections.cardActions', { name: c.name })} actions={menuFor(c)} />
              <div className="dam-collcard__body">
                <Link to={`/collections/${c.id}`} className="dam-collcard__name">
                  {c.name}
                </Link>
                <div className="dam-collcard__badges">
                  <Badge tone={c.type === 'smart' ? 'info' : 'neutral'} size="sm">
                    {t(`collections.type.${c.type}`)}
                  </Badge>
                  <Badge tone={c.visibility === 'tenant' ? 'brand' : 'neutral'} size="sm">
                    {t(`collections.visibility.${c.visibility}`)}
                  </Badge>
                </div>
                <div className="dam-collcard__meta">
                  {c.type === 'static' ? t('collections.assetCount', { count: c.assetIds.length }) : t('collections.smartHint')}
                  <span aria-hidden="true"> · </span>
                  {t('collections.updatedRelative', { time: relativeTime(c.updatedAt, i18n.language) })}
                </div>
              </div>
            </li>
          ))}
        </ul>
      )}

      <NameCollectionDialog
        open={creating}
        title={t('collections.newTitle')}
        description={t('collections.newDescription')}
        confirmLabel={t('collections.create')}
        busy={create.isPending}
        onClose={() => setCreating(false)}
        onSave={(name) =>
          create.mutate(
            { name, assetIds: [] },
            {
              onSuccess: (created) => {
                setCreating(false);
                push({ title: t('collections.created', { name }), tone: 'success' });
                navigate(`/collections/${created.id}`);
              },
            },
          )
        }
      />

      <Modal open={pendingDelete !== null} title={t('collections.deleteConfirmTitle')} onClose={() => setPendingDelete(null)}>
        <div className="dam-stack dam-dialogform">
          <p>{t('collections.deleteConfirmBody', { name: pendingDelete?.name })}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setPendingDelete(null)}>
              {t('collections.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={deleteCollection.isPending}
              onClick={() =>
                deleteCollection.mutate(pendingDelete!.id, {
                  onSuccess: () => {
                    setPendingDelete(null);
                    push({ title: t('collections.deleted'), tone: 'success' });
                  },
                })
              }
            >
              {t('collections.delete')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
