// The console's local `Badge` shim in ui.ts has no tones; the design system's does.
import { Badge } from '@dam/ui-kit';
import { ArrowLeft, Lock, UsersThree } from '@phosphor-icons/react';
import { useCallback, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  BookmarkSimple,
  Button,
  DotsThree,
  EmptyState,
  IconButton,
  Input,
  MagnifyingGlass,
  Modal,
  PencilSimple,
  PopoverMenu,
  SegmentedControl,
  Skeleton,
  ThumbnailGrid,
  ThumbnailGridSkeleton,
  Trash,
  useToast,
} from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import { useThumbnailMap } from '../library/assets';
import { toAssetThumb } from '../library/assetThumb';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { useItemActions } from '../library/itemActions';
import {
  useCollection,
  useCollectionContentsInfinite,
  useDeleteCollection,
  useRenameCollection,
  useShareCollection,
} from './collectionsApi';
import './collections.css';

/** The title with an inline rename: a pencil swaps the heading for a labelled field (Enter saves, Escape cancels). */
function Title({ id, name }: { id: string; name: string }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const rename = useRenameCollection(id);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(name);

  if (!editing)
    return (
      <div className="dam-collhead__titlerow">
        <h1 className="dam-collhead__title">{name}</h1>
        <IconButton
          label={t('collections.renameCollection')}
          size="sm"
          onClick={() => {
            setDraft(name);
            setEditing(true);
          }}
        >
          <PencilSimple size={16} aria-hidden="true" />
        </IconButton>
      </div>
    );

  return (
    <>
      {/* The heading stays in the page (for screen readers and heading navigation) while its text is being edited. */}
      <h1 className="dam-sr-only">{name}</h1>
      <form
        className="dam-collhead__rename"
        onSubmit={(e) => {
          e.preventDefault();
          const next = draft.trim();
          if (!next) return;
          if (next === name) return setEditing(false);
          rename.mutate(next, {
            onSuccess: () => {
              setEditing(false);
              push({ title: t('collections.renamed'), tone: 'success' });
            },
          });
        }}
      >
        <Input
          label={t('collections.nameLabel')}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Escape') setEditing(false);
          }}
          // The field replaces the heading the person just clicked on, so it takes focus straight away.
          // eslint-disable-next-line jsx-a11y/no-autofocus
          autoFocus
        />
        <Button type="submit" variant="primary" loading={rename.isPending} disabled={!draft.trim()}>
          {t('collections.save')}
        </Button>
        <Button variant="ghost" onClick={() => setEditing(false)}>
          {t('collections.cancelRename')}
        </Button>
      </form>
    </>
  );
}

function OverflowMenu({ onDelete }: { onDelete: () => void }) {
  const { t } = useTranslation();
  const [anchor, setAnchor] = useState<HTMLButtonElement | null>(null);
  const [open, setOpen] = useState(false);
  const close = useCallback(() => setOpen(false), []);
  return (
    <>
      <IconButton
        ref={setAnchor}
        label={t('collections.moreActions')}
        variant="secondary"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        <DotsThree size={18} weight="bold" aria-hidden="true" />
      </IconButton>
      {open && (
        <PopoverMenu
          anchor={anchor}
          label={t('collections.moreActions')}
          onClose={close}
          items={[
            {
              key: 'delete',
              label: t('collections.deleteItem'),
              icon: <Trash size={16} aria-hidden="true" />,
              tone: 'danger',
              onSelect: onDelete,
            },
          ]}
        />
      )}
    </>
  );
}

export function CollectionDetailPage() {
  const { t, i18n } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { push } = useToast();
  const collectionQuery = useCollection(id!);
  const share = useShareCollection(id!);
  const deleteCollection = useDeleteCollection();
  const contentsQuery = useCollectionContentsInfinite(id!);
  const pages = contentsQuery.data?.pages;
  const hits = useMemo(() => pages?.flatMap((p) => p.hits) ?? [], [pages]);
  const thumbnails = useThumbnailMap(hits.map((h) => h.id));
  const itemActions = useItemActions();
  const hitById = (assetId: string) => hits.find((h) => h.id === assetId);
  const [confirmDelete, setConfirmDelete] = useState(false);
  useDocumentTitle(collectionQuery.data?.name ?? t('collections.title'));

  if (collectionQuery.isPending)
    return (
      <div className="dam-page" role="status" aria-live="polite">
        <span className="dam-sr-only">{t('collections.loading')}</span>
        <div className="dam-collhead">
          <Skeleton height={16} width={140} />
          <Skeleton height={32} width={320} />
          <Skeleton height={22} width={220} />
        </div>
        <ThumbnailGridSkeleton count={5} label={t('collections.loading')} />
      </div>
    );
  if (collectionQuery.error || !collectionQuery.data)
    return (
      <div role="alert" className="dam-page dam-page--narrow">
        <EmptyState
          title={t('collections.loadFailed')}
          description={t('collections.loadFailedHint')}
          action={
            <Link to="/collections" className="dam-btn dam-btn--secondary">
              {t('collections.backToCollections')}
            </Link>
          }
        />
      </div>
    );

  const collection = collectionQuery.data;
  const smart = collection.type === 'smart';
  return (
    <div className="dam-page">
      <header className="dam-collhead">
        <Link to="/collections" className="dam-collhead__back">
          <ArrowLeft size={14} weight="bold" aria-hidden="true" />
          {t('collections.backToCollections')}
        </Link>
        <div className="dam-collhead__row">
          <div className="dam-collhead__text">
            <Title key={collection.name} id={collection.id} name={collection.name} />
            <div className="dam-collhead__meta">
              <Badge tone={smart ? 'info' : 'neutral'}>{t(`collections.type.${collection.type}`)}</Badge>
              <Badge tone={collection.visibility === 'tenant' ? 'brand' : 'neutral'}>
                {t(`collections.visibility.${collection.visibility}`)}
              </Badge>
              {!smart && <span>{t('collections.assetCount', { count: collection.assetIds.length })}</span>}
              <span aria-hidden="true">·</span>
              <span>{t('collections.updatedRelative', { time: relativeTime(collection.updatedAt, i18n.language) })}</span>
            </div>
            {smart && <p className="dam-hint">{t('collections.smartDescription')}</p>}
          </div>
          <div className="dam-collhead__actions">
            <SegmentedControl
              label={t('collections.visibility')}
              value={collection.visibility}
              onChange={(v) => {
                if (v !== collection.visibility) share.mutate(v as 'private' | 'tenant');
              }}
              options={[
                {
                  value: 'private',
                  label: t('collections.visibility.private'),
                  icon: <Lock size={14} weight="bold" aria-hidden="true" />,
                },
                {
                  value: 'tenant',
                  label: t('collections.visibility.tenant'),
                  icon: <UsersThree size={14} weight="bold" aria-hidden="true" />,
                },
              ]}
            />
            <OverflowMenu onDelete={() => setConfirmDelete(true)} />
          </div>
        </div>
      </header>

      {contentsQuery.isPending ? (
        <ThumbnailGridSkeleton count={5} label={t('collections.loading')} />
      ) : contentsQuery.error ? (
        <div role="alert">
          <EmptyState
            title={t('collections.contentsFailed')}
            description={t('collections.loadFailedHint')}
            action={<Button onClick={() => void contentsQuery.refetch()}>{t('library.retry')}</Button>}
            titleAs="h2"
          />
        </div>
      ) : hits.length === 0 ? (
        <EmptyState
          icon={smart ? <MagnifyingGlass size={28} /> : <BookmarkSimple size={28} />}
          title={smart ? t('collections.contentsEmptySmart.title') : t('collections.contentsEmptyStatic.title')}
          description={smart ? t('collections.contentsEmptySmart.description') : t('collections.contentsEmptyStatic.description')}
          action={
            !smart && (
              <Link to="/library" className="dam-btn dam-btn--secondary">
                {t('collections.goToLibrary')}
              </Link>
            )
          }
        />
      ) : (
        <>
          <ThumbnailGrid
            items={hits.map((h) => toAssetThumb(t, i18n.language, h, thumbnails[h.id]))}
            label={t('collections.contentsLabel')}
            onOpen={(assetId) => navigate(`/library/assets/${assetId}`)}
            cardActions={(assetId) => {
              const hit = hitById(assetId);
              return hit ? itemActions.assetMenu(hit) : [];
            }}
            onMenuOpen={(assetId) => {
              const hit = hitById(assetId);
              if (hit) itemActions.prefetchAsset(hit);
            }}
            menuLabel={(title) => t('library.card.actionsFor', { title })}
          />
          {contentsQuery.hasNextPage && (
            <div className="dam-collection__more">
              <span className="dam-hint" role="status">
                {t('library.showingMore', { count: hits.length })}
              </span>
              <Button
                variant="secondary"
                onClick={() => void contentsQuery.fetchNextPage()}
                loading={contentsQuery.isFetchingNextPage}
              >
                {t('library.loadMore')}
              </Button>
            </div>
          )}
        </>
      )}

      <Modal open={confirmDelete} title={t('collections.deleteConfirmTitle')} onClose={() => setConfirmDelete(false)}>
        <div className="dam-stack dam-dialogform">
          <p>{t('collections.deleteConfirmBody', { name: collection.name })}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setConfirmDelete(false)}>
              {t('collections.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={deleteCollection.isPending}
              onClick={() =>
                deleteCollection.mutate(collection.id, {
                  onSuccess: () => {
                    push({ title: t('collections.deleted'), tone: 'success' });
                    navigate('/collections');
                  },
                })
              }
            >
              {t('collections.delete')}
            </Button>
          </div>
        </div>
      </Modal>
      {itemActions.dialogs}
    </div>
  );
}
