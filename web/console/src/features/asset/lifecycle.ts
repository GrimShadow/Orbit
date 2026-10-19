/** Mirrors `Dam.Domain.Assets.AssetLifecycle` (spec A4.2) just enough to decide which action buttons to offer - a purely
 * cosmetic judgment, never a security boundary: the server re-checks every transition itself (permission, required
 * metadata, workflow), so a button shown here that turns out not to be allowed just surfaces the server's own error. */
const FORWARD: Record<string, { action: string; to: string }[]> = {
  draft: [{ action: 'submit', to: 'in_review' }],
  in_review: [
    { action: 'approve', to: 'approved' },
    { action: 'reject', to: 'draft' },
  ],
  approved: [{ action: 'publish', to: 'published' }],
  published: [{ action: 'expire', to: 'expired' }],
  expired: [{ action: 'archive', to: 'archived' }],
};

const WITHDRAWABLE_FROM = new Set(['in_review', 'approved', 'published', 'expired', 'archived']);

export function availableActions(status: string): string[] {
  const actions = (FORWARD[status] ?? []).map((t) => t.action);
  if (WITHDRAWABLE_FROM.has(status)) actions.push('withdraw');
  return actions;
}

/** The single most likely next step for the page's prominent button: only the stages where there is an obvious forward
 * move (a draft is submitted, a reviewed asset approved, an approved one published). Later stages have none - their
 * actions (expire, archive, withdraw) live in the overflow menu. */
const PRIMARY: Record<string, string> = { draft: 'submit', in_review: 'approve', approved: 'publish' };

export function primaryAction(status: string): string | null {
  return PRIMARY[status] ?? null;
}
