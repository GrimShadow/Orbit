import { AI_TYPES } from '../ai/asset/aiFormat';

/** Where a reviewer goes to decide an asset's translations. The AI panel (Details tab) lists them all, but it exists only for
 * the kinds of file the AI reads; for any other kind the suggestions sit beside their fields on the Metadata tab. */
export function reviewHref(assetId: string, assetType: string): string {
  return AI_TYPES.has(assetType)
    ? `/library/assets/${assetId}?focus=translations`
    : `/library/assets/${assetId}?tab=metadata&focus=translations`;
}
