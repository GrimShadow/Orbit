import { adjustMentions, activeMention, fromTokens, plainText, splitBody, toTokens, type ComposerMention } from './mentionText';

const ID = '0197b000-0000-7000-8000-00000000a001';
const ID2 = '0197b000-0000-7000-8000-00000000a002';
const token = (name: string, id = ID) => `@[${name}](user:${id})`;

describe('splitBody', () => {
  it('splits text and mention tokens, preferring the current name from the mentions list', () => {
    const segs = splitBody(`Hi ${token('Old Name')}, see this`, [{ userId: ID, displayName: 'Priya Natarajan' }]);
    expect(segs).toEqual([
      { kind: 'text', text: 'Hi ' },
      { kind: 'mention', name: 'Priya Natarajan', userId: ID },
      { kind: 'text', text: ', see this' },
    ]);
  });
  it('keeps angle brackets and markup as plain text', () => {
    expect(splitBody('<b>x</b>')).toEqual([{ kind: 'text', text: '<b>x</b>' }]);
  });
  it('shows tokens as @Name in plain excerpts', () => {
    expect(plainText(`ping ${token('Sam Reviewer', ID2)} now`)).toBe('ping @Sam Reviewer now');
  });
});

describe('tokens <-> composer text', () => {
  it('round-trips a stored body through editable text and back', () => {
    const body = `Hi ${token('Priya Natarajan')} and ${token('Sam Reviewer', ID2)}!`;
    const { text, mentions } = fromTokens(body);
    expect(text).toBe('Hi @Priya Natarajan and @Sam Reviewer!');
    expect(mentions.map((m) => text.slice(m.start, m.end))).toEqual(['@Priya Natarajan', '@Sam Reviewer']);
    expect(toTokens(text, mentions)).toBe(body);
  });
  it('turns a range that no longer reads @Name into plain text', () => {
    const mentions: ComposerMention[] = [{ start: 0, end: 6, userId: ID, name: 'Priya' }];
    expect(toTokens('@Priyx hello', mentions)).toBe('@Priyx hello');
    expect(toTokens('@Priya hello', mentions)).toBe(`${token('Priya')} hello`);
  });
  it('does not mention when a word is glued on after the name', () => {
    expect(toTokens('@Priyaish', [{ start: 0, end: 6, userId: ID, name: 'Priya' }])).toBe('@Priyaish');
  });
  it('trims the result', () => {
    expect(toTokens('  hi  ', [])).toBe('hi');
  });
});

describe('adjustMentions', () => {
  const m: ComposerMention[] = [{ start: 6, end: 12, userId: ID, name: 'Priya' }];
  it('shifts a mention when text is typed before it', () => {
    expect(adjustMentions('hello @Priya', 'hello!! @Priya', m)).toEqual([{ start: 8, end: 14, userId: ID, name: 'Priya' }]);
  });
  it('keeps a mention when text is typed after it', () => {
    expect(adjustMentions('hello @Priya', 'hello @Priya now', m)).toEqual(m);
  });
  it('drops a mention that is edited inside', () => {
    expect(adjustMentions('hello @Priya', 'hello @Prya', m)).toEqual([]);
    expect(adjustMentions('hello @Priya', 'hello @Pxriya', m)).toEqual([]);
  });
  it('drops a mention when a letter is typed straight after it', () => {
    expect(adjustMentions('hello @Priya', 'hello @Priyas', m)).toEqual([]);
  });
  it('shifts a mention back when text before it is deleted', () => {
    expect(adjustMentions('hello @Priya', 'hell @Priya', m)).toEqual([{ start: 5, end: 11, userId: ID, name: 'Priya' }]);
  });
});

describe('activeMention', () => {
  it('finds the @query before the caret', () => {
    expect(activeMention('hi @pri', 7, [])).toEqual({ start: 3, query: 'pri' });
    expect(activeMention('@sam rev', 8, [])).toEqual({ start: 0, query: 'sam rev' });
  });
  it('needs the @ to start a word, and at most two words', () => {
    expect(activeMention('mail a@b', 8, [])).toBeNull();
    expect(activeMention('@a b c', 6, [])).toBeNull();
  });
  it('is not a query once the mention is complete', () => {
    expect(activeMention('@Priya Natarajan ', 17, [{ start: 0, end: 16, userId: ID, name: 'Priya Natarajan' }])).toBeNull();
  });
});
