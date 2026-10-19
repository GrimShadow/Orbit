import { useCallback, useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useToast } from '../../ui';
import type { Json, Values } from './MetadataFields';
import { errorMessage, useUpdateAsset, type AssetDto } from './assetApi';

/** A value with every "nothing entered" leaf removed and keys sorted, as a string - so `{a: ''}` equals `{}` and key order
 * from the server never makes a form look edited. Used only to compare, never to save. */
function canonical(v: unknown): string {
  const strip = (x: unknown): unknown => {
    if (x === '' || x === null || x === undefined) return undefined;
    if (Array.isArray(x)) {
      const items = x.map(strip).filter((i) => i !== undefined);
      return items.length > 0 ? items : undefined;
    }
    if (typeof x === 'object') {
      const entries = Object.entries(x as Record<string, unknown>)
        .map(([k, val]) => [k, strip(val)] as const)
        .filter(([, val]) => val !== undefined)
        .sort(([a], [b]) => a.localeCompare(b));
      return entries.length > 0 ? Object.fromEntries(entries) : undefined;
    }
    return x;
  };
  return JSON.stringify(strip(v) ?? null);
}

export interface AssetDraft {
  values: Values;
  tags: string[];
  touched: ReadonlySet<string>;
  dirty: boolean;
  saving: boolean;
  setField: (name: string, value: Json) => void;
  setTags: (tags: string[]) => void;
  touch: (name: string) => void;
  discard: () => void;
  save: () => void;
}

/** The editable half of an asset - its metadata document and tags - held above the tabs, so switching between Details,
 * Metadata and Versions never loses an edit and one "unsaved changes" bar covers both. Saving sends the WHOLE metadata
 * document (`PATCH /assets/{id}` replaces it; fields the schema no longer knows are carried along untouched) and only the
 * parts that actually changed. When the server copy changes underneath (a save, a restored version, a channel toggled from the
 * library) an untouched draft follows it; one with unsaved edits is kept. */
export function useAssetDraft(asset: AssetDto): AssetDraft {
  const { t } = useTranslation();
  const { push } = useToast();
  const update = useUpdateAsset(asset.id);

  const serverMeta = asset.metadata as Values;
  const serverTags = asset.tags;
  const [values, setValues] = useState<Values>(serverMeta);
  const [tags, setTagsState] = useState<string[]>(serverTags);
  const [seen, setSeen] = useState({ meta: serverMeta, tags: serverTags });
  const [touched, setTouched] = useState<ReadonlySet<string>>(new Set());

  if (seen.meta !== serverMeta || seen.tags !== serverTags) {
    // The server copy moved. Follow it unless this draft holds edits made against the previous copy.
    if (canonical(values) === canonical(seen.meta)) setValues(serverMeta);
    if (canonical(tags) === canonical(seen.tags)) setTagsState(serverTags);
    setSeen({ meta: serverMeta, tags: serverTags });
  }

  const metaDirty = canonical(values) !== canonical(serverMeta);
  const tagsDirty = canonical(tags) !== canonical(serverTags);
  const dirty = metaDirty || tagsDirty;

  // Closing the tab or reloading with unsaved edits asks first (in-app navigation is left alone: the bar stays visible).
  useEffect(() => {
    if (!dirty) return;
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  const setField = useCallback((name: string, value: Json) => setValues((v) => ({ ...v, [name]: value })), []);
  const touch = useCallback((name: string) => setTouched((s) => (s.has(name) ? s : new Set(s).add(name))), []);
  const discard = useCallback(() => {
    setValues(serverMeta);
    setTagsState(serverTags);
    setTouched(new Set());
  }, [serverMeta, serverTags]);

  const save = useCallback(() => {
    update.mutate(
      { title: null, tags: tagsDirty ? tags : null, metadata: metaDirty ? values : null },
      {
        onSuccess: () => {
          setTouched(new Set());
          push({ title: t('asset.saved'), tone: 'success' });
        },
        onError: (e) => push({ title: t('asset.saveFailed'), message: errorMessage(e), tone: 'error' }),
      },
    );
  }, [update, tagsDirty, tags, metaDirty, values, push, t]);

  return {
    values,
    tags,
    touched,
    dirty,
    saving: update.isPending,
    setField,
    setTags: setTagsState,
    touch,
    discard,
    save,
  };
}
