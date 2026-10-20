// Pure helpers for the AI admin page: the editable draft of the settings, what a save must send, whether the privacy
// acknowledgement is needed, and the backfill estimate. No React, so they are unit-tested directly.
import type { components } from '@dam/sdk-js';
import type { AiSettings, AiStatus } from '../aiApi';

type ProviderInfo = AiSettings['providers'][number];
export type AiFeatures = AiSettings['features'];
export type { ProviderInfo };

export const FEATURE_KEYS = ['tags', 'ocr', 'transcribe', 'embeddings'] as const;
export type FeatureKey = (typeof FEATURE_KEYS)[number];

/** What the page lets an administrator edit. `provider` is the stored override (`null` follows the server default). */
export interface Draft {
  enabled: boolean;
  provider: string | null;
  features: AiFeatures;
  /** Empty means automatic. */
  ocrLanguage: string;
}

export const toDraft = (s: AiSettings): Draft => ({
  enabled: s.enabled,
  provider: s.provider,
  features: { ...s.features },
  ocrLanguage: s.ocrLanguage ?? '',
});

export function sameDraft(a: Draft, b: Draft): boolean {
  return (
    a.enabled === b.enabled &&
    a.provider === b.provider &&
    a.ocrLanguage.trim() === b.ocrLanguage.trim() &&
    FEATURE_KEYS.every((k) => a.features[k] === b.features[k])
  );
}

/** The server's pattern for an OCR language hint (`en`, `hi`, `pt-br`, `zh_hans`); empty is allowed and means automatic. */
export const LANGUAGE_HINT = /^[a-z]{2,3}([-_][a-z]{2,4})?$/;
export const languageInvalid = (v: string) => v.trim() !== '' && !LANGUAGE_HINT.test(v.trim().toLowerCase());

/** Providers an administrator can choose between. `none` is not one: "no AI" is the switch. */
export const choosable = (s: AiSettings) => s.providers.filter((p) => p.kind !== 'none');

/** The provider the draft resolves to: its own choice, else the server default (which may be `none`, i.e. nothing). */
export function effectiveKind(d: Draft, s: AiSettings): string | null {
  const kind = d.provider ?? s.defaultProvider;
  return kind && kind !== 'none' ? kind : null;
}

export function selectedProvider(d: Draft, s: AiSettings): ProviderInfo | undefined {
  const kind = effectiveKind(d, s);
  return kind ? s.providers.find((p) => p.kind === kind) : undefined;
}

/** Why the draft cannot be switched on, if it cannot (the server answers 422 on `provider` for both). */
export function providerProblem(d: Draft, s: AiSettings): 'choose' | 'unconfigured' | null {
  if (!d.enabled) return null;
  const p = selectedProvider(d, s);
  if (!p) return 'choose';
  return p.configured ? null : 'unconfigured';
}

/** The privacy acknowledgement is needed when the draft leaves AI on with an off-site provider that is not already
 * the one AI is running with (the server asks again after AI has been switched off and on, or the provider changed). */
export function needsAcknowledgement(d: Draft, s: AiSettings): boolean {
  if (!d.enabled) return false;
  const p = selectedProvider(d, s);
  if (!p?.sendsDataOffSite) return false;
  const already = s.enabled && s.effectiveProvider === p.kind;
  return !already;
}

export function toRequest(d: Draft, acknowledgeOffSite: boolean): components['schemas']['AiSettingsRequest'] {
  const language = d.ocrLanguage.trim().toLowerCase();
  return {
    enabled: d.enabled,
    provider: d.provider,
    features: { ...d.features },
    ocrLanguage: language === '' ? null : language,
    acknowledgeOffSite,
  };
}

/** The server's field errors by camelCase field name (it may send either casing). */
export function fieldErrors(fields: Record<string, string[]>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [key, messages] of Object.entries(fields)) {
    const name = key.charAt(0).toLowerCase() + key.slice(1);
    if (messages[0] && !out[name]) out[name] = messages[0];
  }
  return out;
}

// -------------------------------------------------------------------------------------------------------- status

export type HealthTone = 'success' | 'info' | 'warning' | 'danger' | 'neutral';

export function healthTone(status: string): HealthTone {
  switch (status) {
    case 'ok':
      return 'success';
    case 'loading':
      return 'info';
    case 'degraded':
      return 'warning';
    case 'down':
    case 'unreachable':
      return 'danger';
    default:
      return 'neutral';
  }
}

/** Capabilities in display order. `later` ones are declared by the backend but have no feature yet. */
export const CAPABILITIES = [
  { key: 'tags', later: false },
  { key: 'ocr', later: false },
  { key: 'transcribe', later: false },
  { key: 'textEmbeddings', later: false },
  { key: 'imageEmbeddings', later: false },
  { key: 'translate', later: true },
  { key: 'summarise', later: true },
] as const;
export type CapabilityKey = (typeof CAPABILITIES)[number]['key'];

/** Whether the provider in use can do what a feature switch turns on (`embeddings` needs either kind of vector). */
export function featureSupported(key: FeatureKey, caps: AiStatus['capabilities']): boolean {
  return key === 'embeddings' ? caps.textEmbeddings || caps.imageEmbeddings : caps[key];
}

// ------------------------------------------------------------------------------------------------------ backfill

export const BACKFILL_TYPES = ['image', 'video', 'audio', 'document', 'manual'] as const;
export const BACKFILL_DEFAULT_LIMIT = 500;
export const BACKFILL_MAX_LIMIT = 5000;
/** About how long one image takes (the sidecar measures 0.3 to 1.5 s; video and audio take longer). */
export const SECONDS_PER_ASSET = 1.5;

export interface BackfillForm {
  types: string[];
  folderId: string;
  includeSubfolders: boolean;
  onlyMissing: boolean;
  limit: string;
}

export const initialBackfill = (): BackfillForm => ({
  types: [...BACKFILL_TYPES],
  folderId: '',
  includeSubfolders: true,
  onlyMissing: true,
  limit: String(BACKFILL_DEFAULT_LIMIT),
});

/** The limit as a whole number between 1 and the maximum, or `null` when it is not one. */
export function parseLimit(v: string): number | null {
  const s = v.trim();
  if (!/^\d+$/.test(s)) return null;
  const n = Number(s);
  return n >= 1 && n <= BACKFILL_MAX_LIMIT ? n : null;
}

export const filtered = (f: BackfillForm) => f.folderId !== '' || f.types.length !== BACKFILL_TYPES.length;

/** How many assets a run would queue at most, from the status counts (`null` while they are unknown). The counts are
 * for the whole library, so with a type or folder filter this is an upper bound. */
export function estimateCount(f: BackfillForm, status: AiStatus | undefined): number | null {
  const limit = parseLimit(f.limit);
  if (!status || limit === null) return null;
  const pool = f.onlyMissing ? status.counts.notStarted : status.counts.eligible;
  return Math.min(limit, pool);
}

export const estimateSeconds = (count: number) => Math.round(count * SECONDS_PER_ASSET);

export function backfillBody(f: BackfillForm): Partial<components['schemas']['AiBackfillInput']> {
  const everyType = f.types.length === BACKFILL_TYPES.length;
  return {
    types: everyType ? null : f.types,
    folderId: f.folderId === '' ? null : f.folderId,
    includeSubfolders: f.folderId === '' ? null : f.includeSubfolders,
    onlyMissing: f.onlyMissing,
    limit: parseLimit(f.limit),
  };
}
