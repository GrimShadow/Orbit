// Pure helpers for the glossary: parsing a pasted list of terms, and checking one term before it is sent. No React.
import type { GlossaryMode, GlossaryTermInput } from '../translation/translationTypes';

/** Terms one paste may import: they are created one request at a time. */
export const IMPORT_MAX_LINES = 500;

export type LineErrorCode =
  'emptyTerm' | 'badSegment' | 'unknownLanguage' | 'duplicateLanguage' | 'emptyRendering' | 'duplicateTerm';

export interface LineError {
  /** 1-based, counting blank lines too, so it matches the line the person sees. */
  line: number;
  text: string;
  code: LineErrorCode;
  /** Values for the message ("hi", the other line number). */
  detail?: string;
}

export interface ParsedLine {
  line: number;
  input: GlossaryTermInput;
}

export interface ParsedGlossary {
  entries: ParsedLine[];
  errors: LineError[];
  /** More non-blank lines than one import takes: the rest are ignored. */
  truncated: boolean;
}

/**
 * `Acme Lumen` (a term alone) is a locked term, kept exactly as written in every language. `Acme Lumen | hi=ऐक्मे ल्यूमेन |
 * ta=...` is a mapped term with a fixed rendering per language. Blank lines are skipped. A line with a problem is reported
 * with its number and left out; the other lines are still imported.
 */
export function parseGlossaryLines(text: string, languages: readonly string[]): ParsedGlossary {
  const entries: ParsedLine[] = [];
  const errors: LineError[] = [];
  const seen = new Map<string, number>();
  let nonBlank = 0;
  let truncated = false;

  for (const [index, raw] of text.split(/\r?\n/).entries()) {
    const line = index + 1;
    if (raw.trim() === '') continue;
    nonBlank += 1;
    if (nonBlank > IMPORT_MAX_LINES) {
      truncated = true;
      break;
    }
    const [head = '', ...rest] = raw.split('|');
    const term = head.trim();
    const fail = (code: LineErrorCode, detail?: string) => errors.push({ line, text: raw.trim(), code, detail });

    if (term === '') {
      fail('emptyTerm');
      continue;
    }

    const translations: Record<string, string> = {};
    let bad = false;
    for (const segment of rest) {
      const at = segment.indexOf('=');
      const code = at < 0 ? '' : segment.slice(0, at).trim().toLowerCase();
      const rendering = at < 0 ? '' : segment.slice(at + 1).trim();
      if (at < 0 || code === '') {
        fail('badSegment', segment.trim());
        bad = true;
        break;
      }
      if (!languages.includes(code)) {
        fail('unknownLanguage', code);
        bad = true;
        break;
      }
      if (code in translations) {
        fail('duplicateLanguage', code);
        bad = true;
        break;
      }
      if (rendering === '') {
        fail('emptyRendering', code);
        bad = true;
        break;
      }
      translations[code] = rendering;
    }
    if (bad) continue;

    const key = term;
    const first = seen.get(key);
    if (first !== undefined) {
      fail('duplicateTerm', String(first));
      continue;
    }
    seen.set(key, line);

    const mode: GlossaryMode = Object.keys(translations).length > 0 ? 'mapped' : 'locked';
    entries.push({ line, input: { term, mode, translations, note: null } });
  }
  return { entries, errors, truncated };
}

export interface TermDraft {
  term: string;
  mode: GlossaryMode;
  note: string;
  /** One row per language rendering; `language` may be empty while the person is still choosing. */
  rows: { language: string; text: string }[];
}

export const emptyDraft = (): TermDraft => ({ term: '', mode: 'locked', note: '', rows: [] });

export type DraftErrors = Partial<Record<'term' | 'rows', 'termRequired' | 'needRendering' | 'rowIncomplete' | 'rowDuplicate'>>;

/** What is wrong with a term before it is sent. A locked term has no renderings; a mapped one needs at least one. */
export function validateDraft(d: TermDraft): DraftErrors {
  const errors: DraftErrors = {};
  if (d.term.trim() === '') errors.term = 'termRequired';
  if (d.mode === 'mapped') {
    const languages = d.rows.map((r) => r.language);
    if (d.rows.length === 0) errors.rows = 'needRendering';
    else if (d.rows.some((r) => r.language === '' || r.text.trim() === '')) errors.rows = 'rowIncomplete';
    else if (new Set(languages).size !== languages.length) errors.rows = 'rowDuplicate';
  }
  return errors;
}

export function toInput(d: TermDraft): GlossaryTermInput {
  return {
    term: d.term.trim(),
    mode: d.mode,
    translations: d.mode === 'mapped' ? Object.fromEntries(d.rows.map((r) => [r.language, r.text.trim()])) : {},
    note: d.note.trim() === '' ? null : d.note.trim(),
  };
}

export function toDraft(term: {
  term: string;
  mode: GlossaryMode;
  note: string | null;
  translations: Record<string, string>;
}): TermDraft {
  return {
    term: term.term,
    mode: term.mode,
    note: term.note ?? '',
    rows: Object.entries(term.translations).map(([language, text]) => ({ language, text })),
  };
}
