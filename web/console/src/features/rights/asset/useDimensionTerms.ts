import { useMemo } from 'react';
import { useSchema, useTerms, useVocabularies } from '../../asset/assetApi';

export interface DimensionTerms {
  /** The tenant's channel (or region) terms, as `{ value: code, label }`. Empty when the tenant has no such vocabulary. */
  options: { value: string; label: string }[];
  /** The label for a code - the code itself when it is not (or no longer) a term. */
  labelOf: (code: string) => string;
  loading: boolean;
}

/** The terms behind an access dimension - the same source as the metadata "make available for" field (the current schema's
 * field tagged `accessDimension` with a vocabulary; else a vocabulary keyed `channel` / `region`; ADR-0033). A tenant with
 * neither gets an empty list, and callers fall back to free text. */
export function useDimensionTerms(assetType: string, dimension: 'channel' | 'region'): DimensionTerms {
  const schema = useSchema(assetType);
  const vocabularies = useVocabularies();
  const field = schema.data?.fields.find((f) => f.accessDimension === dimension && f.vocabulary);
  const key = field?.vocabulary ?? dimension;
  const vocabulary = vocabularies.data?.find((v) => v.key === key);
  const terms = useTerms(vocabulary?.id);
  const loading = schema.isPending || vocabularies.isPending || (!!vocabulary && terms.isPending);
  return useMemo(() => {
    // A hierarchical vocabulary (regions) is flattened depth-first: indent children so the list reads as a tree.
    const options = (terms.data ?? []).map((term) => ({
      value: term.code,
      label: `${'\u00a0\u00a0'.repeat(term.depth)}${term.label}`,
    }));
    const labels = new Map((terms.data ?? []).map((term) => [term.code.toLowerCase(), term.label]));
    return { options, labelOf: (code: string) => labels.get(code.toLowerCase()) ?? code, loading };
  }, [terms.data, loading]);
}
