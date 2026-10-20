import { IMPORT_MAX_LINES, emptyDraft, parseGlossaryLines, toDraft, toInput, validateDraft } from './glossaryModel';

const LANGS = ['en', 'hi', 'mr', 'ta', 'te'];
const parse = (text: string) => parseGlossaryLines(text, LANGS);

describe('parseGlossaryLines', () => {
  it('reads a term on its own as locked', () => {
    const r = parse('Acme Lumen');
    expect(r.errors).toEqual([]);
    expect(r.entries).toEqual([{ line: 1, input: { term: 'Acme Lumen', mode: 'locked', translations: {}, note: null } }]);
  });

  it('reads term | language=rendering pairs as mapped', () => {
    const r = parse('Acme Cloud | hi=ऐक्मे क्लाउड | ta=ஏக்மி கிளவுட்');
    expect(r.errors).toEqual([]);
    expect(r.entries[0]!.input).toEqual({
      term: 'Acme Cloud',
      mode: 'mapped',
      translations: { hi: 'ऐक्मे क्लाउड', ta: 'ஏக்மி கிளவுட்' },
      note: null,
    });
  });

  it('skips blank lines but keeps line numbers as the person sees them', () => {
    const r = parse('\nAcme One\n\n  \nAcme Two | xx=nope\n');
    expect(r.entries.map((e) => [e.line, e.input.term])).toEqual([[2, 'Acme One']]);
    expect(r.errors).toEqual([{ line: 5, text: 'Acme Two | xx=nope', code: 'unknownLanguage', detail: 'xx' }]);
  });

  it('trims spaces, lower-cases the language code, and copes with Windows line endings', () => {
    const r = parse('  Acme Hub  |  HI = ऐक्मे हब  \r\nAcme Two\r\n');
    expect(r.entries.map((e) => e.input)).toEqual([
      { term: 'Acme Hub', mode: 'mapped', translations: { hi: 'ऐक्मे हब' }, note: null },
      { term: 'Acme Two', mode: 'locked', translations: {}, note: null },
    ]);
  });

  it('reports each kind of problem with its line, and still takes the good lines', () => {
    const r = parse(
      [
        'Good',
        '| hi=x',
        'Bad one | hi',
        'Bad two | =x',
        'Bad three | zz=x',
        'Bad four | hi=a | hi=b',
        'Bad five | hi=  ',
        'Good',
        'Fine',
      ].join('\n'),
    );
    expect(r.entries.map((e) => e.input.term)).toEqual(['Good', 'Fine']);
    expect(r.errors.map((e) => [e.line, e.code])).toEqual([
      [2, 'emptyTerm'],
      [3, 'badSegment'],
      [4, 'badSegment'],
      [5, 'unknownLanguage'],
      [6, 'duplicateLanguage'],
      [7, 'emptyRendering'],
      [8, 'duplicateTerm'],
    ]);
    expect(r.errors.find((e) => e.code === 'duplicateTerm')!.detail).toBe('1');
  });

  it('matches terms exactly, so a different case is a different term', () => {
    expect(parse('Acme\nacme').errors).toEqual([]);
  });

  it('uses only the first lines of a very long paste', () => {
    const text = Array.from({ length: IMPORT_MAX_LINES + 5 }, (_, i) => `Term ${i}`).join('\n');
    const r = parse(text);
    expect(r.entries).toHaveLength(IMPORT_MAX_LINES);
    expect(r.truncated).toBe(true);
  });

  it('is empty for empty input', () => {
    expect(parse('  \n ')).toEqual({ entries: [], errors: [], truncated: false });
  });
});

describe('term drafts', () => {
  it('needs a term, and for a mapped term at least one complete, distinct rendering', () => {
    expect(validateDraft(emptyDraft())).toEqual({ term: 'termRequired' });
    expect(validateDraft({ ...emptyDraft(), term: 'Acme', mode: 'mapped' })).toEqual({ rows: 'needRendering' });
    expect(validateDraft({ ...emptyDraft(), term: 'Acme', mode: 'mapped', rows: [{ language: 'hi', text: ' ' }] })).toEqual({
      rows: 'rowIncomplete',
    });
    expect(validateDraft({ ...emptyDraft(), term: 'Acme', mode: 'mapped', rows: [{ language: '', text: 'x' }] })).toEqual({
      rows: 'rowIncomplete',
    });
    expect(
      validateDraft({
        ...emptyDraft(),
        term: 'Acme',
        mode: 'mapped',
        rows: [
          { language: 'hi', text: 'x' },
          { language: 'hi', text: 'y' },
        ],
      }),
    ).toEqual({ rows: 'rowDuplicate' });
    expect(validateDraft({ ...emptyDraft(), term: 'Acme' })).toEqual({});
  });

  it('sends a locked term without renderings even if some were typed, and an empty note as null', () => {
    const d = { term: ' Acme ', mode: 'locked' as const, note: ' ', rows: [{ language: 'hi', text: 'x' }] };
    expect(toInput(d)).toEqual({ term: 'Acme', mode: 'locked', translations: {}, note: null });
    expect(toInput({ ...d, mode: 'mapped', note: ' why ', rows: [{ language: 'hi', text: ' x ' }] })).toEqual({
      term: 'Acme',
      mode: 'mapped',
      translations: { hi: 'x' },
      note: 'why',
    });
  });

  it('round-trips a stored term', () => {
    const term = { term: 'Acme', mode: 'mapped' as const, note: null, translations: { hi: 'x', ta: 'y' } };
    expect(toInput(toDraft(term))).toEqual(term);
  });
});
