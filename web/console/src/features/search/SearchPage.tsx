import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { BookmarkSimple, Button, FacetList, Input, LoadingRegion, Skeleton, ThumbnailGrid, useToast, type Facet } from '../../ui';
import { useThumbnailLinks } from '../asset/assetApi';
import { useCardActions } from '../library/cardActions';
import { toAssetThumb } from '../library/assetThumb';
import { useCreateSmartCollection } from '../collections/collectionsApi';
import { NameCollectionDialog } from '../collections/NameCollectionDialog';
import { SEARCH_FACET_FIELDS, useSearch, type SearchResultDto } from './searchApi';
import './search.css';

function toFacets(t: (key: string) => string, result: SearchResultDto | undefined): Facet[] {
  if (!result) return [];
  return result.facets.map((f) => ({
    id: f.field,
    label: t(`search.facet.${f.field}`),
    options: f.values.map((v) => ({
      value: v.value,
      label: f.field === 'type' ? t(`library.type.${v.value}`) : f.field === 'status' ? t(`library.status.${v.value}`) : v.value,
      count: v.count,
    })),
  }));
}

/** The global, cross-folder query surface (spec Step 1.8e), distinct from the library screen's folder-scoped browsing
 * (ADR-0012). Runs with an empty query by default (a pure filter/browse over everything the caller can see) and
 * lets the current query - text + facet filters - be saved as a named smart collection ("saved search", ADR-0018). */
export function SearchPage() {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const { push } = useToast();
  const [queryText, setQueryText] = useState('');
  const [filters, setFilters] = useState<Record<string, string[]>>({});
  const [cursor, setCursor] = useState<string | undefined>();
  const [saveOpen, setSaveOpen] = useState(false);

  const setQuery = (apply: () => void) => {
    setCursor(undefined);
    apply();
  };

  const body = {
    query: queryText || null,
    filters: Object.keys(filters).length > 0 ? filters : null,
    facets: SEARCH_FACET_FIELDS,
    sort: null,
    highlight: false,
    similarTo: null,
    cursor: cursor ?? null,
    limit: 50,
  };
  const searchQuery = useSearch(body, true);
  const thumbnailLinks = useThumbnailLinks(searchQuery.data?.hits.map((h) => h.id) ?? []);
  const cardActions = useCardActions(searchQuery.data?.hits ?? []);
  const createSmart = useCreateSmartCollection();

  return (
    <div className="dam-page dam-search">
      <h1>{t('search.title')}</h1>
      <Input label={t('search.queryLabel')} value={queryText} onChange={(e) => setQuery(() => setQueryText(e.target.value))} />

      <div className="dam-search__layout">
        <aside className="dam-search__facets" aria-label={t('search.facets')}>
          <FacetList
            facets={toFacets(t, searchQuery.data)}
            selected={filters}
            onChange={(next) => setQuery(() => setFilters(next))}
          />
        </aside>

        <div className="dam-search__main">
          <Button variant="secondary" onClick={() => setSaveOpen(true)} disabled={!searchQuery.data}>
            <BookmarkSimple size={16} weight="bold" aria-hidden="true" />
            {t('search.saveSearch')}
          </Button>

          {searchQuery.isPending ? (
            <LoadingRegion>
              <Skeleton height={160} />
            </LoadingRegion>
          ) : searchQuery.error ? (
            <div role="alert" className="dam-stack">
              <p>{t('search.failed')}</p>
              <Button onClick={() => void searchQuery.refetch()}>{t('library.retry')}</Button>
            </div>
          ) : (
            <>
              {searchQuery.data.hits.length === 0 ? (
                <p className="dam-empty">{t('search.empty')}</p>
              ) : (
                <ThumbnailGrid
                  items={searchQuery.data.hits.map((h) => toAssetThumb(t, i18n.language, h, thumbnailLinks.data?.[h.id]))}
                  label={t('search.resultsLabel')}
                  onOpen={(id) => navigate(`/library/assets/${id}`)}
                  cardActions={cardActions}
                  menuLabel={(title) => t('library.card.actionsFor', { title })}
                />
              )}
              {searchQuery.data.nextCursor && (
                <Button
                  variant="secondary"
                  onClick={() => setCursor(searchQuery.data.nextCursor!)}
                  loading={searchQuery.isFetching}
                >
                  {t('library.next')}
                </Button>
              )}
            </>
          )}
        </div>
      </div>

      <NameCollectionDialog
        open={saveOpen}
        title={t('search.saveSearchTitle')}
        busy={createSmart.isPending}
        onClose={() => setSaveOpen(false)}
        onSave={(name) =>
          createSmart.mutate(
            { name, query: body },
            {
              onSuccess: () => {
                setSaveOpen(false);
                push({ title: t('search.saveSearchSucceeded', { name }), tone: 'success' });
              },
            },
          )
        }
      />
    </div>
  );
}
