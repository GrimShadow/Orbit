import { Books, Lock } from '@phosphor-icons/react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { useMe } from '../../api/me';
import { relativeTime } from '../../lib/relativeTime';
import {
  Badge,
  Button,
  EmptyState,
  Input,
  Modal,
  PageHeader,
  PencilSimple,
  Plus,
  Select,
  Table,
  Trash,
  WarningCircle,
  useToast,
} from '../../ui';
import { StatusBadge } from '../asset/StatusBadge';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { languageLabel, useEnabledLanguages } from '../translation/languages';
import { DocumentMapDialog } from './DocumentMapDialog';
import { KnowledgeTabs } from './KnowledgeTabs';
import { errorReason, isForbidden, useDeleteKnowledgeDocument, useKnowledgeDocuments } from './knowledgeApi';
import { assetHref, formatYears } from './knowledgeModel';
import { DOC_TYPES, type DocumentFilters, type KnowledgeDocumentDto } from './knowledgeTypes';
import './knowledge.css';

const NO_FILTERS: DocumentFilters = { type: '', model: '', variant: '', year: '', lang: '', q: '' };

/** The text filters follow the typing after a short pause, so a request is not sent for every keystroke. */
function useDebounced<T>(value: T, ms = 300): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const id = setTimeout(() => setV(value), ms);
    return () => clearTimeout(id);
  }, [value, ms]);
  return v;
}

/** `/knowledge`: the documents a vehicle app can look up - owner manuals, FAQs, service and training material - each an existing
 * asset mapped to a model, variant, model years and language (ADR-0039). Anyone with `assets.read` can browse; mapping needs
 * `knowledge.manage`. */
export function KnowledgePage() {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  useDocumentTitle(t('knowledge.documents.title'));
  const me = useMe();
  const languages = useEnabledLanguages();
  const canManage = !!me.data?.permissions.includes('knowledge.manage');
  const [typed, setTyped] = useState<DocumentFilters>(NO_FILTERS);
  const debounced = useDebounced(typed);
  // The selects apply at once; the text boxes after the pause.
  const filters: DocumentFilters = { ...debounced, type: typed.type, lang: typed.lang };
  const query = useKnowledgeDocuments(filters);
  const remove = useDeleteKnowledgeDocument();
  const [form, setForm] = useState<{ document?: KnowledgeDocumentDto; key: number } | null>(null);
  const [toDelete, setToDelete] = useState<KnowledgeDocumentDto | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  const docs = query.data?.pages.flatMap((p) => p.items) ?? [];
  const filtering = Object.values(filters).some((v) => v.trim() !== '');
  const empty = !query.isPending && !query.error && docs.length === 0 && !filtering;
  const set = (key: keyof DocumentFilters, value: string) => setTyped((f) => ({ ...f, [key]: value }));

  const mapAction = (
    <Button variant="primary" onClick={() => setForm((f) => ({ key: (f?.key ?? 0) + 1 }))}>
      <Plus size={16} weight="bold" aria-hidden="true" />
      {t('knowledge.documents.map')}
    </Button>
  );
  const closeDelete = () => {
    setToDelete(null);
    setDeleteError(null);
  };

  let body;
  if (isForbidden(query.error)) {
    body = (
      <EmptyState
        icon={<Lock size={28} />}
        title={t('knowledge.forbidden.title')}
        description={t('knowledge.forbidden.description')}
      />
    );
  } else if (query.error) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('knowledge.documents.loadFailed')}
          description={t('knowledge.documents.loadFailedHint')}
          action={<Button onClick={() => void query.refetch()}>{t('knowledge.retry')}</Button>}
        />
      </div>
    );
  } else if (empty) {
    body = (
      <EmptyState
        icon={<Books size={28} />}
        title={t('knowledge.documents.empty.title')}
        description={t(canManage ? 'knowledge.documents.empty.manage' : 'knowledge.documents.empty.read')}
        action={canManage ? mapAction : undefined}
      />
    );
  } else {
    body = (
      <>
        {query.isPending && (
          <p className="dam-sr-only" role="status">
            {t('knowledge.documents.loading')}
          </p>
        )}
        <Table<KnowledgeDocumentDto>
          caption={t('knowledge.documents.table')}
          loading={query.isPending}
          rows={docs}
          getRowId={(r) => r.id}
          emptyMessage={t('knowledge.documents.noMatch')}
          columns={[
            {
              key: 'title',
              header: t('knowledge.col.title'),
              render: (r) => (
                <Link className="dam-kndoc__title" to={assetHref(r.assetId)}>
                  {r.title}
                </Link>
              ),
            },
            {
              key: 'type',
              header: t('knowledge.col.type'),
              render: (r) => (
                <Badge size="sm" tone="info">
                  {t(`knowledge.docType.${r.docType}`, { defaultValue: r.docType })}
                </Badge>
              ),
            },
            {
              key: 'vehicle',
              header: t('knowledge.col.vehicle'),
              render: (r) => (
                <span className="dam-kncode">
                  {r.model}
                  <span className="dam-hint"> / {r.variant ?? t('knowledge.allVariants')}</span>
                </span>
              ),
            },
            { key: 'years', header: t('knowledge.col.years'), render: (r) => formatYears(t, r.modelYearFrom, r.modelYearTo) },
            { key: 'language', header: t('knowledge.col.language'), render: (r) => languageLabel(r.language, i18n.language) },
            {
              key: 'theme',
              header: t('knowledge.col.theme'),
              className: 'dam-col--hide-sm',
              render: (r) =>
                r.theme ? (
                  <Badge size="sm" tone="brand">
                    {r.theme}
                  </Badge>
                ) : (
                  <span className="dam-hint">{t('knowledge.none')}</span>
                ),
            },
            { key: 'status', header: t('knowledge.col.status'), render: (r) => <StatusBadge status={r.assetStatus} /> },
            {
              key: 'chapters',
              header: t('knowledge.col.chapters'),
              className: 'dam-col--hide-sm dam-knnum',
              render: (r) => r.chapterCount.toLocaleString(i18n.language),
            },
            {
              key: 'updated',
              header: t('knowledge.col.updated'),
              className: 'dam-col--hide-sm',
              render: (r) => (
                <time dateTime={r.updatedAt} title={new Date(r.updatedAt).toLocaleString(i18n.language)}>
                  {relativeTime(r.updatedAt, i18n.language)}
                </time>
              ),
            },
            ...(canManage
              ? [
                  {
                    key: 'actions',
                    header: <span className="dam-sr-only">{t('knowledge.col.actions')}</span>,
                    render: (r: KnowledgeDocumentDto) => (
                      <span className="dam-knrowactions">
                        <button
                          type="button"
                          className="dam-btn dam-btn--ghost dam-btn--sm dam-btn--icon"
                          aria-label={t('knowledge.documents.edit', { title: r.title })}
                          title={t('knowledge.documents.edit', { title: r.title })}
                          onClick={() => setForm((f) => ({ document: r, key: (f?.key ?? 0) + 1 }))}
                        >
                          <PencilSimple size={16} aria-hidden="true" />
                        </button>
                        <button
                          type="button"
                          className="dam-btn dam-btn--ghost dam-btn--sm dam-btn--icon"
                          aria-label={t('knowledge.documents.unmap', { title: r.title })}
                          title={t('knowledge.documents.unmap', { title: r.title })}
                          onClick={() => setToDelete(r)}
                        >
                          <Trash size={16} aria-hidden="true" />
                        </button>
                      </span>
                    ),
                  },
                ]
              : []),
          ]}
        />
        {query.hasNextPage && (
          <div className="dam-knmore">
            <Button variant="secondary" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
              {t('knowledge.loadMore')}
            </Button>
          </div>
        )}
        {!query.isPending && (
          <p className="dam-hint" role="status">
            {t('knowledge.documents.count', { count: docs.length })}
          </p>
        )}
      </>
    );
  }

  const showFilters = !empty && !isForbidden(query.error);

  return (
    <div className="dam-page dam-kn">
      <PageHeader
        eyebrow={t('knowledge.title')}
        title={t('knowledge.documents.title')}
        description={t('knowledge.documents.description')}
        actions={canManage && !!query.data && !empty ? mapAction : undefined}
      />
      <KnowledgeTabs />
      {showFilters && (
        <form
          className="dam-knfilters"
          role="search"
          aria-label={t('knowledge.filters.label')}
          onSubmit={(e) => e.preventDefault()}
        >
          <Input
            type="search"
            label={t('knowledge.filters.search')}
            placeholder={t('knowledge.filters.searchPlaceholder')}
            value={typed.q}
            autoComplete="off"
            onChange={(e) => set('q', e.target.value)}
          />
          <Select
            label={t('knowledge.filters.type')}
            value={typed.type}
            options={[
              { value: '', label: t('knowledge.filters.anyType') },
              ...DOC_TYPES.map((d) => ({ value: d, label: t(`knowledge.docType.${d}`) })),
            ]}
            onChange={(e) => set('type', e.target.value)}
          />
          <Input
            label={t('knowledge.filters.model')}
            value={typed.model}
            autoComplete="off"
            spellCheck={false}
            onChange={(e) => set('model', e.target.value)}
          />
          <Input
            label={t('knowledge.filters.variant')}
            value={typed.variant}
            autoComplete="off"
            spellCheck={false}
            onChange={(e) => set('variant', e.target.value)}
          />
          <Input
            label={t('knowledge.filters.year')}
            value={typed.year}
            inputMode="numeric"
            maxLength={4}
            autoComplete="off"
            onChange={(e) => set('year', e.target.value)}
          />
          <Select
            label={t('knowledge.filters.language')}
            value={typed.lang}
            options={[
              { value: '', label: t('knowledge.filters.anyLanguage') },
              ...languages.map((l) => ({ value: l, label: languageLabel(l, i18n.language) })),
            ]}
            onChange={(e) => set('lang', e.target.value)}
          />
        </form>
      )}
      <div aria-busy={query.isFetching && !query.isFetchingNextPage}>{body}</div>

      {form && (
        <DocumentMapDialog
          key={form.key}
          open
          document={form.document}
          onClose={() => setForm(null)}
          onSaved={(saved, created) => {
            setForm(null);
            push({
              title: t(created ? 'knowledge.map.created' : 'knowledge.map.saved', { title: saved.title }),
              tone: 'success',
            });
          }}
        />
      )}

      <Modal open={toDelete !== null} title={t('knowledge.unmap.title')} closeLabel={t('knowledge.close')} onClose={closeDelete}>
        <div className="dam-stack dam-dialogform">
          <p>{t('knowledge.unmap.body', { title: toDelete?.title })}</p>
          <p className="dam-hint">{t('knowledge.unmap.note')}</p>
          {deleteError && (
            <p className="dam-error" role="alert">
              {deleteError}
            </p>
          )}
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={closeDelete}>
              {t('knowledge.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={remove.isPending}
              onClick={() => {
                const gone = toDelete!;
                remove.mutate(gone.id, {
                  onSuccess: () => {
                    closeDelete();
                    push({ title: t('knowledge.unmap.done', { title: gone.title }), tone: 'success' });
                  },
                  onError: (e) => setDeleteError(t('knowledge.unmap.failed', { reason: errorReason(e) })),
                });
              }}
            >
              {t('knowledge.unmap.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
