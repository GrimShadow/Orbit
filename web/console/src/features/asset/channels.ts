/** The values of a vocabulary field as term codes (a field holds either a list or, for a single choice, one string). */
export const asCodes = (v: unknown): string[] =>
  Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : typeof v === 'string' && v ? [v] : [];
