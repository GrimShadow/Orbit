import { rebaseValues } from './useAssetDraft';

describe('rebaseValues', () => {
  it('follows the server for fields the person has not touched and keeps the ones they have', () => {
    const base = { credit: 'Acme', headline: { en: 'Spring' } };
    const mine = { credit: 'Acme Studio', headline: { en: 'Spring' } };
    const theirs = { credit: 'Acme', headline: { en: 'Spring', hi: 'वसंत' }, blurb: { en: 'New' } };
    expect(rebaseValues(mine, base, theirs)).toEqual({
      credit: 'Acme Studio',
      headline: { en: 'Spring', hi: 'वसंत' },
      blurb: { en: 'New' },
    });
  });

  it('merges a multilingual field language by language', () => {
    const base = { headline: { en: 'Spring', hi: '' } };
    const mine = { headline: { en: 'Spring!', hi: '' } };
    const theirs = { headline: { en: 'Spring', hi: 'वसंत' } };
    expect(rebaseValues(mine, base, theirs)).toEqual({ headline: { en: 'Spring!', hi: 'वसंत' } });
  });

  it("keeps the person's text when both changed the same language", () => {
    const base = { headline: { en: 'Spring' } };
    expect(rebaseValues({ headline: { en: 'Mine' } }, base, { headline: { en: 'Theirs' } })).toEqual({
      headline: { en: 'Mine' },
    });
  });

  it('drops a field the server removed when the person left it alone', () => {
    expect(rebaseValues({ a: 'x', b: 'y!' }, { a: 'x', b: 'y' }, { b: 'y' })).toEqual({ b: 'y!' });
  });

  it('treats an empty string like nothing entered', () => {
    expect(rebaseValues({ headline: { hi: '' } }, {}, { headline: { hi: 'वसंत' } })).toEqual({ headline: { hi: 'वसंत' } });
  });

  it("keeps arrays the person edited and takes the server's when they did not", () => {
    expect(rebaseValues({ tags: ['a', 'b'] }, { tags: ['a'] }, { tags: ['a', 'c'] })).toEqual({ tags: ['a', 'b'] });
    expect(rebaseValues({ tags: ['a'] }, { tags: ['a'] }, { tags: ['a', 'c'] })).toEqual({ tags: ['a', 'c'] });
  });
});
