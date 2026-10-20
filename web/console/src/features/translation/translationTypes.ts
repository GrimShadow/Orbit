// The Step 2.2 contract (ADR-0036) with the narrower types the console wants (statuses and modes as unions, nullable notes).
// Field names match the generated DTOs in web/sdk-js exactly; the API client's responses are cast to these.

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

export type TranslationJobStatus = 'pending' | 'running' | 'succeeded' | 'partial' | 'failed';
export type TranslationItemStatus = 'ok' | 'skipped' | 'failed';

export interface TranslationItem {
  field: string;
  language: string;
  status: TranslationItemStatus;
  reason?: string | null;
}

export interface TranslationJob {
  id: string;
  assetId: string;
  status: TranslationJobStatus;
  fields: string[];
  targetLanguages: string[];
  sourceLanguage: string | null;
  overwrite: boolean;
  requestedBy: string | null;
  createdAt: string;
  finishedAt: string | null;
  items: TranslationItem[];
  /** Why the whole job could not run, when it could not. */
  error?: string | null;
}

export interface TranslationJobs {
  jobs: TranslationJob[];
}

export interface TranslateRequest {
  fields: string[];
  targetLanguages: string[];
  sourceLanguage?: string | null;
  overwrite?: boolean;
}

/** The optional body of accepting a translation suggestion. `text` replaces the suggested text; `replace` confirms
 * overwriting a target language that already has text (otherwise the server answers 409). */
export interface AcceptTranslationBody {
  text?: string;
  replace?: boolean;
}

export type GlossaryMode = 'locked' | 'mapped';

export interface GlossaryTerm {
  id: string;
  term: string;
  mode: GlossaryMode;
  translations: Record<string, string>;
  note: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface GlossaryTermInput {
  term: string;
  mode: GlossaryMode;
  translations: Record<string, string>;
  note: string | null;
}

export interface AiReviewItem {
  assetId: string;
  title: string;
  assetType: string;
  pending: number;
  languages: string[];
  fields: string[];
  requestedBy?: string | null;
  oldestAt: string;
}

export interface ReviewSummary {
  translation: number;
}

/** A job that is still being worked on: the console keeps checking until it is not. */
export const jobActive = (j: Pick<TranslationJob, 'status'>) => j.status === 'pending' || j.status === 'running';
