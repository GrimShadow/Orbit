import { toCsv } from './csv';

describe('toCsv', () => {
  it('quotes commas, quotes and newlines', () => {
    expect(toCsv([['a,b', 'say "hi"', 'x\ny', 3, null]])).toBe('"a,b","say ""hi""","x\ny",3,\r\n');
  });
  it('neutralises spreadsheet formulas in text but leaves negative numbers alone', () => {
    expect(toCsv([['=SUM(A1)', '+1', '@x', -5]])).toBe("'=SUM(A1),'+1,'@x,-5\r\n");
  });
});
