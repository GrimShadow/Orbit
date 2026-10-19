import { useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';

export const STATUSES = ['draft', 'in_review', 'approved', 'published', 'expired', 'archived', 'deleted'] as const;
export const TYPES = ['image', 'video', 'audio', 'document', 'manual', '3d', 'html5', 'other'] as const;
export type LibraryView = 'grid' | 'list';

export interface LibraryParams {
  folder: string | undefined;
  status: string;
  type: string;
  view: LibraryView;
}

type Patch = Partial<{ folder: string | undefined; status: string; type: string; view: LibraryView }>;

/** The library's browse state lives in the URL (`?folder=&status=&type=&view=`) so a refresh, a shared link and the
 * back/forward buttons all restore it. Unknown status/type values are ignored rather than sent to the API. */
export function useLibraryParams(): LibraryParams & { update: (patch: Patch, opts?: { replace?: boolean }) => void } {
  const [params, setParams] = useSearchParams();
  const status = params.get('status') ?? '';
  const type = params.get('type') ?? '';
  const state: LibraryParams = {
    folder: params.get('folder') || undefined,
    status: (STATUSES as readonly string[]).includes(status) ? status : '',
    type: (TYPES as readonly string[]).includes(type) ? type : '',
    view: params.get('view') === 'list' ? 'list' : 'grid',
  };
  const update = useCallback(
    (patch: Patch, opts?: { replace?: boolean }) =>
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          for (const [key, value] of Object.entries(patch)) {
            if (value && !(key === 'view' && value === 'grid')) next.set(key, value);
            else next.delete(key);
          }
          return next;
        },
        { replace: opts?.replace },
      ),
    [setParams],
  );
  return { ...state, update };
}
