import { buildDiff, flatten, formatValue } from './auditDiff';
import { entityRoute, reasonKey, relatedAssetId, shortHash } from './auditFormat';

describe('audit diff', () => {
  it('flattens nested objects to dotted paths and keeps arrays whole', () => {
    expect([...flatten({ a: { b: 1, c: { d: 'x' } }, list: [1, 2], empty: {} })]).toEqual([
      ['a.b', 1],
      ['a.c.d', 'x'],
      ['list', [1, 2]],
      ['empty', {}],
    ]);
    expect([...flatten('plain')]).toEqual([['(value)', 'plain']]);
  });

  it('marks added, removed, changed and unchanged fields', () => {
    const d = buildDiff(
      { name: 'Old', tags: ['a'], meta: { size: 1, gone: true }, same: 1 },
      { name: 'New', tags: ['a', 'b'], meta: { size: 1, fresh: 'y' }, same: 1 },
    );
    expect(d.mode).toBe('diff');
    expect(Object.fromEntries(d.rows.map((r) => [r.path, r.kind]))).toEqual({
      name: 'changed',
      tags: 'changed',
      'meta.size': 'unchanged',
      'meta.gone': 'removed',
      'meta.fresh': 'added',
      same: 'unchanged',
    });
    expect(d.counts).toEqual({ added: 1, removed: 1, changed: 2, unchanged: 2 });
    const name = d.rows.find((r) => r.path === 'name')!;
    expect([name.before, name.after]).toEqual(['Old', 'New']);
  });

  it('lists the fields as recorded when only one side exists', () => {
    const after = buildDiff(null, { To: 'deleted', From: 'draft' });
    expect(after.mode).toBe('after');
    expect(after.rows.map((r) => [r.path, r.kind, r.after])).toEqual([
      ['From', 'unchanged', 'draft'],
      ['To', 'unchanged', 'deleted'],
    ]);
    expect(buildDiff({ a: 1 }, undefined).mode).toBe('before');
    expect(buildDiff(null, null)).toEqual({ mode: 'none', rows: [], counts: { added: 0, removed: 0, changed: 0, unchanged: 0 } });
  });

  it('treats an explicit falsy value as recorded, and a null field as a value', () => {
    const d = buildDiff({ a: null, b: 0 }, { a: 'x', b: 0 });
    expect(d.rows.map((r) => r.kind)).toEqual(['changed', 'unchanged']);
  });

  it('cuts very long values', () => {
    const v = formatValue('x'.repeat(1000), 50);
    expect(v.truncated).toBe(true);
    expect(v.text.length).toBe(51);
    expect(formatValue({ a: 1 })).toEqual({ text: '{"a":1}', truncated: false });
  });
});

describe('audit display helpers', () => {
  it('shortens hashes', () => {
    expect(shortHash('abcdef0123456789abcdef0123456789', 4, 2)).toBe('abcd…89');
    expect(shortHash('abc')).toBe('abc');
  });

  it('maps verification reasons, with a fallback for unknown ones', () => {
    expect(reasonKey('gap')).toBe('gap');
    expect(reasonKey('prev_hash_mismatch')).toBe('prev_hash_mismatch');
    expect(reasonKey('hash_mismatch')).toBe('hash_mismatch');
    expect(reasonKey('something_new')).toBe('unknown');
    expect(reasonKey(null)).toBe('unknown');
  });

  it('links entities that have a console route', () => {
    expect(entityRoute('asset', 'a1')).toBe('/library/assets/a1');
    expect(entityRoute('workflow', 'w1')).toBe('/settings/workflows/w1');
    expect(entityRoute('folder', 'f1')).toBe('/library?folder=f1');
    expect(entityRoute('collection', 'c1')).toBe('/collections/c1');
    expect(entityRoute('webhook', 'x')).toBeNull();
    expect(entityRoute('asset', null)).toBeNull();
  });

  it('finds the asset a non-asset event concerns', () => {
    expect(relatedAssetId({ AssetId: 'a1' })).toBe('a1');
    expect(relatedAssetId({ other: 1 })).toBeNull();
    expect(relatedAssetId(null)).toBeNull();
  });
});

describe('audit diff - empty documents', () => {
  it('treats an empty before-document as having no fields', () => {
    const d = buildDiff({}, { a: 1 });
    expect(d.mode).toBe('diff');
    expect(d.rows.map((r) => [r.path, r.kind])).toEqual([['a', 'added']]);
  });
});
