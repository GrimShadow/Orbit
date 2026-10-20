import { parseDeepLinkPage } from './deepLinkPage';

describe('parseDeepLinkPage', () => {
  it('reads a whole page number from 1', () => {
    expect(parseDeepLinkPage('7')).toBe(7);
    expect(parseDeepLinkPage('120')).toBe(120);
  });
  it('ignores anything else', () => {
    for (const bad of [null, '', '0', '-2', '3.5', 'abc', '1e3', ' 4', '1234567'])
      expect(parseDeepLinkPage(bad), String(bad)).toBeNull();
  });
});
