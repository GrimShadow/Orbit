import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import { decodeFilters, FILTER_KEYS, patchParams, type AuditFilters, type FilterKey } from './auditFilters';

/** The audit filters live in the URL (`?from=&to=&actor=&action=&entityType=&entityId=&q=`) so a view can be linked,
 * survives a reload and works with back/forward. */
export function useAuditFilters() {
  const [params, setParams] = useSearchParams();
  const filters = useMemo(() => decodeFilters(params), [params]);
  const update = useCallback(
    (patch: Partial<Record<FilterKey, string | undefined>>, opts?: { replace?: boolean }) =>
      setParams((prev) => patchParams(prev, patch), { replace: opts?.replace }),
    [setParams],
  );
  const clear = useCallback(
    () => setParams((prev) => patchParams(prev, Object.fromEntries(FILTER_KEYS.map((k) => [k, undefined])))),
    [setParams],
  );
  return { filters: filters as AuditFilters, update, clear };
}
