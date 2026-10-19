import {
  activeFilterKeys,
  decodeFilters,
  defaultVerifyRange,
  exportFilename,
  filtersToQuery,
  fromLocalInput,
  patchParams,
  presetRange,
  rangeIsReversed,
  toLocalInput,
} from './auditFilters';

const ID = '01a154c4-6e0e-7433-a069-18db1e00d58e';

describe('audit filters <-> URL', () => {
  it('reads every filter from the query string', () => {
    const f = decodeFilters(
      new URLSearchParams({
        from: '2026-10-01T00:00:00Z',
        to: '2026-10-02T12:30:00.000Z',
        actor: 'dev-admin',
        action: 'asset.',
        entityType: 'asset',
        entityId: ID.toUpperCase(),
        q: ' hero ',
      }),
    );
    expect(f).toEqual({
      from: '2026-10-01T00:00:00.000Z',
      to: '2026-10-02T12:30:00.000Z',
      actor: 'dev-admin',
      action: 'asset.',
      entityType: 'asset',
      entityId: ID,
      q: 'hero',
    });
  });

  it('drops values that would only earn an error from the API', () => {
    const f = decodeFilters(new URLSearchParams({ from: 'yesterday-ish', entityId: 'not-a-uuid', q: '   ', foo: 'bar' }));
    expect(f).toEqual({});
  });

  it('round-trips through patchParams and leaves unrelated parameters alone', () => {
    const base = new URLSearchParams('keep=1');
    const next = patchParams(base, { action: 'asset.', q: 'x' });
    expect(next.toString()).toBe('keep=1&action=asset.&q=x');
    expect(decodeFilters(next)).toEqual({ action: 'asset.', q: 'x' });
    const cleared = patchParams(next, { action: undefined, q: '  ' });
    expect(cleared.toString()).toBe('keep=1');
  });

  it('builds the API query from the set filters only', () => {
    expect(filtersToQuery({ action: 'asset.', actor: undefined, q: '' })).toEqual({ action: 'asset.' });
    expect(activeFilterKeys({ from: 'a', q: 'b' })).toEqual(['from', 'q']);
  });
});

describe('audit date helpers', () => {
  const now = new Date('2026-10-19T12:00:00.000Z');

  it('presets reach back from now and stay open-ended', () => {
    expect(presetRange('hour', now)).toEqual({ from: '2026-10-19T11:00:00.000Z', to: undefined });
    expect(presetRange('day', now).from).toBe('2026-10-18T12:00:00.000Z');
    expect(presetRange('week', now).from).toBe('2026-10-12T12:00:00.000Z');
    expect(presetRange('month', now).from).toBe('2026-09-19T12:00:00.000Z');
  });

  it('converts to and from datetime-local values, taking an end bound to the end of the minute', () => {
    const local = toLocalInput('2026-10-19T12:34:56.000Z');
    expect(local).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
    const start = fromLocalInput(local, 'start')!;
    const end = fromLocalInput(local, 'end')!;
    expect(Date.parse(end) - Date.parse(start)).toBe(59_999);
    expect(toLocalInput(start)).toBe(local);
    expect(fromLocalInput('', 'start')).toBeUndefined();
    expect(toLocalInput(undefined)).toBe('');
  });

  it('detects a reversed range', () => {
    expect(rangeIsReversed({ from: '2026-10-02T00:00:00Z', to: '2026-10-01T00:00:00Z' })).toBe(true);
    expect(rangeIsReversed({ from: '2026-10-01T00:00:00Z' })).toBe(false);
  });

  it('verifies the filters range, or the last 30 days when none is set', () => {
    expect(defaultVerifyRange({}, now)).toEqual({ from: '2026-09-19T12:00:00.000Z', to: undefined });
    expect(defaultVerifyRange({ from: '2026-10-01T00:00:00.000Z', to: '2026-10-05T00:00:00.000Z' }, now)).toEqual({
      from: '2026-10-01T00:00:00.000Z',
      to: '2026-10-05T00:00:00.000Z',
    });
  });

  it('names export files audit-<from>-<to>.<ext>', () => {
    const local = (iso: string) => iso; // dates are formatted in local time; use mid-day instants so every zone agrees
    expect(exportFilename({ from: local('2026-10-12T12:00:00Z'), to: local('2026-10-19T12:00:00Z') }, 'csv', now)).toBe(
      'audit-20261012-20261019.csv',
    );
    expect(exportFilename({}, 'cef', now)).toBe('audit-start-20261019.cef');
  });
});
