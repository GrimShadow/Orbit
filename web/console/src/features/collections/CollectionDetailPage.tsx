import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Badge, Button, Input, LoadingRegion, Modal, Select, Skeleton, ThumbnailGrid } from '../../ui';
import { useThumbnailLinks } from '../asset/assetApi';
import { toAssetThumb } from '../library/assetThumb';
import {
  useCollection,
  useCollectionContents,
  useDeleteCollection,
  useRenameCollection,
  useShareCollection,
} from './collectionsApi';
import './collections.css';

function RenameForm({ id, initialName }: { id: string; initialName: string }) {
  const { t } = useTranslation();
  const rename = useRenameCollection(id);
  const [name, setName] = useState(initialName);

  return (
    <form
      className="dam-upload__form"
      onSubmit={(e) => {
        e.preventDefault();
        if (name.trim()) rename.mutate(name.trim());
      }}
    >
      <Input label={t('collections.nameLabel')} value={name} onChange={(e) => setName(e.target.value)} />
      <Button type="submit" variant="secondary" loading={rename.isPending} disabled={!name.trim()}>
        {t('collections.rename')}
      </Button>
    </form>
  );
}

export function CollectionDetailPage() {
  const { t, i18n } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const collectionQuery = useCollection(id!);
  const share = useShareCollection(id!);
  const deleteCollection = useDeleteCollection();
  const [cursor, setCursor] = useState<string | undefined>();
  const contentsQuery = useCollectionContents(id!, cursor);
  const thumbnailLinks = useThumbnailLinks(contentsQuery.data?.hits.map((h) => h.id) ?? []);
  const [confirmDelete, setConfirmDelete] = useState(false);

  if (collectionQuery.isPending)
    return (
      <LoadingRegion>
        <Skeleton height={200} />
      </LoadingRegion>
    );
  if (collectionQuery.error || !collectionQuery.data)
    return (
      <div role="alert" className="dam-page dam-stack">
        <p>{t('collections.loadFailed')}</p>
        <Link to="/collections">{t('collections.backToCollections')}</Link>
      </div>
    );

  const collection = collectionQuery.data;
  return (
    <div className="dam-page dam-stack">
      <Link to="/collections">{t('collections.backToCollections')}</Link>
      <div className="dam-asset-header">
        <h1>{collection.name}</h1>
        <Badge>{t(`collections.type.${collection.type}`)}</Badge>
      </div>

      <RenameForm id={collection.id} initialName={collection.name} />

      <Select
        label={t('collections.visibility')}
        value={collection.visibility}
        onChange={(e) => share.mutate(e.target.value as 'private' | 'tenant')}
        options={[
          { value: 'private', label: t('collections.visibility.private') },
          { value: 'tenant', label: t('collections.visibility.tenant') },
        ]}
      />

      <Button variant="danger" onClick={() => setConfirmDelete(true)}>
        {t('collections.delete')}
      </Button>

      {contentsQuery.isPending ? (
        <LoadingRegion>
          <Skeleton height={160} />
        </LoadingRegion>
      ) : contentsQuery.error ? (
        <p className="dam-hint">{t('collections.contentsFailed')}</p>
      ) : contentsQuery.data.hits.length === 0 ? (
        <p className="dam-empty">{t('collections.contentsEmpty')}</p>
      ) : (
        <>
          <ThumbnailGrid
            items={contentsQuery.data.hits.map((h) => toAssetThumb(t, i18n.language, h, thumbnailLinks.data?.[h.id]))}
            label={t('collections.contentsLabel')}
            onOpen={(assetId) => navigate(`/library/assets/${assetId}`)}
          />
          {contentsQuery.data.nextCursor && (
            <Button
              variant="secondary"
              onClick={() => setCursor(contentsQuery.data.nextCursor!)}
              loading={contentsQuery.isFetching}
            >
              {t('library.next')}
            </Button>
          )}
        </>
      )}

      <Modal open={confirmDelete} title={t('collections.deleteConfirmTitle')} onClose={() => setConfirmDelete(false)}>
        <div className="dam-stack">
          <p>{t('collections.deleteConfirmBody', { name: collection.name })}</p>
          <Button
            variant="danger"
            loading={deleteCollection.isPending}
            onClick={() => deleteCollection.mutate(collection.id, { onSuccess: () => navigate('/collections') })}
          >
            {t('collections.delete')}
          </Button>
          <Button variant="ghost" onClick={() => setConfirmDelete(false)}>
            {t('collections.cancel')}
          </Button>
        </div>
      </Modal>
    </div>
  );
}
