import type { TFunction } from 'i18next';

// Pure helpers for the knowledge module: the VIN pattern language (ADR-0039 3), VINs, codes and years. Checks here only save a
// round trip - the server validates everything again.

export const MAX_VIN_LENGTH = 17;
export const MIN_YEAR = 1900;
export const MAX_YEAR = 2100;

export type PatternProblem =
  'empty' | 'tooLong' | 'badChar' | 'unclosedSet' | 'emptySet' | 'badSetChar' | 'badRange' | 'starNotLast';

export type PatternCheck =
  | {
      ok: true;
      /** How many VIN characters the pattern fixes (a `*` adds none). */
      positions: number;
      /** Characters that must match exactly - the more, the more specific the rule. */
      literals: number;
      /** A final `*`: whatever follows is accepted, so the VIN may be any length. */
      open: boolean;
    }
  | { ok: false; problem: PatternProblem; /** The character that is wrong, or a number for tooLong. */ detail?: string };

const ALNUM = /^[A-Za-z0-9]$/;

/** Checks a pattern against the language: letters and digits match themselves; `?` any one character; `[ABC]` or `[A-C]` one
 * of a set or range; a final `*` whatever follows. Case does not matter. At most 17 positions. */
export function checkPattern(pattern: string): PatternCheck {
  if (pattern.length === 0) return { ok: false, problem: 'empty' };
  let positions = 0;
  let literals = 0;
  let open = false;
  for (let i = 0; i < pattern.length; i++) {
    const c = pattern[i]!;
    if (c === '*') {
      if (i !== pattern.length - 1) return { ok: false, problem: 'starNotLast' };
      open = true;
    } else if (c === '?') {
      positions++;
    } else if (c === '[') {
      const end = pattern.indexOf(']', i + 1);
      if (end < 0) return { ok: false, problem: 'unclosedSet' };
      const set = pattern.slice(i + 1, end);
      if (set.length === 0) return { ok: false, problem: 'emptySet' };
      for (let j = 0; j < set.length; j++) {
        const s = set[j]!;
        if (!ALNUM.test(s)) return { ok: false, problem: 'badSetChar', detail: s };
        if (set[j + 1] === '-') {
          const hi = set[j + 2];
          if (hi === undefined || !ALNUM.test(hi)) return { ok: false, problem: 'badRange', detail: `${s}-${hi ?? ''}` };
          if (s.toUpperCase() > hi.toUpperCase()) return { ok: false, problem: 'badRange', detail: `${s}-${hi}` };
          j += 2;
        }
      }
      positions++;
      i = end;
    } else if (ALNUM.test(c)) {
      positions++;
      literals++;
    } else {
      return { ok: false, problem: 'badChar', detail: c };
    }
  }
  if (positions > MAX_VIN_LENGTH) return { ok: false, problem: 'tooLong', detail: String(MAX_VIN_LENGTH) };
  return { ok: true, positions, literals, open };
}

/** A VIN as the API wants it: upper case, no spaces. */
export const normalizeVin = (s: string): string => s.replace(/\s+/g, '').toUpperCase();

export type VinProblem = 'chars' | 'forbidden' | 'length';

/** What is wrong with a (normalised) VIN, or null: 17 letters and digits, none of I, O or Q. The check digit is not enforced. */
export function vinProblem(vin: string): VinProblem | null {
  if (/[^A-Z0-9]/.test(vin)) return 'chars';
  if (/[IOQ]/.test(vin)) return 'forbidden';
  if (vin.length !== MAX_VIN_LENGTH) return 'length';
  return null;
}

const CODE = /^[a-z0-9._-]{1,100}$/;
/** Model, variant and theme are codes: lower case, digits, `.`, `_`, `-`, up to 100 characters. */
export const normalizeCode = (s: string): string => s.trim().toLowerCase();
export const isCode = (s: string): boolean => CODE.test(s);

/** "All years" / "2022" / "2021-2024" for a document's model-year range. */
export function formatYears(t: TFunction, from: number | null, to: number | null): string {
  if (from == null && to == null) return t('knowledge.years.all');
  if (from != null && (to == null || to === from)) return t('knowledge.years.single', { year: from });
  if (from == null) return t('knowledge.years.single', { year: to });
  return t('knowledge.years.range', { from, to });
}

/** A year typed into a form: a whole number from 1900 to 2100, or null for empty, or NaN when it is not one. */
export function parseYear(raw: string): number | null {
  const s = raw.trim();
  if (s === '') return null;
  if (!/^\d{4}$/.test(s)) return Number.NaN;
  const n = Number(s);
  return n >= MIN_YEAR && n <= MAX_YEAR ? n : Number.NaN;
}

export interface ChapterNode<T> {
  chapter: T;
  children: ChapterNode<T>[];
}

/** Nests chapters by `level`: a chapter is the child of the closest earlier chapter with a lower level. A list that starts
 * deeper than level 1, or skips a level, still nests sensibly. */
export function nestChapters<T extends { level: number }>(chapters: T[]): ChapterNode<T>[] {
  const roots: ChapterNode<T>[] = [];
  const stack: ChapterNode<T>[] = [];
  for (const chapter of chapters) {
    const node: ChapterNode<T> = { chapter, children: [] };
    while (stack.length > 0 && stack[stack.length - 1]!.chapter.level >= chapter.level) stack.pop();
    if (stack.length === 0) roots.push(node);
    else stack[stack.length - 1]!.children.push(node);
    stack.push(node);
  }
  return roots;
}

/** "p. 3" / "pp. 3-5" - the pages of a chapter, or null when it has none. */
export function pageRange(t: TFunction, from: number | null, to: number | null): string | null {
  if (from == null) return null;
  return to == null || to === from ? t('knowledge.chapters.page', { page: from }) : t('knowledge.chapters.pages', { from, to });
}

/** The CSV the import accepts (ADR-0039 10): fictional rows, so nobody mistakes it for a customer's. */
export const SAMPLE_CSV = [
  'name,pattern,model,variant,year,theme,priority,enabled',
  'Acme Lumen sedan,ACMLS1*,lumen,sedan,vin,ice,10,true',
  'Acme Lumen electric,ACMLE1*,lumen,long-range,vin,ev,10,true',
  'Acme Orbit 2024,ACMOR1????*,orbit,,2024,,5,true',
  '',
].join('\r\n');

/** A `data:` link for the sample, so the download needs no object URL. The byte-order mark lets Excel read it as UTF-8. */
const BOM = String.fromCharCode(0xfeff);
export const sampleCsvHref = (): string => `data:text/csv;charset=utf-8,${encodeURIComponent(BOM + SAMPLE_CSV)}`;

/** The asset page address of a document, opened at a page when the chapter has one. */
export const assetHref = (assetId: string, page?: number | null): string =>
  page != null ? `/library/assets/${assetId}?page=${page}` : `/library/assets/${assetId}`;
