import {
  deepLinkIndex,
  deepLinkSeekMs,
  expectedSteps,
  filterSegments,
  formatStepDuration,
  hasAiContent,
  languageName,
  matchParts,
  parseDeepLinkTime,
  percent,
  segmentIndexAt,
  transcriptAsText,
  type Segment,
} from './aiFormat';
import type { AssetAi } from '../aiApi';

const SEGS: Segment[] = [
  { startMs: 0, endMs: 4000, text: 'Welcome to the Acme launch.' },
  { startMs: 4000, endMs: 9000, text: 'Today we show the new line.' },
  { startMs: 12000, endMs: 15000, text: 'Questions are welcome.' },
  { startMs: 83400, endMs: 87000, text: 'Thank you for coming.' },
];

describe('transcript timing', () => {
  it('finds the line being spoken (start inclusive, end exclusive) and nothing in a pause', () => {
    expect(segmentIndexAt(SEGS, 0)).toBe(0);
    expect(segmentIndexAt(SEGS, 3999)).toBe(0);
    expect(segmentIndexAt(SEGS, 4000)).toBe(1);
    expect(segmentIndexAt(SEGS, 10000)).toBe(-1);
    expect(segmentIndexAt(SEGS, 99999)).toBe(-1);
    expect(segmentIndexAt([], 10)).toBe(-1);
  });

  it('resolves a whole-second link to the line that starts in that second, else the line being spoken, else the last before it', () => {
    // 83.4 s is linked as t=83: the line that STARTS inside second 83 wins over silence before it.
    expect(deepLinkIndex(SEGS, 83000)).toBe(3);
    expect(deepLinkIndex(SEGS, 5000)).toBe(1);
    expect(deepLinkIndex(SEGS, 10000)).toBe(1);
    expect(deepLinkIndex(SEGS, 200000)).toBe(3);
    expect(deepLinkIndex([], 1000)).toBe(-1);
    expect(deepLinkIndex([{ startMs: 5000, endMs: 6000, text: 'x' }], 1000)).toBe(-1);
  });

  it('seeks to the start of the line a link means, but to the time itself when it falls in a pause', () => {
    expect(deepLinkSeekMs(SEGS, 83000)).toBe(83400);
    expect(deepLinkSeekMs(SEGS, 5000)).toBe(4000);
    expect(deepLinkSeekMs(SEGS, 10000)).toBe(10000);
    expect(deepLinkSeekMs(undefined, 10000)).toBe(10000);
  });

  it('accepts plain non-negative seconds in a link and nothing else', () => {
    expect(parseDeepLinkTime('83')).toBe(83000);
    expect(parseDeepLinkTime('83.5')).toBe(83500);
    expect(parseDeepLinkTime('0')).toBe(0);
    for (const bad of [null, '', '-5', 'abc', '1:23', '1e3', '83s', '999999999', '90000'])
      expect(parseDeepLinkTime(bad)).toBeNull();
  });
});

describe('transcript text', () => {
  it('copies one stamped line per segment', () => {
    expect(transcriptAsText(SEGS.slice(0, 2))).toBe('[0:00] Welcome to the Acme launch.\n[0:04] Today we show the new line.');
  });

  it("filters ignoring case and accents, and keeps each line's index", () => {
    const hits = filterSegments([...SEGS, { startMs: 90000, endMs: 91000, text: 'Café menu' }], 'CAFE');
    expect(hits.map((h) => h.index)).toEqual([4]);
    expect(filterSegments(SEGS, '  ')).toHaveLength(SEGS.length);
    expect(filterSegments(SEGS, 'zzz')).toEqual([]);
  });

  it('marks every match for highlighting', () => {
    expect(matchParts('Welcome, welcome', 'WEL')).toEqual([
      { text: 'Wel', match: true },
      { text: 'come, ', match: false },
      { text: 'wel', match: true },
      { text: 'come', match: false },
    ]);
    expect(matchParts('plain', '')).toEqual([{ text: 'plain', match: false }]);
    expect(matchParts('a.b', '.')).toEqual([
      { text: 'a', match: false },
      { text: '.', match: true },
      { text: 'b', match: false },
    ]);
  });
});

describe('small formatters', () => {
  it('formats step durations', () => {
    expect(formatStepDuration(850)).toBe('850 ms');
    expect(formatStepDuration(1234)).toBe('1.2 s');
    expect(formatStepDuration(125_000)).toBe('2 min 5 s');
  });

  it('turns a confidence into a clamped whole percent', () => {
    expect(percent(0.824)).toBe(82);
    expect(percent(1.4)).toBe(100);
    expect(percent(-1)).toBe(0);
  });

  it('names a language in the console language and falls back to the code', () => {
    expect(languageName('en', 'en')).toBe('English');
    expect(languageName('not a code!', 'en')).toBe('not a code!');
  });

  it('knows the steps of each kind of asset', () => {
    expect(expectedSteps('audio')).toEqual(['transcript', 'language', 'text-embedding']);
    expect(expectedSteps('video')).toContain('transcript');
    expect(expectedSteps('image')).toContain('ocr');
    expect(expectedSteps('document')).toContain('text-layer');
  });

  it('says whether there is anything to show', () => {
    const base = {
      status: 'none',
      run: null,
      suggestions: [],
      language: null,
      ocrText: null,
      ocrTextTruncated: false,
      ocrLanguage: null,
      transcript: null,
      embeddingsReady: false,
    } as AssetAi;
    expect(hasAiContent(undefined)).toBe(false);
    expect(hasAiContent(base)).toBe(false);
    expect(hasAiContent({ ...base, status: 'disabled' })).toBe(false);
    expect(hasAiContent({ ...base, status: 'running' })).toBe(true);
    expect(hasAiContent({ ...base, status: 'disabled', ocrText: 'hello' })).toBe(true);
  });
});
