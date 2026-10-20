import { unwrap } from '@dam/sdk-js';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApiClient } from '../../api/apiClient';
import { Button, Input, Skeleton, WarningCircle } from '../../ui';
import { StatusBadge } from '../asset/StatusBadge';
import { KNOWLEDGE_ASSET_TYPES } from './knowledgeTypes';

export interface PickedAsset {
  id: string;
  title: string;
  type: string;
  status?: string;
}

interface Props {
  value: PickedAsset | null;
  onChange: (asset: PickedAsset | null) => void;
  error?: string;
}

const RESULTS = 8;

/** Finds one existing manual, document or HTML5 asset: a search box (global search, filtered to those types) over a short list of
 * choices. Once an asset is chosen it shows as a summary with a "Change" action. */
export function AssetPicker({ value, onChange, error }: Props) {
  const { t } = useTranslation();
  const api = useApiClient();
  const base = useId();
  const [typed, setTyped] = useState('');
  const [q, setQ] = useState('');
  useEffect(() => {
    const id = setTimeout(() => setQ(typed.trim()), 300);
    return () => clearTimeout(id);
  }, [typed]);

  const search = useQuery({
    queryKey: ['knowledge', 'asset-picker', q],
    queryFn: async () =>
      unwrap(
        await api.POST('/api/v1/search', {
          body: {
            query: q || null,
            filters: { type: [...KNOWLEDGE_ASSET_TYPES] },
            facets: null,
            sort: null,
            highlight: false,
            similarTo: null,
            cursor: null,
            limit: RESULTS,
            semantic: false,
          },
        }),
      ),
    enabled: value === null,
    placeholderData: keepPreviousData,
    staleTime: 15_000,
  });

  if (value) {
    return (
      <div className="dam-field">
        <span className="dam-label" id={`${base}-label`}>
          {t('knowledge.picker.label')}
        </span>
        <div className="dam-knpicked" role="group" aria-labelledby={`${base}-label`}>
          <span className="dam-knpicked__title">{value.title}</span>
          <span className="dam-hint">{t(`library.type.${value.type}`, { defaultValue: value.type })}</span>
          <Button size="sm" variant="ghost" onClick={() => onChange(null)}>
            {t('knowledge.picker.change')}
          </Button>
        </div>
      </div>
    );
  }

  const hits = search.data?.hits ?? [];
  return (
    <fieldset className="dam-knpicker" aria-describedby={error ? `${base}-error` : undefined}>
      <legend className="dam-label">{t('knowledge.picker.label')}</legend>
      <Input
        type="search"
        label={t('knowledge.picker.search')}
        hint={t('knowledge.picker.hint')}
        placeholder={t('knowledge.picker.placeholder')}
        value={typed}
        autoComplete="off"
        onChange={(e) => setTyped(e.target.value)}
      />
      <div aria-live="polite" aria-busy={search.isFetching}>
        {search.isPending ? (
          <div role="status">
            <span className="dam-sr-only">{t('knowledge.picker.loading')}</span>
            <div className="dam-stack" aria-hidden="true">
              <Skeleton height={36} />
              <Skeleton height={36} />
            </div>
          </div>
        ) : search.error ? (
          <p className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {t('knowledge.picker.failed')}
          </p>
        ) : hits.length === 0 ? (
          <p className="dam-hint">{t('knowledge.picker.none')}</p>
        ) : (
          <>
            <p className="dam-sr-only">{t('knowledge.picker.count', { count: hits.length })}</p>
            <ul className="dam-knpicker__list">
              {hits.map((h) => (
                <li key={h.id}>
                  <button
                    type="button"
                    className="dam-knpicker__opt"
                    onClick={() => onChange({ id: h.id, title: h.title, type: h.type, status: h.status })}
                  >
                    <span className="dam-knpicker__name">{h.title}</span>
                    <span className="dam-hint">{t(`library.type.${h.type}`, { defaultValue: h.type })}</span>
                    <StatusBadge status={h.status} />
                  </button>
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
      {error && (
        <span id={`${base}-error`} className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </span>
      )}
    </fieldset>
  );
}
