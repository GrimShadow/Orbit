// Step 3.1 (ADR-0039): the knowledge module's DTOs with the narrower types the console wants (document types as a union, nullable
// fields). Field names match the generated DTOs in web/sdk-js exactly; the API client's responses are cast to these.

/** The kinds of document a vehicle app can look up. */
export const DOC_TYPES = ['owner_manual', 'faq', 'service', 'training', 'product_info'] as const;
export type DocType = (typeof DOC_TYPES)[number];

/** Assets of these types can be mapped as knowledge documents (a PDF manual, a document, an HTML5 bundle). */
export const KNOWLEDGE_ASSET_TYPES = ['manual', 'document', 'html5'] as const;
export const isKnowledgeAssetType = (type: string): boolean => (KNOWLEDGE_ASSET_TYPES as readonly string[]).includes(type);

/** Where a document's chapters came from: read from a PDF's bookmarks (`pdf`), from HTML headings (`html`), typed in (`manual`) or not found (`none`). */
export type ChaptersSource = 'pdf' | 'html' | 'manual' | 'none' | string;

export interface ChapterDto {
  title: string;
  anchor: string | null;
  level: number;
  pageFrom: number | null;
  pageTo: number | null;
}

export interface KnowledgeDocumentDto {
  id: string;
  assetId: string;
  title: string;
  assetType: string;
  assetStatus: string;
  docType: DocType;
  model: string;
  variant: string | null;
  modelYearFrom: number | null;
  modelYearTo: number | null;
  language: string;
  theme: string | null;
  chapterCount: number;
  chaptersSource: ChaptersSource;
  chaptersIndexedAt: string | null;
  currentVersionId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface KnowledgeDocumentDetailDto extends KnowledgeDocumentDto {
  chapters: ChapterDto[];
}

/** The body of a create (POST) or update (PUT; `assetId` is fixed once mapped). */
export interface KnowledgeDocumentInput {
  assetId?: string;
  docType: DocType;
  model: string;
  variant: string | null;
  modelYearFrom: number | null;
  modelYearTo: number | null;
  language: string;
  theme: string | null;
}

export interface DocumentFilters {
  type: string;
  model: string;
  variant: string;
  year: string;
  lang: string;
  q: string;
}

export interface VinRuleDto {
  id: string;
  name: string;
  pattern: string;
  model: string;
  variant: string | null;
  modelYear: number | null;
  yearFromVin: boolean;
  theme: string | null;
  priority: number;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface VinRuleInput {
  name: string;
  pattern: string;
  model: string;
  variant: string | null;
  modelYear: number | null;
  yearFromVin: boolean;
  theme: string | null;
  priority: number;
  enabled: boolean;
}

export interface VehicleDto {
  model: string;
  variant: string | null;
  modelYear: number | null;
  theme: string | null;
}

export interface ResolveDto {
  vin: string;
  rule: { id: string; name: string; priority: number };
  vehicle: VehicleDto;
  document: KnowledgeDocumentDetailDto & { versionId?: string | null; versionNumber: number | null };
  otherLanguages: { language: string; documentId: string; assetId: string }[];
}

export interface VinTestCandidate {
  ruleId: string;
  name: string;
  priority: number;
  specificity: number;
  matched: boolean;
  reason: string;
}

export interface VinTestDto {
  vin: string;
  valid: boolean;
  chosen: { ruleId: string; name: string } | null;
  vehicle: VehicleDto | null;
  candidates: VinTestCandidate[];
  /** Why the VIN could not be tested (malformed), else null. */
  reason?: string | null;
}

export interface VinImportError {
  line: number;
  message: string;
}

export interface VinImportResultDto {
  created: number;
  updated: number;
  skipped: number;
  errors: VinImportError[];
  dryRun: boolean;
}

/** What GET .../chapters returns: the chapters with where they came from. */
export interface ChaptersDto {
  source: ChaptersSource;
  indexedAt: string | null;
  chapters: ChapterDto[];
}

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

/** Rules a tenant may hold (ADR-0039). */
export const VIN_RULE_LIMIT = 5000;
/** Chapters one document may hold (ADR-0039). */
export const CHAPTER_LIMIT = 500;
export const CHAPTER_TITLE_MAX = 300;
