import { CHAPTER_LIMIT, CHAPTER_TITLE_MAX } from './knowledgeTypes';
import type {
  ChapterDto,
  DocType,
  KnowledgeDocumentDto,
  KnowledgeDocumentInput,
  VinRuleDto,
  VinRuleInput,
} from './knowledgeTypes';
import { checkPattern, isCode, normalizeCode, parseYear, type PatternProblem } from './knowledgeModel';

// Form drafts for the three editors (document mapping, VIN rule, chapters): what the person typed, how it is checked, and
// the body it becomes. Pure, so the rules are tested on their own.

// ---- document mapping -----------------------------------------------------------------------------------------------

export interface DocDraft {
  docType: DocType;
  model: string;
  variant: string;
  yearFrom: string;
  yearTo: string;
  language: string;
  theme: string;
}

export const emptyDocDraft = (language = 'en'): DocDraft => ({
  docType: 'owner_manual',
  model: '',
  variant: '',
  yearFrom: '',
  yearTo: '',
  language,
  theme: '',
});

export const docToDraft = (d: KnowledgeDocumentDto): DocDraft => ({
  docType: d.docType,
  model: d.model,
  variant: d.variant ?? '',
  yearFrom: d.modelYearFrom == null ? '' : String(d.modelYearFrom),
  yearTo: d.modelYearTo == null ? '' : String(d.modelYearTo),
  language: d.language,
  theme: d.theme ?? '',
});

export type DocErrors = Partial<
  Record<'asset' | 'model' | 'variant' | 'yearFrom' | 'yearTo' | 'years' | 'language' | 'theme', string>
>;

/** Error codes (i18n keys under `knowledge.errors.`) per field; empty when the draft can be sent. */
export function validateDocDraft(d: DocDraft, hasAsset: boolean): DocErrors {
  const errors: DocErrors = {};
  if (!hasAsset) errors.asset = 'assetRequired';
  const model = normalizeCode(d.model);
  if (model === '') errors.model = 'modelRequired';
  else if (!isCode(model)) errors.model = 'codeInvalid';
  const variant = normalizeCode(d.variant);
  if (variant !== '' && !isCode(variant)) errors.variant = 'codeInvalid';
  const theme = normalizeCode(d.theme);
  if (theme !== '' && !isCode(theme)) errors.theme = 'codeInvalid';
  if (d.language.trim() === '') errors.language = 'languageRequired';
  const from = parseYear(d.yearFrom);
  const to = parseYear(d.yearTo);
  if (Number.isNaN(from)) errors.yearFrom = 'yearInvalid';
  if (Number.isNaN(to)) errors.yearTo = 'yearInvalid';
  if (!errors.yearFrom && !errors.yearTo) {
    if ((from === null) !== (to === null)) errors.years = 'yearsBoth';
    else if (from !== null && to !== null && from > to) errors.years = 'yearsOrder';
  }
  return errors;
}

export function docDraftToInput(d: DocDraft, assetId?: string): KnowledgeDocumentInput {
  return {
    assetId,
    docType: d.docType,
    model: normalizeCode(d.model),
    variant: normalizeCode(d.variant) || null,
    modelYearFrom: parseYear(d.yearFrom),
    modelYearTo: parseYear(d.yearTo),
    language: d.language.trim(),
    theme: normalizeCode(d.theme) || null,
  };
}

// ---- VIN rules -------------------------------------------------------------------------------------------------------

export interface RuleDraft {
  name: string;
  pattern: string;
  model: string;
  variant: string;
  /** Read the year from the VIN's tenth character, or use `modelYear`. */
  yearMode: 'vin' | 'fixed';
  modelYear: string;
  theme: string;
  priority: string;
  enabled: boolean;
}

export const emptyRuleDraft = (): RuleDraft => ({
  name: '',
  pattern: '',
  model: '',
  variant: '',
  yearMode: 'vin',
  modelYear: '',
  theme: '',
  priority: '0',
  enabled: true,
});

export const ruleToDraft = (r: VinRuleDto): RuleDraft => ({
  name: r.name,
  pattern: r.pattern,
  model: r.model,
  variant: r.variant ?? '',
  yearMode: r.modelYear != null && !r.yearFromVin ? 'fixed' : 'vin',
  modelYear: r.modelYear == null ? '' : String(r.modelYear),
  theme: r.theme ?? '',
  priority: String(r.priority),
  enabled: r.enabled,
});

export const RULE_NAME_MAX = 200;
const PRIORITY = /^-?\d{1,9}$/;

export type RuleErrors = Partial<
  Record<'name' | 'pattern' | 'model' | 'variant' | 'modelYear' | 'theme' | 'priority', string>
> & { patternDetail?: string };

export function validateRuleDraft(d: RuleDraft): RuleErrors {
  const errors: RuleErrors = {};
  if (d.name.trim() === '') errors.name = 'nameRequired';
  else if (d.name.trim().length > RULE_NAME_MAX) errors.name = 'nameLong';
  const check = checkPattern(d.pattern.trim());
  if (!check.ok) {
    errors.pattern = check.problem satisfies PatternProblem;
    errors.patternDetail = check.detail;
  }
  const model = normalizeCode(d.model);
  if (model === '') errors.model = 'modelRequired';
  else if (!isCode(model)) errors.model = 'codeInvalid';
  const variant = normalizeCode(d.variant);
  if (variant !== '' && !isCode(variant)) errors.variant = 'codeInvalid';
  const theme = normalizeCode(d.theme);
  if (theme !== '' && !isCode(theme)) errors.theme = 'codeInvalid';
  if (d.yearMode === 'fixed') {
    const y = parseYear(d.modelYear);
    if (y === null) errors.modelYear = 'yearRequired';
    else if (Number.isNaN(y)) errors.modelYear = 'yearInvalid';
  }
  if (!PRIORITY.test(d.priority.trim())) errors.priority = 'priorityInvalid';
  return errors;
}

export function ruleDraftToInput(d: RuleDraft): VinRuleInput {
  const year = d.yearMode === 'fixed' ? parseYear(d.modelYear) : null;
  return {
    name: d.name.trim(),
    pattern: d.pattern.trim().toUpperCase(),
    model: normalizeCode(d.model),
    variant: normalizeCode(d.variant) || null,
    modelYear: year !== null && !Number.isNaN(year) ? year : null,
    yearFromVin: d.yearMode === 'vin',
    theme: normalizeCode(d.theme) || null,
    priority: Number(d.priority.trim()),
    enabled: d.enabled,
  };
}

/** The body of an update that only flips `enabled`. */
export const ruleToInput = (r: VinRuleDto, enabled: boolean): VinRuleInput => ({
  name: r.name,
  pattern: r.pattern,
  model: r.model,
  variant: r.variant,
  modelYear: r.modelYear,
  yearFromVin: r.yearFromVin,
  theme: r.theme,
  priority: r.priority,
  enabled,
});

// ---- chapters -------------------------------------------------------------------------------------------------------

export const MAX_LEVEL = 6;

export interface ChapterRow {
  /** Only for React keys while rows move; never sent. */
  key: number;
  title: string;
  level: string;
  pageFrom: string;
  pageTo: string;
  /** Kept from the chapter this row came from, so editing a title does not lose an HTML anchor. */
  anchor: string | null;
}

let rowKey = 1;
export const newChapterRow = (over: Partial<ChapterRow> = {}): ChapterRow => ({
  key: rowKey++,
  title: '',
  level: '1',
  pageFrom: '',
  pageTo: '',
  anchor: null,
  ...over,
});

export const chaptersToRows = (chapters: ChapterDto[]): ChapterRow[] =>
  chapters.map((c) =>
    newChapterRow({
      title: c.title,
      level: String(c.level),
      pageFrom: c.pageFrom == null ? '' : String(c.pageFrom),
      pageTo: c.pageTo == null ? '' : String(c.pageTo),
      anchor: c.anchor,
    }),
  );

const wholeNumber = (s: string): number | null => {
  const v = s.trim();
  if (v === '') return null;
  return /^\d{1,6}$/.test(v) ? Number(v) : Number.NaN;
};

export type RowErrors = Partial<Record<'title' | 'level' | 'pageFrom' | 'pageTo', string>>;

/** Per-row error codes by row key, plus a list-level one (too many rows). */
export function validateChapterRows(rows: ChapterRow[]): { rows: Map<number, RowErrors>; list?: string } {
  const out = new Map<number, RowErrors>();
  for (const r of rows) {
    const e: RowErrors = {};
    const title = r.title.trim();
    if (title === '') e.title = 'titleRequired';
    else if (title.length > CHAPTER_TITLE_MAX) e.title = 'titleLong';
    const level = wholeNumber(r.level);
    if (level === null || Number.isNaN(level) || level < 1 || level > MAX_LEVEL) e.level = 'levelInvalid';
    const from = wholeNumber(r.pageFrom);
    const to = wholeNumber(r.pageTo);
    if (from !== null && (Number.isNaN(from) || from < 1)) e.pageFrom = 'pageInvalid';
    if (to !== null && (Number.isNaN(to) || to < 1)) e.pageTo = 'pageInvalid';
    if (!e.pageFrom && !e.pageTo) {
      if (to !== null && from === null) e.pageFrom = 'pageFromRequired';
      else if (from !== null && to !== null && to < from) e.pageTo = 'pageOrder';
    }
    if (Object.keys(e).length > 0) out.set(r.key, e);
  }
  return { rows: out, list: rows.length > CHAPTER_LIMIT ? 'tooMany' : undefined };
}

export const chapterRowsToDtos = (rows: ChapterRow[]): ChapterDto[] =>
  rows.map((r) => {
    const from = wholeNumber(r.pageFrom);
    const to = wholeNumber(r.pageTo);
    return {
      title: r.title.trim(),
      anchor: r.anchor,
      level: Number(r.level.trim()),
      pageFrom: from,
      pageTo: from === null ? null : (to ?? from),
    };
  });

/** Moves the row at `index` by `delta` places; out-of-range moves leave the list as it is. */
export function moveRow<T>(rows: T[], index: number, delta: -1 | 1): T[] {
  const to = index + delta;
  if (to < 0 || to >= rows.length) return rows;
  const next = rows.slice();
  [next[index], next[to]] = [next[to]!, next[index]!];
  return next;
}
