import i18n from '../../i18n';
import { storage, topAssets } from './insightsTestKit';
import { cellOf, formatCell, isNumericColumn, storageSegments } from './reportFormat';

const t = i18n.getFixedT('en');

describe('formatCell', () => {
  it.each([
    [2048, 'bytes', '2.0 KB'],
    [1234, 'count', '1,234'],
    [3.14159, 'number', '3.14'],
    [0.425, 'percent', '42.5%'],
    [1, 'percent', '100%'],
    [5.5, 'hours', '5.5 h'],
    ['hello', 'text', 'hello'],
    ['hello', 'mystery-type', 'hello'],
  ])('formats %s as %s', (value, type, expected) => {
    expect(formatCell(value, type, t, 'en')).toBe(expected);
  });
  it('leaves missing values blank and keeps unreadable numbers as text', () => {
    expect(formatCell(null, 'count', t, 'en')).toBe('');
    expect(formatCell(undefined, 'bytes', t, 'en')).toBe('');
    expect(formatCell('n/a', 'count', t, 'en')).toBe('n/a');
  });
  it("shows a datetime in the reader's language", () => {
    expect(formatCell('2026-10-19T09:30:00Z', 'datetime', t, 'en')).toMatch(/Oct 19, 2026/);
  });
});

describe('report cells', () => {
  it('reads a row by its column key', () => {
    expect(cellOf({ uses: 4 }, { key: 'uses', label: 'Uses', type: 'count' })).toBe(4);
  });
  it('right-aligns figures only', () => {
    expect(isNumericColumn({ key: 'a', label: 'A', type: 'bytes' })).toBe(true);
    expect(isNumericColumn({ key: 'a', label: 'A', type: 'text' })).toBe(false);
  });
});

describe('storageSegments', () => {
  it('takes only top-level folder rows, largest first, sized by bytes', () => {
    const segments = storageSegments(storage());
    expect(segments.map((s) => s.label)).toEqual(['Acme Campaigns', 'Acme Brand']);
    expect(segments[0]!.display).toBe('300.0 MB');
  });
  it('gives nothing when the report has no byte column', () => {
    expect(storageSegments({ ...topAssets(), columns: [{ key: 'name', label: 'Name', type: 'text' }] })).toEqual([]);
  });
});
