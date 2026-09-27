import type { ApiClient } from '@dam/sdk-js';
import { unwrap } from '@dam/sdk-js';

export interface PartUrl {
  partNumber: number;
  url: string;
}
export interface UploadStart {
  upload: {
    id: string;
    partSize: number;
    partCount: number;
  };
  parts: PartUrl[];
}

const TERMINAL_STATUSES = new Set(['ingested', 'blocked', 'failed', 'duplicate']);

/** Uploads every part of one file straight to storage (spec ADR-0004: no file bytes through dam-api), completes the
 * transfer, then polls until the ingest pipeline (Step 1.4) settles on a terminal status. */
export async function uploadFile(
  api: ApiClient,
  file: File,
  start: UploadStart,
  onProgress: (fraction: number) => void,
): Promise<{ status: string; error?: string }> {
  const { id: uploadId, partSize, partCount } = start.upload;
  const urlByPart = new Map(start.parts.map((p) => [p.partNumber, p.url]));

  const missing = [];
  for (let n = 1; n <= partCount; n++) if (!urlByPart.has(n)) missing.push(n);
  if (missing.length > 0) {
    const more = unwrap(
      await api.POST('/api/v1/uploads/{id}/parts', { params: { path: { id: uploadId } }, body: { partNumbers: missing } }),
    );
    for (const p of more) urlByPart.set(p.partNumber, p.url);
  }

  const etags: { partNumber: number; eTag: string }[] = [];
  let uploadedBytes = 0;
  for (let partNumber = 1; partNumber <= partCount; partNumber++) {
    const url = urlByPart.get(partNumber);
    if (!url) throw new Error(`No upload URL for part ${partNumber}.`);
    const start = (partNumber - 1) * partSize;
    const chunk = file.slice(start, start + partSize);
    const res = await fetch(url, { method: 'PUT', body: chunk });
    if (!res.ok) throw new Error(`Uploading part ${partNumber} failed (${res.status}).`);
    const eTag = res.headers.get('ETag')?.replaceAll('"', '');
    if (!eTag) throw new Error(`Part ${partNumber} was not accepted (no ETag returned).`);
    etags.push({ partNumber, eTag });
    uploadedBytes += chunk.size;
    onProgress(Math.min(uploadedBytes / file.size, 1));
  }

  const completed = unwrap(
    await api.POST('/api/v1/uploads/{id}/complete', { params: { path: { id: uploadId } }, body: { parts: etags, sha256: null } }),
  );

  let latest = completed;
  while (!TERMINAL_STATUSES.has(latest.status)) {
    await new Promise((resolve) => setTimeout(resolve, 500));
    latest = unwrap(await api.GET('/api/v1/uploads/{id}', { params: { path: { id: uploadId } } }));
  }
  return { status: latest.status, error: latest.error || undefined };
}
