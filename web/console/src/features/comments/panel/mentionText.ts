import { MENTION_TOKEN, mentionToken } from '../commentsApi';

/** Mentions, in the two forms they live in.
 *
 * Stored / on the wire a mention is a token, `@[Display Name](user:<guid>)`. While typing, the composer's text holds just
 * `@Display Name` and a parallel list of ranges says which of those spans really are people; on submit the ranges are
 * turned back into tokens. Typing inside a mention (or deleting part of it) drops that range, so what you see highlighted
 * is exactly what will notify someone. */

export type Segment = { kind: 'text'; text: string } | { kind: 'mention'; name: string; userId: string };

/** A body split into plain text and mention chips. `mentions` (the server's current names) win over the name in the token. */
export function splitBody(body: string, mentions: readonly { userId: string; displayName: string }[] = []): Segment[] {
  const current = new Map(mentions.map((m) => [m.userId.toLowerCase(), m.displayName]));
  const out: Segment[] = [];
  let last = 0;
  for (const m of body.matchAll(MENTION_TOKEN)) {
    const at = m.index ?? 0;
    if (at > last) out.push({ kind: 'text', text: body.slice(last, at) });
    out.push({ kind: 'mention', name: current.get(m[2]!.toLowerCase()) ?? m[1]!, userId: m[2]! });
    last = at + m[0].length;
  }
  if (last < body.length) out.push({ kind: 'text', text: body.slice(last) });
  return out;
}

/** Tokens shown as `@Name` - for one-line excerpts. */
export const plainText = (body: string) => body.replace(MENTION_TOKEN, (_m, name: string) => `@${name}`);

/** A mention inside the composer text: `text.slice(start, end) === '@' + name`. */
export interface ComposerMention {
  start: number;
  end: number;
  userId: string;
  name: string;
}

const WORD = /[\p{L}\p{N}_]/u;

/** Keeps only the ranges that still read exactly `@Name` and are not glued to a following word. */
function valid(text: string, mentions: readonly ComposerMention[]): ComposerMention[] {
  return mentions.filter((m) => text.slice(m.start, m.end) === `@${m.name}` && !WORD.test(text.charAt(m.end)));
}

/** A stored body to editable text + ranges (for editing a comment). */
export function fromTokens(body: string): { text: string; mentions: ComposerMention[] } {
  let text = '';
  const mentions: ComposerMention[] = [];
  let last = 0;
  for (const m of body.matchAll(MENTION_TOKEN)) {
    const at = m.index ?? 0;
    text += body.slice(last, at);
    const name = m[1]!;
    mentions.push({ start: text.length, end: text.length + 1 + name.length, userId: m[2]!, name });
    text += `@${name}`;
    last = at + m[0].length;
  }
  text += body.slice(last);
  return { text, mentions };
}

/** Editable text + ranges back to the stored form, trimmed. */
export function toTokens(text: string, mentions: readonly ComposerMention[]): string {
  let out = '';
  let at = 0;
  for (const m of valid(
    text,
    [...mentions].sort((a, b) => a.start - b.start),
  )) {
    if (m.start < at) continue;
    out += text.slice(at, m.start) + mentionToken(m.name.replace(/[[\]]/g, ''), m.userId);
    at = m.end;
  }
  return (out + text.slice(at)).trim();
}

/** Re-anchors the ranges after an edit, found by comparing old and new text: ranges before the change stay, ranges after
 * it shift, ranges the change touches are dropped (they are no longer a clean `@Name`). */
export function adjustMentions(oldText: string, newText: string, mentions: readonly ComposerMention[]): ComposerMention[] {
  if (oldText === newText) return [...mentions];
  let prefix = 0;
  const max = Math.min(oldText.length, newText.length);
  while (prefix < max && oldText[prefix] === newText[prefix]) prefix++;
  let suffix = 0;
  while (suffix < max - prefix && oldText[oldText.length - 1 - suffix] === newText[newText.length - 1 - suffix]) suffix++;
  const delta = newText.length - oldText.length;
  const moved: ComposerMention[] = [];
  for (const m of mentions) {
    if (m.end <= prefix) moved.push(m);
    else if (m.start >= oldText.length - suffix) moved.push({ ...m, start: m.start + delta, end: m.end + delta });
  }
  return valid(newText, moved);
}

/** The `@query` being typed at the caret, or null. Queries are one or two words; a mention that is already complete (the
 * caret sits right after it) is not a query. */
export function activeMention(
  text: string,
  caret: number,
  mentions: readonly ComposerMention[],
): { start: number; query: string } | null {
  const m = /(^|[\s(])@([\p{L}\p{N}._'’-]*(?: [\p{L}\p{N}._'’-]*)?)$/u.exec(text.slice(0, caret));
  if (!m) return null;
  const start = m.index + m[1]!.length;
  if (mentions.some((x) => x.start === start)) return null;
  return { start, query: m[2]!.trim() };
}
