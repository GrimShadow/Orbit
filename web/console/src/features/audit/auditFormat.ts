/** Small display helpers for audit entries. */

/** `abcd1234…ef567890`: enough of a hash to recognise, with the full value one click (copy) away. */
export function shortHash(hash: string, head = 8, tail = 8): string {
  return hash.length <= head + tail + 1 ? hash : `${hash.slice(0, head)}…${hash.slice(-tail)}`;
}

export const shortId = (id: string): string => (id.length > 9 ? `${id.slice(0, 8)}…` : id);

export type VerifyReason = 'gap' | 'prev_hash_mismatch' | 'hash_mismatch';

/** The i18n key suffix for a verification failure reason (unknown reasons get a generic sentence). */
export function reasonKey(reason: string | null | undefined): VerifyReason | 'unknown' {
  return reason === 'gap' || reason === 'prev_hash_mismatch' || reason === 'hash_mismatch' ? reason : 'unknown';
}

/** The console route that shows an audited entity, when there is one. */
export function entityRoute(entityType: string | null, entityId: string | null): string | null {
  if (!entityType || !entityId) return null;
  switch (entityType) {
    case 'asset':
      return `/library/assets/${entityId}`;
    case 'folder':
      return `/library?folder=${entityId}`;
    case 'collection':
      return `/collections/${entityId}`;
    case 'workflow':
      return `/settings/workflows/${entityId}`;
    default:
      return null;
  }
}

/** The asset an event concerns, read from its recorded payload (`AssetId`), for events whose entity is something else
 * (a workflow task, a rendition). */
export function relatedAssetId(payload: unknown): string | null {
  if (payload && typeof payload === 'object' && !Array.isArray(payload)) {
    const v = (payload as Record<string, unknown>).AssetId ?? (payload as Record<string, unknown>).assetId;
    if (typeof v === 'string' && v) return v;
  }
  return null;
}
