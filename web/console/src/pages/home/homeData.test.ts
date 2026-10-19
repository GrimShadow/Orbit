import { dayPartOf, facetCount, firstNameOf } from './homeData';

describe('firstNameOf', () => {
  it.each([
    ['Priya Natarajan', 'Priya'],
    ['  Sam ', 'Sam'],
    ['priya.natarajan@acme.example', 'Priya'],
    ['', ''],
    [undefined, ''],
  ])('%j -> %j', (input, expected) => expect(firstNameOf(input)).toBe(expected));
});

describe('dayPartOf', () => {
  it.each([
    [0, 'morning'],
    [11, 'morning'],
    [12, 'afternoon'],
    [17, 'afternoon'],
    [18, 'evening'],
    [23, 'evening'],
  ] as const)('hour %i is the %s', (hour, expected) => {
    expect(dayPartOf(new Date(2026, 0, 5, hour, 30))).toBe(expected);
  });
});

describe('facetCount', () => {
  const facets = [{ field: 'status', values: [{ value: 'draft', count: 4 }] }];
  it('finds a value and defaults to zero', () => {
    expect(facetCount(facets, 'status', 'draft')).toBe(4);
    expect(facetCount(facets, 'status', 'published')).toBe(0);
    expect(facetCount(undefined, 'status', 'draft')).toBe(0);
  });
});
