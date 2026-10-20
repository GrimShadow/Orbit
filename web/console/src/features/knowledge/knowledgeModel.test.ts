import i18n from '../../i18n';
import {
  SAMPLE_CSV,
  assetHref,
  checkPattern,
  formatYears,
  nestChapters,
  normalizeCode,
  normalizeVin,
  pageRange,
  parseYear,
  sampleCsvHref,
  vinProblem,
  isCode,
} from './knowledgeModel';

const t = i18n.getFixedT('en');

describe('checkPattern (the VIN pattern language)', () => {
  it('accepts literals, ?, sets, ranges and a final *', () => {
    expect(checkPattern('ACM*')).toEqual({ ok: true, positions: 3, literals: 3, open: true });
    expect(checkPattern('ACM??1*')).toEqual({ ok: true, positions: 6, literals: 4, open: true });
    expect(checkPattern('ACM[LS]1*')).toEqual({ ok: true, positions: 5, literals: 4, open: true });
    expect(checkPattern('ACM[A-C]*')).toEqual({ ok: true, positions: 4, literals: 3, open: true });
    expect(checkPattern('acmls1ab1n0123456')).toEqual({ ok: true, positions: 17, literals: 17, open: false });
    expect(checkPattern('*')).toMatchObject({ ok: true, positions: 0, open: true });
  });

  it('counts a set as one position however long it is written', () => {
    expect(checkPattern('[A-CX-Z]????????????????')).toMatchObject({ ok: true, positions: 17 });
  });

  it.each([
    ['', 'empty'],
    ['ACM*1', 'starNotLast'],
    ['**', 'starNotLast'],
    ['AC[M', 'unclosedSet'],
    ['AC[]', 'emptySet'],
    ['AC[M-]', 'badRange'],
    ['AC[C-A]', 'badRange'],
    ['AC[M$]', 'badSetChar'],
    ['AC[-M]', 'badSetChar'],
    ['AC M', 'badChar'],
    ['ACM.', 'badChar'],
    ['ABCDEFGHJKLMNPRST?', 'tooLong'],
  ])('rejects %j as %s', (pattern, problem) => {
    expect(checkPattern(pattern)).toMatchObject({ ok: false, problem });
  });

  it('names the offending character', () => {
    expect(checkPattern('AC#')).toEqual({ ok: false, problem: 'badChar', detail: '#' });
  });
});

describe('VINs', () => {
  it('upper-cases and strips spaces', () => {
    expect(normalizeVin(' acm ls1\tab1n0123456 ')).toBe('ACMLS1AB1N0123456');
  });

  it('accepts 17 letters and digits without I, O or Q (the check digit is not enforced)', () => {
    expect(vinProblem('ACMLS1AB1N0123456')).toBeNull();
    expect(vinProblem('ACMLS1AB1N0123450')).toBeNull();
  });

  it('says what is wrong, wrong characters before length', () => {
    expect(vinProblem('ACMLS1AB1N012345-')).toBe('chars');
    expect(vinProblem('ACMLS1AB1N012345O')).toBe('forbidden');
    expect(vinProblem('ACMLS1AB1N012345Q')).toBe('forbidden');
    expect(vinProblem('ACMLS1AB1N012345I')).toBe('forbidden');
    expect(vinProblem('ACMLS1')).toBe('length');
    expect(vinProblem('')).toBe('length');
    expect(vinProblem('ACMLS1AB1N01234567')).toBe('length');
  });
});

describe('codes', () => {
  it('lower-cases and trims, and accepts a-z 0-9 . _ - up to 100 characters', () => {
    expect(normalizeCode('  Lumen-EV ')).toBe('lumen-ev');
    expect(isCode('lumen-ev_2.0')).toBe(true);
    expect(isCode('lumen ev')).toBe(false);
    expect(isCode('Lumen')).toBe(false);
    expect(isCode('')).toBe(false);
    expect(isCode('a'.repeat(100))).toBe(true);
    expect(isCode('a'.repeat(101))).toBe(false);
  });
});

describe('years', () => {
  it('formats the range of a document', () => {
    expect(formatYears(t, null, null)).toBe('All years');
    expect(formatYears(t, 2022, 2022)).toBe('2022');
    expect(formatYears(t, 2022, null)).toBe('2022');
    expect(formatYears(t, 2021, 2024)).toBe('2021-2024');
  });

  it('parses a year typed into a form', () => {
    expect(parseYear('')).toBeNull();
    expect(parseYear(' 2024 ')).toBe(2024);
    expect(parseYear('24')).toBeNaN();
    expect(parseYear('1899')).toBeNaN();
    expect(parseYear('2101')).toBeNaN();
    expect(parseYear('20x4')).toBeNaN();
  });
});

describe('chapters', () => {
  const c = (title: string, level: number) => ({ title, level });

  it('nests by level', () => {
    const tree = nestChapters([c('A', 1), c('A.1', 2), c('A.1.1', 3), c('A.2', 2), c('B', 1)]);
    expect(tree.map((n) => n.chapter.title)).toEqual(['A', 'B']);
    expect(tree[0]!.children.map((n) => n.chapter.title)).toEqual(['A.1', 'A.2']);
    expect(tree[0]!.children[0]!.children.map((n) => n.chapter.title)).toEqual(['A.1.1']);
    expect(tree[1]!.children).toEqual([]);
  });

  it('copes with a list that starts deep or skips a level', () => {
    const tree = nestChapters([c('deep', 3), c('top', 1), c('skip', 3)]);
    expect(tree.map((n) => n.chapter.title)).toEqual(['deep', 'top']);
    expect(tree[1]!.children.map((n) => n.chapter.title)).toEqual(['skip']);
  });

  it('writes the pages of a chapter', () => {
    expect(pageRange(t, null, null)).toBeNull();
    expect(pageRange(t, 4, 4)).toBe('p. 4');
    expect(pageRange(t, 4, null)).toBe('p. 4');
    expect(pageRange(t, 4, 9)).toBe('pp. 4-9');
  });

  it('links a chapter to its page on the asset page', () => {
    expect(assetHref('a1')).toBe('/library/assets/a1');
    expect(assetHref('a1', 12)).toBe('/library/assets/a1?page=12');
  });
});

describe('the sample CSV', () => {
  it('has the documented header, fictional Acme rows, and patterns that can match a 17-character VIN', () => {
    const [header, ...rows] = SAMPLE_CSV.trim().split('\r\n');
    expect(header).toBe('name,pattern,model,variant,year,theme,priority,enabled');
    expect(rows.length).toBeGreaterThanOrEqual(3);
    for (const row of rows) {
      const cells = row.split(',');
      expect(cells[0]).toMatch(/^Acme /);
      const check = checkPattern(cells[1]!);
      expect(check.ok && (check.open || check.positions === 17)).toBe(true);
    }
  });

  it('is offered as a data: link with a byte-order mark so Excel reads it as UTF-8', () => {
    const href = sampleCsvHref();
    expect(href.startsWith('data:text/csv;charset=utf-8,')).toBe(true);
    const text = decodeURIComponent(href.slice('data:text/csv;charset=utf-8,'.length));
    expect(text.charCodeAt(0)).toBe(0xfeff);
    expect(text.slice(1)).toBe(SAMPLE_CSV);
  });
});
