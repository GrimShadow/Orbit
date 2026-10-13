import { unwrap } from '@dam/sdk-js';
import { useCallback, useState } from 'react';
import { useApiClient } from '../../api/apiClient';
import { uploadFile } from './uploadOrchestrator';
import type { QueueItem } from './uploadQueue';

export type FileStatus = 'uploading' | 'finalizing' | 'ingested' | 'duplicate' | 'blocked' | 'failed' | 'rejected';

export interface UploadItemState {
  /** The queue item this row came from, so a failed file can be sent back to the form. */
  queueId: string;
  fileName: string;
  title: string;
  type: string;
  size: number;
  previewUrl?: string;
  status: FileStatus;
  /** 0-1 while uploading; meaningless once settled. */
  progress: number;
  error?: string;
  assetId?: string;
  /** For `duplicate`: the asset that already holds identical bytes. */
  duplicateOfAssetId?: string;
}

export interface UploadBatchState {
  status: 'idle' | 'submitting' | 'running' | 'done';
  items: UploadItemState[];
}

export interface UploadOptions {
  folderId?: string;
  tags: string[];
  /** Per asset type: the metadata document for that type's files (only types the user filled in). */
  metadataByType?: Record<string, Record<string, unknown>>;
}

/** How many files upload at once - enough to make use of the browser's own connection pool without saturating it. */
const CONCURRENCY = 3;

function patchItem(state: UploadBatchState, index: number, patch: Partial<UploadItemState>): UploadBatchState {
  const items = state.items.slice();
  items[index] = { ...items[index]!, ...patch };
  return { ...state, items };
}

/** Drives one batch through `POST /assets/bulk-upload` (one draft asset and one upload per file, each with its own type
 * and title, sharing the batch's folder/tags and a metadata document per type) and then each file's own multipart
 * transfer, several at a time. */
export function useUploadBatch() {
  const api = useApiClient();
  const [state, setState] = useState<UploadBatchState>({ status: 'idle', items: [] });

  const start = useCallback(
    async (queue: QueueItem[], options: UploadOptions) => {
      if (queue.length === 0) return;
      const base = (q: QueueItem): Omit<UploadItemState, 'status' | 'progress'> => ({
        queueId: q.id,
        fileName: q.file.name,
        title: q.title,
        type: q.type,
        size: q.file.size,
        previewUrl: q.previewUrl,
      });
      setState({ status: 'submitting', items: queue.map((q) => ({ ...base(q), status: 'uploading', progress: 0 })) });

      let result;
      try {
        result = unwrap(
          await api.POST('/api/v1/assets/bulk-upload', {
            body: {
              folderId: options.folderId ?? null,
              batchId: null,
              metadata: null,
              // A raw JSON document is generated as an empty-object type (OpenAPI cannot express "any JSON"), so cast here.
              metadataByType: (options.metadataByType && Object.keys(options.metadataByType).length > 0
                ? options.metadataByType
                : null) as unknown as { [x: string]: undefined } | null,
              tags: options.tags.length > 0 ? options.tags : null,
              files: queue.map((q) => ({
                fileName: q.file.name,
                size: q.file.size,
                contentType: q.file.type || null,
                assetType: q.type,
                title: q.title.trim() || null,
              })),
            },
          }),
        );
      } catch (e) {
        // The whole request failed (validation, permission, network) - every file is rejected with the reason.
        const message = e instanceof Error ? e.message : String(e);
        setState({ status: 'done', items: queue.map((q) => ({ ...base(q), status: 'rejected', progress: 0, error: message })) });
        return;
      }

      setState({
        status: 'running',
        items: result.items.map((item, i) => ({
          ...base(queue[i]!),
          status: item.error ? 'rejected' : 'uploading',
          progress: 0,
          error: item.error ?? undefined,
          assetId: item.assetId ?? undefined,
        })),
      });

      const work = result.items.map((item, index) => ({ item, index, file: queue[index]?.file }));
      const worker = async () => {
        for (let next = work.shift(); next; next = work.shift()) {
          const { item, index, file } = next;
          if (item.error || !item.upload || !file) continue;
          try {
            const outcome = await uploadFile(api, file, item.upload, (fraction) =>
              setState((s) => patchItem(s, index, { progress: fraction })),
            );
            setState((s) =>
              patchItem(s, index, {
                status: outcome.status as FileStatus,
                progress: 1,
                error: outcome.error,
                duplicateOfAssetId: outcome.duplicateOfAssetId,
              }),
            );
          } catch (e) {
            setState((s) =>
              patchItem(s, index, { status: 'failed', progress: 1, error: e instanceof Error ? e.message : String(e) }),
            );
          }
        }
      };
      await Promise.all(Array.from({ length: Math.min(CONCURRENCY, work.length) }, worker));
      setState((s) => ({ ...s, status: 'done' }));
    },
    [api],
  );

  const reset = useCallback(() => setState({ status: 'idle', items: [] }), []);

  return { state, start, reset };
}
