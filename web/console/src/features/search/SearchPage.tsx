import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import {
  BookmarkSimple,
  Button,
  Card,
  EmptyState,
  Drawer,
  FacetList,
  Images,
  MagnifyingGlass,
  PageHeader,
  ThumbnailGrid,
  ThumbnailGridSkeleton,
  UploadSimple,
  X,
  useToast,
  type Facet,
} from '../../ui';
import { useThumbnailMap } from '../library/assets';
import { FilterChips, type FilterChip } from '../library/FilterChips';
import { useItemActions } from '../library/itemActions';
import { toAssetThumb } from '../library/assetThumb';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { useCreateSmartCollection } from '../collections/collectionsApi';
import { NameCollectionDialog } from '../collections/NameCollectionDialog';
import { SEARCH_FACET_FIELDS, useSearchInfinite, type SearchResultDto } from './searchApi';
import './search.css';

const DEBOUNCE_MS = 250;

function toFacets(t: (key: string) => string, result: SearchResultDto | undefined, selected: Record<string, string[]>): Facet[] {
  if (!result) return [];
  const label = (field: string, value: string) =>
    field === 'type' ? t(`library.type.${value}`) : field === 'status' ? t(`library.status.${value}`) : value;
  return result.facets.flatMap((f) => {
    const options = f.values.map((v) => ({ value: v.value, label: label(f.field, v.value), count: v.count }));
    // A chosen value the current results no longer contain stays listed (count 0) so it can still be unticked.
    for (const value of selected[f.field] ?? []) {
      if (!options.some((o) => o.value === value)) options.push({ value, label: label(f.field, value), count: 0 });
    }
    return options.length > 0 ? [{ id: f.field, label: t(`search.facet.${f.field}`), options }] : [];
  });
}

/** The global, cross-folder query surface (spec Step 1.8e), distinct from the library screen's folder-scoped browsing
 * (ADR-0012). The query text and the facet filters live in the URL (`?q=&type=&status=`) - the shell's global search
 * box lands here with `?q=` - and the page runs with nothing chosen as a browse-all over everything the caller can
 * see. The current query can be saved as a named smart collection ("saved search", ADR-0018). */
export function SearchPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const { push } = useToast();
  useDocumentTitle(t('search.title'));
  const [params, setParams] = useSearchParams();

  const q = params.get('q') ?? '';
  const filters = useMemo(() => {
    const out: Record<string, string[]> = {};
    for (const field of SEARCH_FACET_FIELDS) {
      const values = params.getAll(field);
      if (values.length > 0) out[field] = values;
    }
    return out;
  }, [params]);

  // The input is the user's draft; the URL is the committed query (written debounced, replacing the history entry so
  // typing never fills the back button). A `q` changed from outside - the global search box - overwrites the draft.
  const [draft, setDraft] = useState(q);
  const lastWritten = useRef(q);
  useEffect(() => {
    if (q !== lastWritten.current) {
      lastWritten.current = q;
      setDraft(q);
    }
  }, [q]);
  useEffect(() => {
    const next = draft.trim();
    if (next === q) return;
    const id = window.setTimeout(() => {
      lastWritten.current = next;
      setParams(
        (prev) => {
          const p = new URLSearchParams(prev);
          if (next) p.set('q', next);
          else p.delete('q');
          return p;
        },
        { replace: true },
      );
    }, DEBOUNCE_MS);
    return () => window.clearTimeout(id);
  }, [draft, q, setParams]);

  const setFilters = (next: Record<string, string[]>) =>
    setParams((prev) => {
      const p = new URLSearchParams(prev);
      for (const field of SEARCH_FACET_FIELDS) {
        p.delete(field);
        for (const v of next[field] ?? []) p.append(field, v);
      }
      return p;
    });
  const clearQuery = () => {
    lastWritten.current = '';
    setDraft('');
    setParams((prev) => {
      const p = new URLSearchParams(prev);
      p.delete('q');
      return p;
    });
  };
  const clearFilters = () => setFilters({});
  const clearAll = () => {
    clearQuery();
    clearFilters();
  };

  const [filtersOpen, setFiltersOpen] = useState(false);
  const [saveOpen, setSaveOpen] = useState(false);

  const body = {
    query: q || null,
    filters: Object.keys(filters).length > 0 ? filters : null,
    facets: SEARCH_FACET_FIELDS,
    sort: null,
    highlight: false,
    similarTo: null,
    limit: 50,
  };
  const searchQuery = useSearchInfinite(body);
  const pages = searchQuery.data?.pages;
  const hits = useMemo(() => pages?.flatMap((p) => p.hits) ?? [], [pages]);
  const total = pages?.[0]?.total ?? 0;
  const thumbnails = useThumbnailMap(hits.map((h) => h.id));
  const itemActions = useItemActions();
  const hitById = (id: string) => hits.find((h) => h.id === id);
  const createSmart = useCreateSmartCollection();
  const facets = toFacets(t, pages?.[0], filters);
  const busy = searchQuery.isPlaceholderData;

  const activeFilterCount = Object.values(filters).reduce((n, v) => n + v.length, 0);
  const chips: FilterChip[] = [
    ...(q ? [{ id: 'q', group: t('search.queryChip'), label: q, onRemove: clearQuery }] : []),
    ...SEARCH_FACET_FIELDS.flatMap((field) =>
      (filters[field] ?? []).map((value) => ({
        id: `${field}:${value}`,
        group: t(`search.facet.${field}`),
        label: field === 'type' ? t(`library.type.${value}`) : field === 'status' ? t(`library.status.${value}`) : value,
        onRemove: () => setFilters({ ...filters, [field]: (filters[field] ?? []).filter((v) => v !== value) }),
      })),
    ),
  ];
  const isBrowseAll = !q && activeFilterCount === 0;

  const description = !searchQuery.isSuccess
    ? undefined
    : q
      ? t('search.resultCountFor', { count: total, query: q })
      : activeFilterCount > 0
        ? t('search.resultCount', { count: total })
        : t('search.browseAll');

  const facetPanel = (
    <>
      <FacetList facets={facets} selected={filters} onChange={setFilters} />
      {facets.length === 0 && <p className="dam-hint">{t('search.noFacets')}</p>}
    </>
  );

  const noResults =
    searchQuery.isSuccess && hits.length === 0 ? (
      isBrowseAll ? (
        <EmptyState
          icon={<Images size={28} />}
          title={t('search.emptyLibrary.title')}
          description={t('search.emptyLibrary.description')}
          action={
            <Link to="/upload" className="dam-btn dam-btn--primary">
              <UploadSimple size={16} weight="bold" aria-hidden="true" />
              {t('library.upload')}
            </Link>
          }
        />
      ) : (
        <EmptyState
          icon={<MagnifyingGlass size={28} />}
          title={t('search.noResults.title')}
          description={t('search.noResults.description')}
          action={
            <>
              {activeFilterCount > 0 && (
                <Button variant="secondary" onClick={clearFilters}>
                  {t('library.clearFilters')}
                </Button>
              )}
              {q && (
                <Button variant="secondary" onClick={clearQuery}>
                  {t('search.clearSearch')}
                </Button>
              )}
            </>
          }
        />
      )
    ) : null;

  return (
    <div className="dam-page dam-search">
      <PageHeader
        title={t('search.title')}
        description={description}
        actions={
          <Button variant="secondary" onClick={() => setSaveOpen(true)} disabled={!searchQuery.isSuccess}>
            <BookmarkSimple size={16} weight="bold" aria-hidden="true" />
            {t('search.saveSearch')}
          </Button>
        }
      />

      <div className="dam-search__bar" role="search" aria-label={t('search.title')}>
        <MagnifyingGlass size={20} aria-hidden="true" className="dam-search__baricon" />
        <input
          type="search"
          className="dam-search__input"
          aria-label={t('search.queryLabel')}
          placeholder={t('search.queryPlaceholder')}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          enterKeyHint="search"
        />
        {draft && (
          <button type="button" className="dam-search__clear" aria-label={t('search.clearField')} onClick={clearQuery}>
            <X size={14} weight="bold" aria-hidden="true" />
          </button>
        )}
        <Button className="dam-search__filterbtn" variant="secondary" size="sm" onClick={() => setFiltersOpen(true)}>
          {activeFilterCount > 0 ? t('search.filtersButtonCount', { count: activeFilterCount }) : t('search.filtersButton')}
        </Button>
      </div>

      <FilterChips chips={chips} onClear={clearAll} clearLabel={t('search.clearAll')} />

      <div className="dam-search__layout">
        <aside className="dam-search__facets" aria-label={t('search.facets')}>
          <Card padding="md" className="dam-search__facetcard">
            <div className="dam-search__facethead">
              <h2 className="dam-search__facettitle">{t('search.facets')}</h2>
              {activeFilterCount > 0 && (
                <Button variant="ghost" size="sm" onClick={clearFilters}>
                  {t('search.clearAll')}
                </Button>
              )}
            </div>
            {facetPanel}
          </Card>
        </aside>

        <div className="dam-search__main">
          {searchQuery.isPending ? (
            <ThumbnailGridSkeleton count={10} label={t('search.loading')} />
          ) : searchQuery.error ? (
            <div role="alert" className="dam-search__error">
              <EmptyState
                title={t('search.failed')}
                description={t('search.failedHint')}
                action={<Button onClick={() => void searchQuery.refetch()}>{t('library.retry')}</Button>}
              />
            </div>
          ) : (
            <div className={`dam-search__results${busy ? ' dam-search__results--busy' : ''}`} aria-busy={busy || undefined}>
              {noResults}
              {hits.length > 0 && (
                <ThumbnailGrid
                  items={hits.map((h) => toAssetThumb(t, i18n.language, h, thumbnails[h.id]))}
                  label={t('search.resultsLabel')}
                  onOpen={(id) => navigate(`/library/assets/${id}`)}
                  cardActions={(id) => {
                    const hit = hitById(id);
                    return hit ? itemActions.assetMenu(hit) : [];
                  }}
                  onMenuOpen={(id) => {
                    const hit = hitById(id);
                    if (hit) itemActions.prefetchAsset(hit);
                  }}
                  menuLabel={(title) => t('library.card.actionsFor', { title })}
                />
              )}
              {hits.length > 0 && (
                <div className="dam-search__more">
                  <span className="dam-hint" role="status">
                    {searchQuery.hasNextPage
                      ? t('search.showingMore', { count: hits.length })
                      : t('search.showingAll', { count: hits.length })}
                  </span>
                  {searchQuery.hasNextPage && (
                    <Button
                      variant="secondary"
                      onClick={() => void searchQuery.fetchNextPage()}
                      loading={searchQuery.isFetchingNextPage}
                    >
                      {t('library.loadMore')}
                    </Button>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      <Drawer
        open={filtersOpen}
        title={t('search.facets')}
        onClose={() => setFiltersOpen(false)}
        closeLabel={t('search.closeFilters')}
      >
        <div className="dam-stack dam-search__drawer">
          {facetPanel}
          <div className="dam-search__draweractions">
            {activeFilterCount > 0 && (
              <Button variant="ghost" onClick={clearFilters}>
                {t('search.clearAll')}
              </Button>
            )}
            <Button variant="primary" onClick={() => setFiltersOpen(false)}>
              {t('search.done')}
            </Button>
          </div>
        </div>
      </Drawer>

      <NameCollectionDialog
        open={saveOpen}
        title={t('search.saveSearchTitle')}
        busy={createSmart.isPending}
        onClose={() => setSaveOpen(false)}
        onSave={(name) =>
          createSmart.mutate(
            { name, query: { ...body, cursor: null } },
            {
              onSuccess: () => {
                setSaveOpen(false);
                push({ title: t('search.saveSearchSucceeded', { name }), tone: 'success' });
              },
            },
          )
        }
      />
      {itemActions.dialogs}
    </div>
  );
}
