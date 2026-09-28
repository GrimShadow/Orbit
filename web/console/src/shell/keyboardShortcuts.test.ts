import { isHelpShortcut, isShortcutTarget, matchNavShortcut } from './keyboardShortcuts';

const key = (overrides: Partial<{ key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean }> = {}) => ({
  key: 'l',
  ctrlKey: false,
  metaKey: false,
  altKey: false,
  ...overrides,
});

describe('matchNavShortcut', () => {
  it('resolves a known key to its path', () => {
    expect(matchNavShortcut(key({ key: 'l' }))).toBe('/library');
    expect(matchNavShortcut(key({ key: 's' }))).toBe('/search');
    expect(matchNavShortcut(key({ key: 'c' }))).toBe('/collections');
    expect(matchNavShortcut(key({ key: 'u' }))).toBe('/upload');
  });

  it('is case-insensitive', () => {
    expect(matchNavShortcut(key({ key: 'L' }))).toBe('/library');
  });

  it('returns undefined for an unmapped key', () => {
    expect(matchNavShortcut(key({ key: 'x' }))).toBeUndefined();
  });

  it('never matches while a modifier is held, even for a mapped key', () => {
    expect(matchNavShortcut(key({ key: 'l', ctrlKey: true }))).toBeUndefined();
    expect(matchNavShortcut(key({ key: 'l', metaKey: true }))).toBeUndefined();
    expect(matchNavShortcut(key({ key: 'l', altKey: true }))).toBeUndefined();
  });
});

describe('isHelpShortcut', () => {
  it('matches a bare "?"', () => {
    expect(isHelpShortcut(key({ key: '?' }))).toBe(true);
  });

  it('does not match "?" with a modifier held', () => {
    expect(isHelpShortcut(key({ key: '?', ctrlKey: true }))).toBe(false);
  });

  it('does not match any other key', () => {
    expect(isHelpShortcut(key({ key: 'l' }))).toBe(false);
  });
});

describe('isShortcutTarget', () => {
  it('allows a plain element (e.g. the document body)', () => {
    expect(isShortcutTarget(document.body)).toBe(true);
  });

  it('allows a non-Element target (e.g. document itself)', () => {
    expect(isShortcutTarget(document)).toBe(true);
  });

  it.each(['input', 'textarea', 'select'])('rejects focus inside a <%s>', (tag) => {
    const el = document.createElement(tag);
    expect(isShortcutTarget(el)).toBe(false);
  });

  it('rejects a contenteditable region', () => {
    const el = document.createElement('div');
    Object.defineProperty(el, 'isContentEditable', { value: true });
    expect(isShortcutTarget(el)).toBe(false);
  });
});
