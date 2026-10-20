import { unwrap } from '@dam/sdk-js';
import { useQueries, useQuery } from '@tanstack/react-query';
import { useApiClient } from '../../../api/apiClient';
import { useAuth } from '../../../auth/AuthProvider';
import { schemaOptions, useTerms, useVocabularies } from '../../asset/assetApi';

export type ScopeDimension = 'channel' | 'region';

export interface ScopeOptions {
  loading: boolean;
  /** The tenant's terms for this dimension, or `null` when it has no vocabulary (the picker then takes free entry). */
  options: { value: string; label: string }[] | null;
}

/** `global` is not a value: an empty list already means everything, and the server refuses it. */
const NOT_A_VALUE = 'global';

/** The channel or region terms a rights record may name. They are the codes of the vocabulary behind the schema field
 * tagged with that `accessDimension`; a tenant that tags none uses a vocabulary keyed `channel` / `region`; a tenant
 * with neither (or an empty one) gets `null`, and the server accepts any lower-case slug instead. */
export function useScopeOptions(dimension: ScopeDimension): ScopeOptions {
  const api = useApiClient();
  const { status } = useAuth();
  const vocabularies = useVocabularies();
  const summaries = useQuery({
    queryKey: ['schemas'],
    queryFn: async () => unwrap(await api.GET('/api/v1/schemas')),
    enabled: status === 'authenticated',
    staleTime: 60_000,
  });
  const schemas = useQueries({
    queries: (summaries.data ?? []).map((s) => ({ ...schemaOptions(api, s.assetType), staleTime: 60_000 })),
  });
  const tagged = schemas
    .flatMap((q) => q.data?.fields ?? [])
    .find((f) => f.accessDimension === dimension && f.vocabulary)?.vocabulary;
  const vocabulary = vocabularies.data?.find((v) => v.key === (tagged ?? dimension));
  const terms = useTerms(vocabulary?.id);

  const settled = (q: { isPending: boolean }) => !q.isPending;
  const loading =
    !settled(vocabularies) || !settled(summaries) || schemas.some((q) => q.isPending) || (!!vocabulary && terms.isPending);

  const usable = (terms.data ?? []).filter((t) => t.code.toLowerCase() !== NOT_A_VALUE);
  return {
    loading: loading && !vocabularies.isError,
    options: usable.length > 0 ? usable.map((t) => ({ value: t.code, label: t.label })) : null,
  };
}
