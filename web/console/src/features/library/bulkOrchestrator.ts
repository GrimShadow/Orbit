/** How many items run at once - the same fixed-concurrency-pool shape `useUploadBatch.ts` already established for
 * bulk uploads (ADR-0015), reused here instead of a library: enough parallelism to keep 50 assets well under the
 * spec's "< 5s round-trip" accept criterion (spec Step 1.8) without opening 50 connections at once. */
const CONCURRENCY = 5;

export interface BulkResult {
  succeeded: string[];
  failed: { id: string; error: string }[];
}

/** Runs `task` for every id, a fixed number at a time, collecting which ids succeeded and which failed (with why) -
 * one item's failure never stops or rolls back any other, since there is no server-side bulk transaction to roll back
 * (each id goes through its own ordinary per-asset command; see ADR-0017). */
export async function runBulk(ids: string[], task: (id: string) => Promise<void>): Promise<BulkResult> {
  const succeeded: string[] = [];
  const failed: { id: string; error: string }[] = [];
  const queue = [...ids];

  const worker = async () => {
    for (let id = queue.shift(); id; id = queue.shift()) {
      try {
        await task(id);
        succeeded.push(id);
      } catch (e) {
        failed.push({ id, error: e instanceof Error ? e.message : String(e) });
      }
    }
  };
  await Promise.all(Array.from({ length: Math.min(CONCURRENCY, queue.length) }, worker));
  return { succeeded, failed };
}
