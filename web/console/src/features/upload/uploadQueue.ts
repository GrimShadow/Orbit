/** The pre-upload queue: what the user has chosen to upload, with the same type/size/extension rules the server applies
 * (`Dam.Domain.Assets.UploadPolicy`) so a problem shows next to the file *before* anything is sent. The server still
 * re-checks everything - this is a courtesy, never the gate. */

export const ASSET_TYPES = ['image', 'video', 'audio', 'document', 'manual', '3d', 'html5', 'other'] as const;
export const MAX_FILES = 500; // BulkUploadValidator.MaxFiles

const GIB = 1024 ** 3;
const EXTENSIONS: Record<string, string[]> = {
  image: ['jpg', 'jpeg', 'png', 'gif', 'webp', 'avif', 'tif', 'tiff', 'bmp', 'svg', 'heic', 'heif', 'psd'],
  video: ['mp4', 'mov', 'm4v', 'webm', 'mkv', 'avi', 'mxf'],
  audio: ['mp3', 'wav', 'aac', 'm4a', 'flac', 'ogg'],
  document: ['pdf', 'doc', 'docx', 'ppt', 'pptx', 'xls', 'xlsx', 'txt', 'rtf', 'csv', 'odt', 'ods', 'odp'],
  manual: ['pdf', 'html', 'htm', 'epub', 'zip'],
  '3d': ['glb', 'gltf', 'usdz', 'obj', 'fbx', 'zip'],
  html5: ['zip'],
};
const BLOCKED = new Set([
  'exe',
  'dll',
  'msi',
  'bat',
  'cmd',
  'com',
  'scr',
  'ps1',
  'vbs',
  'jar',
  'app',
  'dmg',
  'apk',
  'so',
  'sh',
  'pif',
]);
const MAX_BYTES: Record<string, number> = {
  image: 2 * GIB,
  video: 20 * GIB,
  audio: 2 * GIB,
  document: 2 * GIB,
  manual: 2 * GIB,
  '3d': 5 * GIB,
  html5: 1 * GIB,
  other: 5 * GIB,
};
/** Images a browser can show as a preview straight from the local file. */
const PREVIEWABLE = new Set(['jpg', 'jpeg', 'png', 'gif', 'webp', 'avif', 'bmp', 'svg']);
const PREVIEW_MAX_BYTES = 25 * 1024 * 1024;

export type Problem = 'blocked' | 'noExtension' | 'empty' | 'tooLarge';

export interface QueueItem {
  id: string;
  file: File;
  title: string;
  type: string;
  problem: Problem | null;
  /** Object URL for an image preview; revoke with `revokePreview` when the item leaves the queue. */
  previewUrl?: string;
}

export function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return dot < 0 || dot === fileName.length - 1 ? '' : fileName.slice(dot + 1).toLowerCase();
}

/** The type the server would guess (`UploadPolicy.InferType`); null when the file can never be accepted. */
export function inferType(fileName: string): string | null {
  const ext = extensionOf(fileName);
  if (ext === '' || BLOCKED.has(ext)) return null;
  if (ext === 'zip') return 'other'; // ambiguous - the uploader must choose
  return ['image', 'video', 'audio', 'document', '3d'].find((t) => EXTENSIONS[t]!.includes(ext)) ?? 'other';
}

/** Every type this file may be uploaded as, best guess first and the catch-all "other" last. */
export function typeChoices(fileName: string): string[] {
  const ext = extensionOf(fileName);
  if (ext === '' || BLOCKED.has(ext)) return [];
  const accepting = ASSET_TYPES.filter((t) => t !== 'other' && EXTENSIONS[t]!.includes(ext));
  const guess = inferType(fileName);
  return [...new Set([...(guess && guess !== 'other' ? [guess] : []), ...accepting, 'other'])];
}

export function problemFor(fileName: string, size: number, type: string): Problem | null {
  const ext = extensionOf(fileName);
  if (ext === '') return 'noExtension';
  if (BLOCKED.has(ext)) return 'blocked';
  if (size === 0) return 'empty';
  if (size > (MAX_BYTES[type] ?? MAX_BYTES.other!)) return 'tooLarge';
  return null;
}

export function titleFromName(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  const base = dot > 0 ? fileName.slice(0, dot) : fileName;
  return base.trim() || fileName;
}

let nextId = 0;

function previewFor(file: File, type: string): string | undefined {
  if (type !== 'image' || !PREVIEWABLE.has(extensionOf(file.name)) || file.size > PREVIEW_MAX_BYTES) return undefined;
  try {
    return typeof URL.createObjectURL === 'function' ? URL.createObjectURL(file) : undefined;
  } catch {
    return undefined; // a preview is a nicety - never let it stop a file from being queued
  }
}

export function makeQueueItem(file: File): QueueItem {
  const type = inferType(file.name) ?? 'other';
  return {
    id: `q${nextId++}`,
    file,
    title: titleFromName(file.name),
    type,
    problem: problemFor(file.name, file.size, type),
    previewUrl: previewFor(file, type),
  };
}

export function revokePreview(item: Pick<QueueItem, 'previewUrl'>) {
  if (item.previewUrl && typeof URL.revokeObjectURL === 'function') URL.revokeObjectURL(item.previewUrl);
}

/** Re-evaluates a file after the user picked another type for it. */
export function withType(item: QueueItem, type: string): QueueItem {
  return { ...item, type, problem: problemFor(item.file.name, item.file.size, type) };
}

const sameFile = (a: File, b: File) => a.name === b.name && a.size === b.size && a.lastModified === b.lastModified;

/** Adds files to the queue, skipping ones already queued and anything beyond the per-batch limit. */
export function addToQueue(queue: QueueItem[], files: File[]): { queue: QueueItem[]; duplicates: number; overLimit: number } {
  const fresh = files.filter((f) => !queue.some((q) => sameFile(q.file, f)));
  const room = Math.max(0, MAX_FILES - queue.length);
  const accepted = fresh.slice(0, room);
  return {
    queue: [...queue, ...accepted.map(makeQueueItem)],
    duplicates: files.length - fresh.length,
    overLimit: fresh.length - accepted.length,
  };
}

/** Drops empty values so an untouched field is never sent (metadata is optional at upload; drafts may be incomplete). */
export function compactMetadata(values: Record<string, unknown>): Record<string, unknown> | null {
  const keep = (v: unknown): unknown => {
    if (v === '' || v === null || v === undefined) return undefined;
    if (Array.isArray(v)) return v.length > 0 ? v : undefined;
    if (typeof v === 'object') {
      const inner = Object.fromEntries(
        Object.entries(v as Record<string, unknown>)
          .map(([k, x]) => [k, keep(x)])
          .filter(([, x]) => x !== undefined),
      );
      return Object.keys(inner).length > 0 ? inner : undefined;
    }
    return v;
  };
  const out = Object.fromEntries(
    Object.entries(values)
      .map(([k, v]) => [k, keep(v)])
      .filter(([, v]) => v !== undefined),
  );
  return Object.keys(out).length > 0 ? out : null;
}
