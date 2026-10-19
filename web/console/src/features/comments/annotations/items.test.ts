import { activeAt, assignLanes, excerpt, onPage, POINT_CAPTION_MS, shapeItems, stackRanks, timeItems } from './items';
import { moment, pin, region, thread } from './stageHarness';

describe('excerpt', () => {
  it('keeps short text, collapses whitespace and shows mention tokens as @Name', () => {
    expect(excerpt('  tighten\n\n the   crop ')).toBe('tighten the crop');
    expect(excerpt('ask @[Priya Shah](user:11111111-1111-1111-1111-111111111111) about this')).toBe('ask @Priya Shah about this');
  });

  it('cuts long text at a word boundary with an ellipsis', () => {
    const out = excerpt('the logo is too close to the edge of the frame and needs more breathing room', 30);
    expect(out.endsWith('…')).toBe(true);
    expect(out.length).toBeLessThanOrEqual(31);
    expect(out).toBe('the logo is too close to the…');
  });
});

describe('shapeItems', () => {
  const threads = [
    region('r1', 0.1, 0.1, 0.2, 0.2),
    thread('c1', null),
    pin('p1', 0.5, 0.5, { resolved: true }),
    moment('t1', 1000),
    region('old', 0.1, 0.1, 0.2, 0.2, { versionId: 'v1' }),
    region('gone', 0.1, 0.1, 0.2, 0.2, { deleted: true }),
  ];

  it('numbers by the shared annotation numbering (every anchored thread, timecodes included) and keeps region/pin only', () => {
    const items = shapeItems(threads, 'v2');
    expect(items.map((i) => [i.id, i.number, i.kind, i.resolved])).toEqual([
      ['r1', 1, 'region', false],
      ['p1', 2, 'point', true],
    ]);
  });

  it('hides annotations that belong to another version, and draws nothing without a version', () => {
    expect(shapeItems(threads, 'v2').some((i) => i.id === 'old')).toBe(false);
    expect(shapeItems(threads, 'v1').map((i) => i.id)).toEqual(['old']);
    expect(shapeItems(threads, null)).toEqual([]);
  });

  it('skips a region with missing fields rather than drawing NaN', () => {
    expect(shapeItems([thread('bad', { type: 'region', x: 0.1, y: 0.1 })], 'v2')).toEqual([]);
  });
});

describe('timeItems', () => {
  it('takes timecode threads with their range and number', () => {
    const items = timeItems([region('r', 0, 0, 0.5, 0.5), moment('a', 1000), moment('b', 5000, 9000, { resolved: true })], 'v2');
    expect(items.map((i) => [i.id, i.number, i.startMs, i.endMs, i.resolved])).toEqual([
      ['a', 2, 1000, null, false],
      ['b', 3, 5000, 9000, true],
    ]);
  });
});

describe('onPage', () => {
  const items = shapeItems([region('a', 0, 0, 0.1, 0.1, { page: 1 }), pin('b', 0.5, 0.5, { page: 2 }), pin('c', 0.2, 0.2)], 'v2');
  it('draws only the page showing, treating an anchor with no page as page 1', () => {
    expect(onPage(items, 1).map((i) => i.id)).toEqual(['a', 'c']);
    expect(onPage(items, 2).map((i) => i.id)).toEqual(['b']);
    expect(onPage(items, 3)).toEqual([]);
  });
  it('draws everything on a plain image', () => {
    expect(onPage(items, undefined)).toHaveLength(3);
  });
});

it('ranks smaller shapes above larger ones without reordering them', () => {
  const items = shapeItems([region('big', 0, 0, 0.9, 0.9), region('small', 0.2, 0.2, 0.1, 0.1), pin('pin', 0.5, 0.5)], 'v2');
  const ranks = stackRanks(items);
  expect(ranks.get('big')).toBeLessThan(ranks.get('small')!);
  expect(ranks.get('small')).toBeLessThan(ranks.get('pin')!);
});

describe('activeAt', () => {
  const items = timeItems(
    [moment('p', 10_000), moment('r', 20_000, 30_000), moment('done', 10_000, 12_000, { resolved: true })],
    'v2',
  );
  it('is on inside a range and for ~2 s after a single moment, off otherwise, and ignores resolved threads', () => {
    expect(activeAt(items, 9_999)).toEqual([]);
    expect(activeAt(items, 10_000).map((i) => i.id)).toEqual(['p']);
    expect(activeAt(items, 10_000 + POINT_CAPTION_MS).map((i) => i.id)).toEqual(['p']);
    expect(activeAt(items, 10_000 + POINT_CAPTION_MS + 1)).toEqual([]);
    expect(activeAt(items, 25_000).map((i) => i.id)).toEqual(['r']);
    expect(activeAt(items, 30_001)).toEqual([]);
  });
});

describe('assignLanes', () => {
  it('puts markers that would overlap on different rows and lets distant ones share row 0', () => {
    const items = timeItems([moment('a', 10_000), moment('b', 10_500), moment('c', 60_000), moment('d', 10_800)], 'v2');
    const lanes = assignLanes(items, 100_000);
    expect(lanes.get('a')).toBe(0);
    expect(lanes.get('b')).toBe(1);
    expect(lanes.get('d')).toBe(2);
    expect(lanes.get('c')).toBe(0);
  });
  it('keeps a range clear of what follows it, wraps beyond three rows, and needs a duration', () => {
    const items = timeItems(
      [moment('r', 0, 50_000), moment('a', 20_000), moment('b', 20_100), moment('c', 20_200), moment('d', 20_300)],
      'v2',
    );
    const lanes = assignLanes(items, 100_000);
    expect(lanes.get('r')).toBe(0);
    expect(Math.max(...lanes.values())).toBeLessThanOrEqual(2);
    expect(assignLanes(items, 0).size).toBe(0);
  });
});
