import type { AiTranscript, AssetAi } from '../aiApi';
import { formatTimestamp } from '../aiApi';

export type Segment = AiTranscript['segments'][number];

/** The asset types the enrichment pipeline looks at (anything else is recorded as skipped and has nothing to show). */
export const AI_TYPES: ReadonlySet<string> = new Set(['image', 'video', 'audio', 'document', 'manual']);

/** The steps a run goes through, in order, per asset type (ADR-0035) - what "step 2 of 5" counts against. */
export function expectedSteps(type: string): string[] {
  switch (type) {
    case 'video':
      return ['tags', 'image-embedding', 'transcript', 'language', 'text-embedding'];
    case 'audio':
      return ['transcript', 'language', 'text-embedding'];
    case 'document':
    case 'manual':
      return ['tags', 'image-embedding', 'text-layer', 'ocr', 'language', 'text-embedding'];
    default:
      return ['tags', 'ocr', 'image-embedding', 'language', 'text-embedding'];
  }
}

/** The thing the panel has nothing but a shrug for: no run, no suggestions, no text, no transcript. */
export function hasAiContent(ai: AssetAi | undefined): boolean {
  if (!ai) return false;
  return (
    ai.suggestions.length > 0 ||
    !!ai.ocrText ||
    (ai.transcript?.segments.length ?? 0) > 0 ||
    ai.embeddingsReady ||
    ['pending', 'running', 'ready', 'failed'].includes(ai.status)
  );
}

/** "en" -> "English" in the console's language; the code itself when the runtime does not know it. */
export function languageName(code: string, locale: string): string {
  try {
    const name = new Intl.DisplayNames([locale], { type: 'language' }).of(code);
    return name && name !== code ? name : code;
  } catch {
    return code;
  }
}

/** The segment being spoken at `ms` (start inclusive, end exclusive); -1 in a pause or outside the recording. */
export function segmentIndexAt(segments: readonly Segment[], ms: number): number {
  let lo = 0;
  let hi = segments.length - 1;
  let found = -1;
  // The segments are in time order: find the last one that starts at or before `ms`, then check that it has not ended.
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (segments[mid]!.startMs <= ms) {
      found = mid;
      lo = mid + 1;
    } else hi = mid - 1;
  }
  return found >= 0 && ms < segments[found]!.endMs ? found : -1;
}

/**
 * The segment a `?t=<seconds>` link means. Links carry whole seconds (a segment that starts at 83.4 s is linked as `t=83`),
 * so a segment that STARTS inside that second wins over the previous one still running at 83.0; failing that, the one being
 * spoken then; failing that (a pause), the last one that began before it. -1 for an empty transcript or a time before it begins.
 */
export function deepLinkIndex(segments: readonly Segment[], ms: number): number {
  const starting = segments.findIndex((s) => s.startMs >= ms && s.startMs < ms + 1000);
  if (starting >= 0) return starting;
  const at = segmentIndexAt(segments, ms);
  if (at >= 0) return at;
  let before = -1;
  segments.forEach((s, i) => {
    if (s.startMs <= ms) before = i;
  });
  return before;
}

/** Where to put the playhead for a `?t=` link: the start of the line it means when it really falls on one, else the time itself. */
export function deepLinkSeekMs(segments: readonly Segment[] | undefined, ms: number): number {
  const s = segments?.[deepLinkIndex(segments, ms)];
  const onIt = s && ((s.startMs >= ms && s.startMs < ms + 1000) || (s.startMs <= ms && ms < s.endMs));
  return onIt ? s.startMs : ms;
}

/** `?t=83` / `?t=83.5` -> milliseconds. Anything that is not a plain, non-negative, sane number of seconds is no link at all. */
export function parseDeepLinkTime(value: string | null): number | null {
  if (value === null || !/^\d{1,6}(\.\d{1,3})?$/.test(value.trim())) return null;
  const seconds = Number(value);
  return Number.isFinite(seconds) && seconds <= 24 * 3600 ? Math.round(seconds * 1000) : null;
}

/** What "Copy transcript" puts on the clipboard: one line per segment, `[1:23] words`. */
export function transcriptAsText(segments: readonly Segment[]): string {
  return segments.map((s) => `[${formatTimestamp(s.startMs)}] ${s.text.trim()}`).join('\n');
}

const norm = (s: string) => s.normalize('NFKD').replace(/\p{M}/gu, '').toLowerCase();

/** The segments whose text contains `query` (case and accent blind); everything for an empty query. Keeps the index into the full list. */
export function filterSegments(segments: readonly Segment[], query: string): { segment: Segment; index: number }[] {
  const q = norm(query.trim());
  const all = segments.map((segment, index) => ({ segment, index }));
  return q ? all.filter(({ segment }) => norm(segment.text).includes(q)) : all;
}

/** Splits `text` into runs, marking the ones that match `query` (case-insensitively), so the filter can highlight them. */
export function matchParts(text: string, query: string): { text: string; match: boolean }[] {
  const q = query.trim();
  if (!q) return [{ text, match: false }];
  const lower = text.toLowerCase();
  const needle = q.toLowerCase();
  // Lower-casing can change a string's length (a few scripts); fall back to no highlight rather than cut in the wrong place.
  if (lower.length !== text.length) return [{ text, match: false }];
  const parts: { text: string; match: boolean }[] = [];
  let from = 0;
  for (let at = lower.indexOf(needle); at >= 0; at = lower.indexOf(needle, from)) {
    if (at > from) parts.push({ text: text.slice(from, at), match: false });
    parts.push({ text: text.slice(at, at + needle.length), match: true });
    from = at + needle.length;
  }
  if (from < text.length) parts.push({ text: text.slice(from), match: false });
  return parts.length > 0 ? parts : [{ text, match: false }];
}

/** 850 -> "850 ms", 1234 -> "1.2 s", 125000 -> "2 min 5 s". */
export function formatStepDuration(ms: number): string {
  if (ms < 1000) return `${Math.max(0, Math.round(ms))} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  const total = Math.round(ms / 1000);
  return `${Math.floor(total / 60)} min ${total % 60} s`;
}

/** The confidence as a whole percent for display (0..100), and as a bar width that stays visible for a small score. */
export function percent(confidence: number): number {
  return Math.min(100, Math.max(0, Math.round(confidence * 100)));
}
