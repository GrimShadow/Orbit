import { runBulk } from './bulkOrchestrator';

describe('runBulk', () => {
  it('runs every id and reports them all as succeeded when the task never throws', async () => {
    const seen: string[] = [];
    const result = await runBulk(['a', 'b', 'c'], async (id) => {
      seen.push(id);
    });
    expect(seen.sort()).toEqual(['a', 'b', 'c']);
    expect(result.succeeded.sort()).toEqual(['a', 'b', 'c']);
    expect(result.failed).toEqual([]);
  });

  it("collects a failing id's error without stopping the others", async () => {
    const result = await runBulk(['a', 'b', 'c'], async (id) => {
      if (id === 'b') throw new Error('boom');
    });
    expect(result.succeeded.sort()).toEqual(['a', 'c']);
    expect(result.failed).toEqual([{ id: 'b', error: 'boom' }]);
  });

  it('stringifies a non-Error throw', async () => {
    const result = await runBulk(['a'], async () => {
      throw 'nope'; // exercising the non-Error fallback branch
    });
    expect(result.failed).toEqual([{ id: 'a', error: 'nope' }]);
  });

  it('runs more than the concurrency limit worth of ids to completion', async () => {
    const ids = Array.from({ length: 23 }, (_, i) => `id${i}`);
    const result = await runBulk(ids, async () => {});
    expect(result.succeeded).toHaveLength(23);
    expect(result.failed).toEqual([]);
  });

  it('resolves immediately for an empty id list', async () => {
    const result = await runBulk([], async () => {
      throw new Error('should never run');
    });
    expect(result).toEqual({ succeeded: [], failed: [] });
  });
});
