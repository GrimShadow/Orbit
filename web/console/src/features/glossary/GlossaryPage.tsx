import { BookOpenText, Lock } from '@phosphor-icons/react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../../api/client';
import { useMe } from '../../api/me';
import {
  Badge,
  Button,
  EmptyState,
  Input,
  Modal,
  PageHeader,
  PencilSimple,
  Plus,
  Skeleton,
  Table,
  Trash,
  WarningCircle,
  useToast,
} from '../../ui';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { languageName } from '../translation/languages';
import type { GlossaryTerm } from '../translation/translationTypes';
import { GlossaryForm } from './GlossaryForm';
import { GlossaryImport } from './GlossaryImport';
import { GLOSSARY_LIMIT, useDeleteGlossaryTerm, useGlossary } from './glossaryApi';
import './glossary.css';
import { useSettingsEyebrow } from '../settings/useSettingsEyebrow';

/** `/settings/glossary`: terms that translation must not get wrong. Locked terms are kept as written in every language; mapped
 * terms are replaced by a fixed rendering per language (ADR-0036). Needs `glossary.manage`. */
export function GlossaryPage() {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  useDocumentTitle(t('glossary.title'));
  const eyebrow = useSettingsEyebrow();
  const me = useMe();
  const [typed, setTyped] = useState('');
  const [q, setQ] = useState('');
  const query = useGlossary(q);
  const remove = useDeleteGlossaryTerm();
  const [form, setForm] = useState<{ term: GlossaryTerm | null; key: number } | null>(null);
  const [importing, setImporting] = useState(false);
  const [toDelete, setToDelete] = useState<GlossaryTerm | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  // The search follows the typing after a short pause, so a request is not sent for every keystroke.
  useEffect(() => {
    const id = setTimeout(() => setQ(typed), 300);
    return () => clearTimeout(id);
  }, [typed]);

  const canManage = !!me.data?.permissions.includes('glossary.manage');
  const terms = query.data?.pages.flatMap((p) => p.items) ?? [];
  const searching = q.trim() !== '';
  const empty = !query.isPending && !query.error && terms.length === 0 && !searching;

  const actions = (
    <div className="dam-gpage__actions">
      <Button variant="secondary" onClick={() => setImporting(true)}>
        {t('glossary.import.open')}
      </Button>
      <Button variant="primary" onClick={() => setForm((f) => ({ term: null, key: (f?.key ?? 0) + 1 }))}>
        <Plus size={16} weight="bold" aria-hidden="true" />
        {t('glossary.add')}
      </Button>
    </div>
  );

  const closeDelete = () => {
    setToDelete(null);
    setDeleteError(null);
  };

  let body;
  if (me.isPending) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('glossary.loading')}</span>
        <div className="dam-stack dam-stack--fill" aria-hidden="true">
          <Skeleton height={40} />
          <Skeleton height={200} />
        </div>
      </div>
    );
  } else if (!canManage) {
    body = (
      <EmptyState
        icon={<Lock size={28} />}
        title={t('glossary.forbidden.title')}
        description={t('glossary.forbidden.description')}
      />
    );
  } else {
    body = (
      <>
        <Input
          type="search"
          label={t('glossary.search')}
          placeholder={t('glossary.searchPlaceholder')}
          value={typed}
          autoComplete="off"
          onChange={(e) => setTyped(e.target.value)}
        />
        {query.error ? (
          <div role="alert">
            <EmptyState
              icon={<WarningCircle size={28} />}
              title={t('glossary.loadFailed')}
              description={t('glossary.loadFailedHint')}
              action={<Button onClick={() => void query.refetch()}>{t('glossary.retry')}</Button>}
            />
          </div>
        ) : empty ? (
          <EmptyState
            icon={<BookOpenText size={28} />}
            title={t('glossary.empty.title')}
            description={t('glossary.empty.description')}
            action={actions}
          />
        ) : (
          <>
            <Table<GlossaryTerm>
              caption={t('glossary.table')}
              loading={query.isPending}
              rows={terms}
              getRowId={(r) => r.id}
              emptyMessage={t('glossary.noMatch', { q: q.trim() })}
              columns={[
                {
                  key: 'term',
                  header: t('glossary.col.term'),
                  render: (r) => <strong className="dam-gterm">{r.term}</strong>,
                },
                {
                  key: 'mode',
                  header: t('glossary.col.mode'),
                  render: (r) => (
                    <Badge size="sm" tone={r.mode === 'locked' ? 'neutral' : 'brand'}>
                      {t(`glossary.mode.${r.mode}`)}
                    </Badge>
                  ),
                },
                {
                  key: 'languages',
                  header: t('glossary.col.languages'),
                  render: (r) => {
                    const codes = Object.keys(r.translations);
                    return codes.length === 0 ? (
                      <span className="dam-hint">{t('glossary.allLanguages')}</span>
                    ) : (
                      <span className="dam-gchips">
                        {codes.map((c) => (
                          <span key={c} className="dam-gchip" title={`${languageName(c, i18n.language)}: ${r.translations[c]}`}>
                            {c.toUpperCase()}
                          </span>
                        ))}
                      </span>
                    );
                  },
                },
                { key: 'note', header: t('glossary.col.note'), className: 'dam-col--hide-sm', render: (r) => r.note ?? '' },
                {
                  key: 'actions',
                  header: <span className="dam-sr-only">{t('glossary.col.actions')}</span>,
                  render: (r) => (
                    <span className="dam-gpage__rowactions">
                      <button
                        type="button"
                        className="dam-btn dam-btn--ghost dam-btn--sm dam-btn--icon"
                        aria-label={t('glossary.edit', { term: r.term })}
                        title={t('glossary.edit', { term: r.term })}
                        onClick={() => setForm((f) => ({ term: r, key: (f?.key ?? 0) + 1 }))}
                      >
                        <PencilSimple size={16} aria-hidden="true" />
                      </button>
                      <button
                        type="button"
                        className="dam-btn dam-btn--ghost dam-btn--sm dam-btn--icon"
                        aria-label={t('glossary.delete.label', { term: r.term })}
                        title={t('glossary.delete.label', { term: r.term })}
                        onClick={() => setToDelete(r)}
                      >
                        <Trash size={16} aria-hidden="true" />
                      </button>
                    </span>
                  ),
                },
              ]}
            />
            {query.hasNextPage && (
              <div className="dam-gpage__more">
                <Button variant="secondary" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
                  {t('glossary.loadMore')}
                </Button>
              </div>
            )}
            <p className="dam-hint" role="status">
              {t('glossary.count', { count: terms.length })}{' '}
              {t('glossary.limitNote', { max: GLOSSARY_LIMIT.toLocaleString(i18n.language) })}
            </p>
          </>
        )}
      </>
    );
  }

  return (
    <div className="dam-page dam-gpage">
      <PageHeader
        title={t('glossary.title')}
        eyebrow={eyebrow}
        description={t('glossary.description')}
        actions={canManage && !me.isPending && !empty && !query.error ? actions : undefined}
      />
      <p className="dam-gpage__explain">{t('glossary.explain')}</p>
      {body}

      {form && (
        <GlossaryForm
          key={form.key}
          open
          term={form.term}
          onClose={() => setForm(null)}
          onSaved={(saved, created) => {
            setForm(null);
            push({ title: t(created ? 'glossary.created' : 'glossary.saved', { term: saved.term }), tone: 'success' });
          }}
        />
      )}
      {importing && <GlossaryImport open onClose={() => setImporting(false)} />}

      <Modal open={toDelete !== null} title={t('glossary.delete.title')} closeLabel={t('glossary.close')} onClose={closeDelete}>
        <div className="dam-stack dam-dialogform">
          <p>{t('glossary.delete.body', { term: toDelete?.term })}</p>
          <p className="dam-hint">{t('glossary.delete.note')}</p>
          {deleteError && (
            <p className="dam-error" role="alert">
              {deleteError}
            </p>
          )}
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={closeDelete}>
              {t('glossary.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={remove.isPending}
              onClick={() => {
                const gone = toDelete!;
                remove.mutate(gone.id, {
                  onSuccess: () => {
                    closeDelete();
                    push({ title: t('glossary.deleted', { term: gone.term }), tone: 'success' });
                  },
                  onError: (e) =>
                    setDeleteError(t('glossary.delete.failed', { reason: e instanceof ApiError ? e.detail || e.title : '' })),
                });
              }}
            >
              {t('glossary.delete.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
