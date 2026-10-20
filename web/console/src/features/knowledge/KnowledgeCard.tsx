import { ArrowsClockwise, Books, CaretDown, CaretRight, ListBullets, Warning } from '@phosphor-icons/react';
import { useId, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useMe } from '../../api/me';
import { relativeTime } from '../../lib/relativeTime';
import { Badge, Button, Card, EmptyState, Modal, PencilSimple, Plus, Skeleton, Trash, useToast } from '../../ui';
import type { AssetDto } from '../asset/assetApi';
import { useNearViewport } from '../insights/useNearViewport';
import { languageLabel } from '../translation/languages';
import { ChapterEditor } from './ChapterEditor';
import { ChapterTree } from './ChapterTree';
import { DocumentMapDialog } from './DocumentMapDialog';
import {
  errorReason,
  isForbidden,
  useAssetKnowledge,
  useDeleteKnowledgeDocument,
  useKnowledgeDocument,
  useReindexChapters,
} from './knowledgeApi';
import { formatYears } from './knowledgeModel';
import { isKnowledgeAssetType, type KnowledgeDocumentDto } from './knowledgeTypes';
import './knowledge.css';

type CardAsset = Pick<AssetDto, 'id' | 'title' | 'type'>;

/** The "Knowledge" card on the asset page's Details tab, for manuals, documents and HTML5 bundles: whether the asset is mapped as a
 * knowledge document (and to which model, variant, years and language), its chapters, and - with `knowledge.manage` - the ways to
 * map, edit, unmap, re-index and correct chapters. Other asset types never see it. */
export function KnowledgeCard({ asset }: { asset: CardAsset }) {
  const me = useMe();
  if (!isKnowledgeAssetType(asset.type)) return null;
  return <Loaded asset={asset} canManage={!!me.data?.permissions.includes('knowledge.manage')} />;
}

function Loaded({ asset, canManage }: { asset: CardAsset; canManage: boolean }) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const ref = useRef<HTMLDivElement>(null);
  // Loaded after the page itself, once the card is near the screen - the asset never waits on it.
  const near = useNearViewport(ref);
  const titleId = useId();
  const listId = useId();
  const lookup = useAssetKnowledge(asset.id, near);
  const doc = lookup.data ?? undefined;
  const detail = useKnowledgeDocument(doc?.id);
  const reindex = useReindexChapters();
  const unmap = useDeleteKnowledgeDocument();
  const [mapping, setMapping] = useState<{ key: number } | null>(null);
  const [editingChapters, setEditingChapters] = useState(0);
  const [confirmUnmap, setConfirmUnmap] = useState(false);
  const [confirmReindex, setConfirmReindex] = useState(false);
  const [showChapters, setShowChapters] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  if (isForbidden(lookup.error)) return null;
  // Someone who cannot map has nothing to see on an unmapped asset.
  if (lookup.data === null && !canManage) return null;

  const chapters = detail.data?.chapters ?? [];
  const shell = (children: ReactNode) => (
    <Card as="section" padding="md" aria-labelledby={titleId} className="dam-knowcard">
      <h2 id={titleId} className="dam-metasection__title">
        {t('knowledge.card.title')}
      </h2>
      <div ref={ref} aria-busy={lookup.isFetching}>
        {children}
      </div>
    </Card>
  );

  const runReindex = (force: boolean) => {
    setActionError(null);
    reindex.mutate(
      { id: doc!.id, force },
      {
        onSuccess: () => {
          setConfirmReindex(false);
          push({ title: t('knowledge.card.reindexStarted'), tone: 'success' });
        },
        onError: (e) => {
          setConfirmReindex(false);
          setActionError(t('knowledge.card.reindexFailed', { reason: errorReason(e) }));
        },
      },
    );
  };

  if (lookup.error) {
    return shell(
      <div role="alert" className="dam-knnote">
        <Warning size={16} weight="bold" aria-hidden="true" />
        <span>{t('knowledge.card.error')}</span>
        <Button size="sm" variant="ghost" onClick={() => void lookup.refetch()}>
          {t('knowledge.retry')}
        </Button>
      </div>,
    );
  }
  if (lookup.isPending || (doc && detail.isPending)) {
    return shell(
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('knowledge.card.loading')}</span>
        <div className="dam-stack" aria-hidden="true">
          <Skeleton height={20} width="50%" />
          <Skeleton height={64} />
        </div>
      </div>,
    );
  }

  const dialogs = (
    <>
      {mapping && (
        <DocumentMapDialog
          key={mapping.key}
          open
          document={doc}
          asset={{ id: asset.id, title: asset.title, type: asset.type }}
          onClose={() => setMapping(null)}
          onSaved={(saved, created) => {
            setMapping(null);
            push({
              title: t(created ? 'knowledge.map.created' : 'knowledge.map.saved', { title: saved.title }),
              tone: 'success',
            });
          }}
        />
      )}
      {editingChapters > 0 && doc && (
        <ChapterEditor
          key={editingChapters}
          open
          documentId={doc.id}
          chapters={chapters}
          onClose={() => setEditingChapters(0)}
          onSaved={() => {
            setEditingChapters(0);
            push({ title: t('knowledge.chapters.saved'), tone: 'success' });
          }}
        />
      )}
      <Modal
        open={confirmUnmap}
        title={t('knowledge.unmap.title')}
        closeLabel={t('knowledge.close')}
        onClose={() => setConfirmUnmap(false)}
      >
        <div className="dam-stack dam-dialogform">
          <p>{t('knowledge.unmap.body', { title: asset.title })}</p>
          <p className="dam-hint">{t('knowledge.unmap.note')}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setConfirmUnmap(false)}>
              {t('knowledge.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={unmap.isPending}
              onClick={() =>
                unmap.mutate(doc!.id, {
                  onSuccess: () => {
                    setConfirmUnmap(false);
                    push({ title: t('knowledge.unmap.done', { title: asset.title }), tone: 'success' });
                  },
                  onError: (e) => {
                    setConfirmUnmap(false);
                    setActionError(t('knowledge.unmap.failed', { reason: errorReason(e) }));
                  },
                })
              }
            >
              {t('knowledge.unmap.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
      <Modal
        open={confirmReindex}
        title={t('knowledge.card.replaceTitle')}
        closeLabel={t('knowledge.close')}
        onClose={() => setConfirmReindex(false)}
      >
        <div className="dam-stack dam-dialogform">
          <p>{t('knowledge.card.replaceBody')}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setConfirmReindex(false)}>
              {t('knowledge.cancel')}
            </Button>
            <Button variant="danger" loading={reindex.isPending} onClick={() => runReindex(true)}>
              {t('knowledge.card.replaceConfirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );

  // ---- not mapped (only a manager gets here) --------------------------------------------------------------------
  if (!doc) {
    return (
      <>
        {shell(
          <EmptyState
            titleAs="p"
            icon={<Books />}
            title={t('knowledge.card.notMapped')}
            description={t('knowledge.card.notMappedBody')}
            action={
              <Button variant="secondary" onClick={() => setMapping((m) => ({ key: (m?.key ?? 0) + 1 }))}>
                <Plus size={16} weight="bold" aria-hidden="true" />
                {t('knowledge.card.add')}
              </Button>
            }
          />,
        )}
        {dialogs}
      </>
    );
  }

  // ---- mapped ---------------------------------------------------------------------------------------------------
  const d: KnowledgeDocumentDto = doc;
  const none = <span className="dam-hint">{t('knowledge.none')}</span>;
  return (
    <>
      {shell(
        <div className="dam-stack">
          <dl className="dam-facts">
            <div className="dam-facts__row">
              <dt>{t('knowledge.field.docType')}</dt>
              <dd>
                <Badge size="sm" tone="info">
                  {t(`knowledge.docType.${d.docType}`, { defaultValue: d.docType })}
                </Badge>
              </dd>
            </div>
            <div className="dam-facts__row">
              <dt>{t('knowledge.field.model')}</dt>
              <dd className="dam-kncode">{d.model}</dd>
            </div>
            <div className="dam-facts__row">
              <dt>{t('knowledge.field.variant')}</dt>
              <dd className="dam-kncode">{d.variant ?? t('knowledge.allVariants')}</dd>
            </div>
            <div className="dam-facts__row">
              <dt>{t('knowledge.col.years')}</dt>
              <dd>{formatYears(t, d.modelYearFrom, d.modelYearTo)}</dd>
            </div>
            <div className="dam-facts__row">
              <dt>{t('knowledge.col.language')}</dt>
              <dd>{languageLabel(d.language, i18n.language)}</dd>
            </div>
            <div className="dam-facts__row">
              <dt>{t('knowledge.col.theme')}</dt>
              <dd>
                {d.theme ? (
                  <Badge size="sm" tone="brand">
                    {d.theme}
                  </Badge>
                ) : (
                  none
                )}
              </dd>
            </div>
            <div className="dam-facts__row">
              <dt>{t('knowledge.card.chaptersSource')}</dt>
              <dd>
                {t(`knowledge.source.${d.chaptersSource}`, { defaultValue: d.chaptersSource })}
                {d.chaptersIndexedAt && (
                  <span className="dam-hint">
                    {' '}
                    ·{' '}
                    <time dateTime={d.chaptersIndexedAt} title={new Date(d.chaptersIndexedAt).toLocaleString(i18n.language)}>
                      {relativeTime(d.chaptersIndexedAt, i18n.language)}
                    </time>
                  </span>
                )}
              </dd>
            </div>
          </dl>

          <div>
            <button
              type="button"
              className="dam-knchtoggle"
              aria-expanded={showChapters}
              aria-controls={listId}
              onClick={() => setShowChapters((v) => !v)}
            >
              {showChapters ? (
                <CaretDown size={14} weight="bold" aria-hidden="true" />
              ) : (
                <CaretRight size={14} weight="bold" aria-hidden="true" />
              )}
              {t('knowledge.card.chapters', { count: chapters.length })}
            </button>
            <div id={listId} hidden={!showChapters}>
              {chapters.length === 0 ? (
                <p className="dam-hint">{t('knowledge.card.noChapters')}</p>
              ) : (
                <ChapterTree chapters={chapters} assetId={asset.id} label={t('knowledge.result.chaptersLabel')} />
              )}
            </div>
          </div>

          {canManage && (
            <div className="dam-knactions" role="group" aria-label={t('knowledge.card.actions')}>
              <Button size="sm" variant="secondary" onClick={() => setMapping((m) => ({ key: (m?.key ?? 0) + 1 }))}>
                <PencilSimple size={14} aria-hidden="true" />
                {t('knowledge.card.edit')}
              </Button>
              <Button size="sm" variant="secondary" onClick={() => setEditingChapters((n) => n + 1)}>
                <ListBullets size={14} aria-hidden="true" />
                {t('knowledge.card.editChapters')}
              </Button>
              <Button
                size="sm"
                variant="secondary"
                loading={reindex.isPending && !confirmReindex}
                onClick={() => (d.chaptersSource === 'manual' ? setConfirmReindex(true) : runReindex(false))}
              >
                <ArrowsClockwise size={14} aria-hidden="true" />
                {t('knowledge.card.reindex')}
              </Button>
              <Button size="sm" variant="ghost" onClick={() => setConfirmUnmap(true)}>
                <Trash size={14} aria-hidden="true" />
                {t('knowledge.card.unmap')}
              </Button>
            </div>
          )}
          {actionError && (
            <p className="dam-error" role="alert">
              {actionError}
            </p>
          )}
        </div>,
      )}
      {dialogs}
    </>
  );
}
