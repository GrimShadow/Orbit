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

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

/** One value of a three-way merge: what the person has now (`mine`), what the server held when they started (`base`), and
 * what it holds now (`theirs`). Untouched means `mine` equals `base`: take `theirs`. Objects (a multilingual field is one)
 * merge key by key, so editing Hindi while an English translation is accepted keeps both. */
function mergeValue(mine: unknown, base: unknown, theirs: unknown): unknown {
  if (canonical(mine) === canonical(base)) return theirs;
  if (isObject(mine) && (isObject(base) || base == null) && (isObject(theirs) || theirs == null)) {
    const keys = new Set([...Object.keys(mine), ...Object.keys(base ?? {}), ...Object.keys(theirs ?? {})]);
    const out: Record<string, unknown> = {};
    for (const k of keys) {
      const merged = mergeValue(mine[k], (base ?? {})[k], (theirs ?? {})[k]);
      if (merged !== undefined) out[k] = merged;
    }
    return out;
  }
  return mine;
}

/** The draft after the server copy moved from `base` to `theirs` while the draft holds unsaved edits: every field the person
 * has not touched follows the server, every field they have touched keeps their edit. Without this, saving would send the
 * stale value of a field changed meanwhile (an accepted AI translation, say) and silently undo it. */
export function rebaseValues(mine: Values, base: Values, theirs: Values): Values {
  return mergeValue(mine, base, theirs) as Values;
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
 * library, an accepted AI translation) an untouched draft follows it; one with unsaved edits keeps those edits and follows the
 * server for every field it has not touched. */
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
    setValues(canonical(values) === canonical(seen.meta) ? serverMeta : rebaseValues(values, seen.meta, serverMeta));
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
