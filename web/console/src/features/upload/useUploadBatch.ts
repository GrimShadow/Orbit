import { unwrap } from '@dam/sdk-js';
import { useCallback, useState } from 'react';
import { useApiClient } from '../../api/apiClient';
import { uploadFile } from './uploadOrchestrator';

export type FileStatus = 'uploading' | 'finalizing' | 'ingested' | 'duplicate' | 'blocked' | 'failed' | 'rejected';

export interface UploadItemState {
  fileName: string;
  status: FileStatus;
  /** 0-1 while uploading; meaningless once settled. */
  progress: number;
  error?: string;
  assetId?: string;
}

export interface UploadBatchState {
  status: 'idle' | 'submitting' | 'running' | 'done';
  items: UploadItemState[];
}

/** How many files upload at once - enough to make use of the browser's own connection pool without saturating it. */
const CONCURRENCY = 3;

function patchItem(state: UploadBatchState, index: number, patch: Partial<UploadItemState>): UploadBatchState {
  const items = state.items.slice();
  items[index] = { ...items[index]!, ...patch };
  return { ...state, items };
}

/** Drives one batch through `POST /assets/bulk-upload` (spec Step 1.8: "drag-drop + bulk uploader with batch metadata
 * form" - one draft asset and one upload per file, sharing the batch's folder/tags/metadata) and then each file's own
 * multipart transfer, several at a time. */
export function useUploadBatch() {
  const api = useApiClient();
  const [state, setState] = useState<UploadBatchState>({ status: 'idle', items: [] });

  const start = useCallback(
    async (files: File[], options: { folderId?: string; tags: string[] }) => {
      if (files.length === 0) return;
      setState({ status: 'submitting', items: files.map((f) => ({ fileName: f.name, status: 'uploading', progress: 0 })) });

      const result = unwrap(
        await api.POST('/api/v1/assets/bulk-upload', {
          body: {
            folderId: options.folderId ?? null,
            batchId: null,
            metadata: null,
            tags: options.tags.length > 0 ? options.tags : null,
            files: files.map((f) => ({ fileName: f.name, size: f.size, contentType: f.type || null })),
          },
        }),
      );

      setState({
        status: 'running',
        items: result.items.map((item) => ({
          fileName: item.fileName,
          status: item.error ? 'rejected' : 'uploading',
          progress: 0,
          error: item.error ?? undefined,
          assetId: item.assetId ?? undefined,
        })),
      });

      const queue = result.items.map((item, index) => ({ item, index, file: files[index] }));
      const worker = async () => {
        for (let next = queue.shift(); next; next = queue.shift()) {
          const { item, index, file } = next;
          if (item.error || !item.upload || !file) continue;
          try {
            const outcome = await uploadFile(api, file, item.upload, (fraction) =>
              setState((s) => patchItem(s, index, { progress: fraction })),
            );
            setState((s) => patchItem(s, index, { status: outcome.status as FileStatus, progress: 1, error: outcome.error }));
          } catch (e) {
            setState((s) =>
              patchItem(s, index, { status: 'failed', progress: 1, error: e instanceof Error ? e.message : String(e) }),
            );
          }
        }
      };
      await Promise.all(Array.from({ length: Math.min(CONCURRENCY, queue.length) }, worker));
      setState((s) => ({ ...s, status: 'done' }));
    },
    [api],
  );

  const reset = useCallback(() => setState({ status: 'idle', items: [] }), []);

  return { state, start, reset };
}
