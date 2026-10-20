import {
  chapterRowsToDtos,
  chaptersToRows,
  docDraftToInput,
  emptyDocDraft,
  emptyRuleDraft,
  moveRow,
  newChapterRow,
  ruleDraftToInput,
  ruleToDraft,
  ruleToInput,
  validateChapterRows,
  validateDocDraft,
  validateRuleDraft,
} from './knowledgeDraft';
import { CHAPTERS, doc, rule } from './knowledgeTestKit';

describe('document mapping draft', () => {
  const ok = { ...emptyDocDraft('hi'), model: 'Lumen', variant: 'Sedan', yearFrom: '2021', yearTo: '2024', theme: 'EV' };

  it('needs an asset and a model, and nothing else', () => {
    expect(validateDocDraft(ok, true)).toEqual({});
    expect(validateDocDraft({ ...emptyDocDraft(), model: 'lumen' }, true)).toEqual({});
    expect(validateDocDraft(emptyDocDraft(), false)).toEqual({ asset: 'assetRequired', model: 'modelRequired' });
  });

  it('checks the codes after lower-casing them', () => {
    expect(validateDocDraft({ ...ok, model: 'lumen ev' }, true).model).toBe('codeInvalid');
    expect(validateDocDraft({ ...ok, variant: 'a/b' }, true).variant).toBe('codeInvalid');
    expect(validateDocDraft({ ...ok, theme: 'no way' }, true).theme).toBe('codeInvalid');
  });

  it('wants both years or neither, in order', () => {
    expect(validateDocDraft({ ...ok, yearFrom: '', yearTo: '' }, true)).toEqual({});
    expect(validateDocDraft({ ...ok, yearTo: '' }, true).years).toBe('yearsBoth');
    expect(validateDocDraft({ ...ok, yearFrom: '' }, true).years).toBe('yearsBoth');
    expect(validateDocDraft({ ...ok, yearFrom: '2025', yearTo: '2022' }, true).years).toBe('yearsOrder');
    expect(validateDocDraft({ ...ok, yearFrom: '2022', yearTo: '2022' }, true)).toEqual({});
    expect(validateDocDraft({ ...ok, yearFrom: '21' }, true).yearFrom).toBe('yearInvalid');
  });

  it('becomes the request body', () => {
    expect(docDraftToInput(ok, 'a9')).toEqual({
      assetId: 'a9',
      docType: 'owner_manual',
      model: 'lumen',
      variant: 'sedan',
      modelYearFrom: 2021,
      modelYearTo: 2024,
      language: 'hi',
      theme: 'ev',
    });
    expect(docDraftToInput({ ...emptyDocDraft(), model: 'lumen' })).toMatchObject({
      variant: null,
      modelYearFrom: null,
      modelYearTo: null,
      theme: null,
    });
  });
});

describe('VIN rule draft', () => {
  const ok = { ...emptyRuleDraft(), name: ' Acme Lumen ', pattern: 'acmls1*', model: 'Lumen' };

  it('needs a name, a valid pattern and a model', () => {
    expect(validateRuleDraft(ok)).toEqual({});
    expect(validateRuleDraft(emptyRuleDraft())).toMatchObject({ name: 'nameRequired', pattern: 'empty', model: 'modelRequired' });
    expect(validateRuleDraft({ ...ok, pattern: 'AC[' })).toMatchObject({ pattern: 'unclosedSet' });
    expect(validateRuleDraft({ ...ok, name: 'x'.repeat(201) }).name).toBe('nameLong');
  });

  it('needs a year only when the year is fixed', () => {
    expect(validateRuleDraft({ ...ok, yearMode: 'fixed', modelYear: '' }).modelYear).toBe('yearRequired');
    expect(validateRuleDraft({ ...ok, yearMode: 'fixed', modelYear: '24' }).modelYear).toBe('yearInvalid');
    expect(validateRuleDraft({ ...ok, yearMode: 'fixed', modelYear: '2024' })).toEqual({});
    expect(validateRuleDraft({ ...ok, yearMode: 'vin', modelYear: 'junk' })).toEqual({});
  });

  it('takes a whole-number priority, negatives included', () => {
    expect(validateRuleDraft({ ...ok, priority: '-5' })).toEqual({});
    expect(validateRuleDraft({ ...ok, priority: '1.5' }).priority).toBe('priorityInvalid');
    expect(validateRuleDraft({ ...ok, priority: '' }).priority).toBe('priorityInvalid');
  });

  it('becomes the request body, with the pattern upper-cased', () => {
    expect(ruleDraftToInput({ ...ok, variant: 'Sedan', theme: 'ICE', priority: ' 7 ' })).toEqual({
      name: 'Acme Lumen',
      pattern: 'ACMLS1*',
      model: 'lumen',
      variant: 'sedan',
      modelYear: null,
      yearFromVin: true,
      theme: 'ice',
      priority: 7,
      enabled: true,
    });
    expect(ruleDraftToInput({ ...ok, yearMode: 'fixed', modelYear: '2024' })).toMatchObject({
      modelYear: 2024,
      yearFromVin: false,
    });
  });

  it('round-trips a stored rule', () => {
    const fixed = rule({ modelYear: 2023, yearFromVin: false, variant: null, theme: null });
    expect(ruleToDraft(fixed)).toMatchObject({ yearMode: 'fixed', modelYear: '2023', variant: '', theme: '' });
    expect(ruleToDraft(rule())).toMatchObject({ yearMode: 'vin', modelYear: '' });
    expect(ruleDraftToInput(ruleToDraft(fixed))).toMatchObject({ modelYear: 2023, yearFromVin: false, variant: null });
  });

  it('flips only the enabled flag', () => {
    const r = rule();
    expect(ruleToInput(r, false)).toEqual({
      name: r.name,
      pattern: r.pattern,
      model: r.model,
      variant: r.variant,
      modelYear: r.modelYear,
      yearFromVin: r.yearFromVin,
      theme: r.theme,
      priority: r.priority,
      enabled: false,
    });
  });
});

describe('chapter rows', () => {
  it('round-trips chapters, keeping an HTML anchor', () => {
    const rows = chaptersToRows([...CHAPTERS, { title: 'Intro', anchor: 'intro', level: 1, pageFrom: null, pageTo: null }]);
    const back = chapterRowsToDtos(rows);
    expect(back.slice(0, 3)).toEqual(CHAPTERS);
    expect(back[3]).toEqual({ title: 'Intro', anchor: 'intro', level: 1, pageFrom: null, pageTo: null });
  });

  it('needs a title and a level; pages are optional but ordered', () => {
    const good = newChapterRow({ title: 'A', pageFrom: '3', pageTo: '5' });
    const noTitle = newChapterRow({ title: '  ' });
    const badLevel = newChapterRow({ title: 'B', level: '9' });
    const toOnly = newChapterRow({ title: 'C', pageTo: '4' });
    const reversed = newChapterRow({ title: 'D', pageFrom: '9', pageTo: '2' });
    const zero = newChapterRow({ title: 'E', pageFrom: '0' });
    const long = newChapterRow({ title: 'x'.repeat(301) });
    const v = validateChapterRows([good, noTitle, badLevel, toOnly, reversed, zero, long]);
    expect(v.rows.has(good.key)).toBe(false);
    expect(v.rows.get(noTitle.key)).toEqual({ title: 'titleRequired' });
    expect(v.rows.get(badLevel.key)).toEqual({ level: 'levelInvalid' });
    expect(v.rows.get(toOnly.key)).toEqual({ pageFrom: 'pageFromRequired' });
    expect(v.rows.get(reversed.key)).toEqual({ pageTo: 'pageOrder' });
    expect(v.rows.get(zero.key)).toEqual({ pageFrom: 'pageInvalid' });
    expect(v.rows.get(long.key)).toEqual({ title: 'titleLong' });
    expect(v.list).toBeUndefined();
  });

  it('allows 500 chapters and no more', () => {
    const rows = Array.from({ length: 501 }, (_, i) => newChapterRow({ title: `C${i}` }));
    expect(validateChapterRows(rows.slice(0, 500)).list).toBeUndefined();
    expect(validateChapterRows(rows).list).toBe('tooMany');
  });

  it('sends a single page as from = to, and trims titles', () => {
    expect(chapterRowsToDtos([newChapterRow({ title: ' A ', pageFrom: '4' })])).toEqual([
      { title: 'A', anchor: null, level: 1, pageFrom: 4, pageTo: 4 },
    ]);
  });

  it('moves a row by one place and stops at the ends', () => {
    expect(moveRow(['a', 'b', 'c'], 1, -1)).toEqual(['b', 'a', 'c']);
    expect(moveRow(['a', 'b', 'c'], 1, 1)).toEqual(['a', 'c', 'b']);
    expect(moveRow(['a', 'b'], 0, -1)).toEqual(['a', 'b']);
    expect(moveRow(['a', 'b'], 1, 1)).toEqual(['a', 'b']);
  });
});

describe('fixtures', () => {
  it('are fictional', () => {
    expect(JSON.stringify([doc(), rule(), CHAPTERS])).not.toMatch(/\b(toyota|ford|bmw|honda|tesla|mahindra|tata)\b/i);
  });
});
