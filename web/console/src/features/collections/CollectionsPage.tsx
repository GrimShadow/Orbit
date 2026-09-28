import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Badge, Button, LoadingRegion, Modal, Skeleton } from '../../ui';
import { useCollections, useDeleteCollection, type CollectionDto } from './collectionsApi';
import './collections.css';

/** Lists both collection kinds together (spec Step 1.8e: "collections (static + smart); saved searches" - a saved
 * search is just a named smart collection, ADR-0018, so there is no separate list for it). */
export function CollectionsPage() {
  const { t } = useTranslation();
  const collectionsQuery = useCollections();
  const deleteCollection = useDeleteCollection();
  const [pendingDelete, setPendingDelete] = useState<CollectionDto | null>(null);

  if (collectionsQuery.isPending)
    return (
      <LoadingRegion>
        <Skeleton height={160} />
      </LoadingRegion>
    );
  if (collectionsQuery.error)
    return (
      <div role="alert" className="dam-page dam-stack">
        <p>{t('collections.loadFailed')}</p>
        <Button onClick={() => void collectionsQuery.refetch()}>{t('library.retry')}</Button>
      </div>
    );

  const collections = collectionsQuery.data;
  return (
    <div className="dam-page dam-stack">
      <h1>{t('collections.title')}</h1>
      {collections.length === 0 ? (
        <p className="dam-empty">{t('collections.empty')}</p>
      ) : (
        <ul className="dam-stack" aria-label={t('collections.title')}>
          {collections.map((c) => (
            <li key={c.id} className="dam-collections__row">
              <Link to={`/collections/${c.id}`}>{c.name}</Link>
              <Badge>{t(`collections.type.${c.type}`)}</Badge>
              <Badge>{t(`collections.visibility.${c.visibility}`)}</Badge>
              <Button variant="danger" size="sm" onClick={() => setPendingDelete(c)}>
                {t('collections.delete')}
              </Button>
            </li>
          ))}
        </ul>
      )}

      <Modal open={pendingDelete !== null} title={t('collections.deleteConfirmTitle')} onClose={() => setPendingDelete(null)}>
        <div className="dam-stack">
          <p>{t('collections.deleteConfirmBody', { name: pendingDelete?.name })}</p>
          <Button
            variant="danger"
            loading={deleteCollection.isPending}
            onClick={() => deleteCollection.mutate(pendingDelete!.id, { onSuccess: () => setPendingDelete(null) })}
          >
            {t('collections.delete')}
          </Button>
          <Button variant="ghost" onClick={() => setPendingDelete(null)}>
            {t('collections.cancel')}
          </Button>
        </div>
      </Modal>
    </div>
  );
}
