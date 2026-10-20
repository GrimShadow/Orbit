/** The page a `?page=<n>` link points at (a chapter in the Knowledge card or the VIN resolver): a whole number from 1, else null. */
export function parseDeepLinkPage(raw: string | null): number | null {
  if (raw === null || !/^\d{1,6}$/.test(raw)) return null;
  const n = Number(raw);
  return n >= 1 ? n : null;
}
