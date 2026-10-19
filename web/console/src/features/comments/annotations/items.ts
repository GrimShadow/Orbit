import { MENTION_TOKEN, type CommentThread } from '../commentsApi';
import { annotationNumbers } from '../numbering';

/** A region or pin on an image/page, ready to draw. */
export interface ShapeItem {
  id: string;
  number: number;
  kind: 'region' | 'point';
  x: number;
  y: number;
  w: number;
  h: number;
  /** 1-based page for paged documents; null when the anchor carries none. */
  page: number | null;
  resolved: boolean;
  excerpt: string;
}

/** A timecode comment on a video, ready to place on the timeline. */
export interface TimeItem {
  id: string;
  number: number;
  startMs: number;
  endMs: number | null;
  resolved: boolean;
  excerpt: string;
}

/** The first few words of a comment for an accessible name or a caption: mention tokens read as `@Name`, whitespace is
 * collapsed, and anything long is cut at a word boundary with an ellipsis. */
export function excerpt(body: string, maxChars = 48): string {
  const plain = body
    .replace(MENTION_TOKEN, (_m, name: string) => `@${name}`)
    .replace(/\s+/g, ' ')
    .trim();
  if (plain.length <= maxChars) return plain;
  const cut = plain.slice(0, maxChars);
  const space = cut.lastIndexOf(' ');
  return `${(space > maxChars / 2 ? cut.slice(0, space) : cut).replace(/[\s.,;:!?-]+$/, '')}…`;
}

/** Region/pin items for one version. `threads` must be that version's UNFILTERED list so the numbers match the cards. */
export function shapeItems(threads: readonly CommentThread[], versionId: string | null): ShapeItem[] {
  if (!versionId) return [];
  const numbers = annotationNumbers(threads);
  const out: ShapeItem[] = [];
  for (const t of threads) {
    const a = t.root.anchor;
    const n = numbers.get(t.root.id);
    if (!a || n === undefined || t.root.deleted || t.root.versionId !== versionId) continue;
    if (a.type === 'region' && a.x != null && a.y != null && a.w != null && a.h != null)
      out.push({
        id: t.root.id,
        number: n,
        kind: 'region',
        x: a.x,
        y: a.y,
        w: a.w,
        h: a.h,
        page: a.page ?? null,
        resolved: t.root.resolved,
        excerpt: excerpt(t.root.body),
      });
    else if (a.type === 'point' && a.x != null && a.y != null)
      out.push({
        id: t.root.id,
        number: n,
        kind: 'point',
        x: a.x,
        y: a.y,
        w: 0,
        h: 0,
        page: a.page ?? null,
        resolved: t.root.resolved,
        excerpt: excerpt(t.root.body),
      });
  }
  return out;
}

export function timeItems(threads: readonly CommentThread[], versionId: string | null): TimeItem[] {
  if (!versionId) return [];
  const numbers = annotationNumbers(threads);
  const out: TimeItem[] = [];
  for (const t of threads) {
    const a = t.root.anchor;
    const n = numbers.get(t.root.id);
    if (!a || n === undefined || t.root.deleted || t.root.versionId !== versionId) continue;
    if (a.type === 'timecode' && a.startMs != null)
      out.push({
        id: t.root.id,
        number: n,
        startMs: a.startMs,
        endMs: a.endMs ?? null,
        resolved: t.root.resolved,
        excerpt: excerpt(t.root.body),
      });
  }
  return out;
}

/** The items to draw on one page of a paged document (an anchor without a page belongs to page 1), or all of them for a
 * plain image (`page` undefined). */
export function onPage(items: readonly ShapeItem[], page: number | undefined): ShapeItem[] {
  return page === undefined ? [...items] : items.filter((i) => (i.page ?? 1) === page);
}

/** Stacking order for overlapping shapes: the smaller a shape, the higher it sits, so a small region inside a large one
 * stays clickable. DOM order (= number order = Tab order) is left alone; this is only a rank for `z-index`. */
export function stackRanks(items: readonly ShapeItem[]): Map<string, number> {
  const bySize = [...items].sort((a, b) => b.w * b.h - a.w * a.h || a.number - b.number);
  return new Map(bySize.map((it, i) => [it.id, i + 1]));
}

/** How long after a single moment its caption stays up while playing. */
export const POINT_CAPTION_MS = 2000;

/** The timecode threads whose caption should be showing at `timeMs`: inside a range, or within ~2 s after a moment. */
export function activeAt(items: readonly TimeItem[], timeMs: number): TimeItem[] {
  return items.filter((i) => !i.resolved && timeMs >= i.startMs && timeMs <= (i.endMs ?? i.startMs + POINT_CAPTION_MS));
}

/** Rows for timeline markers so neighbours do not sit on top of each other: each marker takes the first lane whose last
 * marker has finished (plus `gap`, a fraction of the duration). More than `maxLanes` overlapping wrap round. */
export function assignLanes(items: readonly TimeItem[], durationMs: number, gap = 0.045, maxLanes = 3): Map<string, number> {
  const lanes = new Map<string, number>();
  if (durationMs <= 0) return lanes;
  const ends: number[] = [];
  for (const it of [...items].sort((a, b) => a.startMs - b.startMs || a.number - b.number)) {
    const end = Math.max(it.endMs ?? it.startMs, it.startMs) + gap * durationMs;
    let lane = ends.findIndex((e) => e <= it.startMs);
    if (lane === -1) lane = ends.length < maxLanes ? ends.length : lanes.size % maxLanes;
    ends[lane] = end;
    lanes.set(it.id, lane);
  }
  return lanes;
}
