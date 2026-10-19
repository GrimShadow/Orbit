import { act, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { setLanguage } from '../../../i18n';
import { PLAYHEAD_THROTTLE_MS } from './useTimecodeAnnotations';
import { mockImageBox, moment, pin, probe, region, renderStage, resetHarness } from './stageHarness';

/** jsdom has no media pipeline: the video's clock and length are plain variables the tests move. */
const media = { time: 0, duration: Number.NaN, pause: vi.fn() };
let restoreMedia: () => void;
let restoreBox: () => void;
beforeEach(() => {
  setLanguage('en');
  media.time = 0;
  media.duration = Number.NaN;
  media.pause = vi.fn();
  const proto = HTMLMediaElement.prototype;
  const saved = ['currentTime', 'duration', 'pause'].map((k) => [k, Object.getOwnPropertyDescriptor(proto, k)] as const);
  Object.defineProperty(proto, 'currentTime', {
    configurable: true,
    get: () => media.time,
    set: (v: number) => (media.time = v),
  });
  Object.defineProperty(proto, 'duration', { configurable: true, get: () => media.duration });
  Object.defineProperty(proto, 'pause', { configurable: true, value: () => media.pause() });
  restoreMedia = () =>
    saved.forEach(([k, d]) => (d ? Object.defineProperty(proto, k, d) : delete (proto as unknown as Record<string, unknown>)[k]));
  restoreBox = mockImageBox({ left: 0, top: 0, width: 640, height: 360 });
});
afterEach(() => {
  restoreMedia();
  restoreBox();
  resetHarness();
  vi.restoreAllMocks();
});

const video = () => document.querySelector('video') as HTMLVideoElement;
/** The player reports a new length / position. */
const loaded = (seconds: number) =>
  act(() => {
    media.duration = seconds;
    fireEvent(video(), new Event('loadedmetadata'));
  });
const at = (seconds: number, event = 'seeked') =>
  act(() => {
    media.time = seconds;
    fireEvent(video(), new Event(event));
  });
const playing = { type: 'video', status: 'published' } as const;
const threads = [
  moment('a', 25_000, null, { body: 'Cut here' }),
  moment('b', 40_000, 60_000, { body: 'Music too loud in this stretch' }),
  moment('c', 80_000, null, { body: 'Fixed already', resolved: true }),
  moment('old', 10_000, null, { versionId: 'v1', body: 'Old version' }),
  region('r', 0.1, 0.1, 0.1, 0.1),
];
const markers = () => within(screen.getByRole('group', { name: 'Comments on the timeline' })).getAllByRole('button');

describe('timecode capture', () => {
  it('shows the current time on the button, pauses on click and drafts a timecode anchor, then focuses the composer', async () => {
    renderStage(playing);
    const button = await screen.findByRole('button', { name: /Comment at 0:00/ });
    loaded(120);
    at(83.5);
    expect(screen.getByRole('button', { name: 'Comment at 1:23' })).toBe(button);
    await userEvent.click(button);
    expect(media.pause).toHaveBeenCalledTimes(1);
    expect(probe.ui?.draftAnchor).toEqual({ type: 'timecode', startMs: 83_500 });
    expect(probe.focus).toHaveBeenCalledTimes(1);
  });

  it('does the same on the C key while the stage has focus, but not while typing', async () => {
    renderStage(playing);
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(120);
    at(12);
    const canvas = screen.getByRole('region', { name: 'Preview' });
    const input = document.createElement('input');
    canvas.appendChild(input);
    fireEvent.keyDown(input, { key: 'm' });
    fireEvent.keyDown(canvas, { key: 'm', ctrlKey: true });
    expect(probe.ui?.draftAnchor).toBeNull();
    canvas.focus();
    // the app-wide `c` shortcut (go to Collections) listens on the document and must not also fire
    const global = vi.fn();
    document.addEventListener('keydown', global);
    await userEvent.keyboard('m');
    document.removeEventListener('keydown', global);
    expect(global).not.toHaveBeenCalled();
    expect(probe.ui?.draftAnchor).toEqual({ type: 'timecode', startMs: 12_000 });
    expect(media.pause).toHaveBeenCalled();
  });

  it('turns a pending time into a range with "End at", only once the playhead is past the start', async () => {
    renderStage(playing);
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(120);
    expect(screen.queryByRole('button', { name: /^End at/ })).not.toBeInTheDocument();
    at(10);
    await userEvent.click(screen.getByRole('button', { name: /Comment at/ }));
    const end = screen.getByRole('button', { name: /^End at/ });
    expect(end).toBeDisabled();
    expect(end).toHaveAttribute('title', expect.stringContaining('after the start'));
    at(25);
    expect(screen.getByRole('button', { name: 'End at 0:25' })).toBeEnabled();
    await userEvent.click(screen.getByRole('button', { name: 'End at 0:25' }));
    expect(probe.ui?.draftAnchor).toEqual({ type: 'timecode', startMs: 10_000, endMs: 25_000 });
    // the pending range is drawn on the timeline
    expect(document.querySelector('.dam-timeline__pending')).toHaveAttribute('data-kind', 'range');
    expect(document.querySelector('.dam-timeline__pending')).toHaveStyle({ width: `${(15 / 120) * 100}%` });
  });

  it('Escape drops the pending time', async () => {
    renderStage(playing);
    await userEvent.click(await screen.findByRole('button', { name: /Comment at/ }));
    expect(probe.ui?.draftAnchor).not.toBeNull();
    fireEvent.keyDown(screen.getByRole('region', { name: 'Preview' }), { key: 'Escape' });
    expect(probe.ui?.draftAnchor).toBeNull();
  });

  it('is not offered without comments.write, but the timeline still is', async () => {
    renderStage({ ...playing, permissions: ['assets.read'], threads });
    await screen.findByRole('region', { name: 'Preview' });
    loaded(120);
    expect(await screen.findByRole('group', { name: 'Comments on the timeline' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Comment at/ })).not.toBeInTheDocument();
    fireEvent.keyDown(screen.getByRole('region', { name: 'Preview' }), { key: 'm' });
    expect(probe.ui?.draftAnchor).toBeNull();
  });

  it('is visible but off for a video that cannot be played yet, and says why', async () => {
    renderStage({ type: 'video', status: 'draft' });
    const button = await screen.findByRole('button', { name: /Comment at/ });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute('title', 'Video comments need the published video, which can be played.');
    expect(screen.getByText('Video playback is available once the asset is published.')).toBeInTheDocument();
  });
});

describe('timeline markers', () => {
  it('appear once the duration is known, placed by start time, ranges as bars, current version only', async () => {
    renderStage({ ...playing, threads });
    await screen.findByRole('region', { name: 'Preview' });
    await screen.findByRole('button', { name: /Comment at/ });
    expect(screen.queryByRole('group', { name: 'Comments on the timeline' })).not.toBeInTheDocument();
    loaded(100);
    const list = await screen.findAllByRole('button', { name: /^Annotation \d at/ });
    // numbers come from the shared numbering: a=2 (the region is annotation 5 and is not on the timeline)
    expect(list.map((b) => b.getAttribute('aria-label'))).toEqual([
      'Annotation 1 at 0:25: Cut here',
      'Annotation 2 at 0:40-1:00: Music too loud in this stretch',
      'Annotation 3 at 1:20 (resolved): Fixed already',
    ]);
    expect(list[0]).toHaveStyle({ insetInlineStart: '25%' });
    expect(list[0]).toHaveAttribute('data-kind', 'point');
    expect(list[1]).toHaveStyle({ insetInlineStart: '40%', width: '20%' });
    expect(list[1]).toHaveAttribute('data-kind', 'range');
    expect(list[2]).toHaveAttribute('data-resolved', 'true');
    expect(markers()).toHaveLength(3);
  });

  it('re-lays out when the duration changes', async () => {
    renderStage({ ...playing, threads });
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(100);
    expect((await screen.findAllByRole('button', { name: /^Annotation 1 at/ }))[0]).toHaveStyle({ insetInlineStart: '25%' });
    act(() => {
      media.duration = 200;
      fireEvent(video(), new Event('durationchange'));
    });
    expect(screen.getByRole('button', { name: /^Annotation 1 at/ })).toHaveStyle({ insetInlineStart: '12.5%' });
  });

  it('seeks to the start and selects the thread on click, and on Enter for keyboard users', async () => {
    renderStage({ ...playing, threads });
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(100);
    const two = await screen.findByRole('button', { name: /^Annotation 2 at/ });
    await userEvent.click(two);
    expect(media.time).toBe(40);
    expect(probe.ui?.selectedThreadId).toBe('b');
    expect(two).toHaveAttribute('aria-pressed', 'true');
    const one = screen.getByRole('button', { name: /^Annotation 1 at/ });
    one.focus();
    await userEvent.keyboard('{Enter}');
    expect(media.time).toBe(25);
    expect(probe.ui?.selectedThreadId).toBe('a');
  });

  it('shares hover and focus with the thread cards', async () => {
    renderStage({ ...playing, threads });
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(100);
    const one = await screen.findByRole('button', { name: /^Annotation 1 at/ });
    await userEvent.hover(one);
    expect(probe.ui?.hoveredThreadId).toBe('a');
    await userEvent.unhover(one);
    expect(probe.ui?.hoveredThreadId).toBeNull();
    act(() => one.focus());
    expect(probe.ui?.hoveredThreadId).toBe('a');
    act(() => one.blur());
    act(() => probe.ui?.hoverThread('b'));
    expect(screen.getByRole('button', { name: /^Annotation 2 at/ })).toHaveAttribute('data-hovered', 'true');
  });

  it('registers seekTo so clicking a thread card seeks the player', async () => {
    renderStage({ ...playing, threads });
    await screen.findByRole('button', { name: /Comment at/ });
    act(() => probe.ui?.seekTo(61_500));
    expect(media.time).toBe(61.5);
  });

  it('keeps clear of overlap by putting close markers on different rows', async () => {
    renderStage({ ...playing, threads: [moment('x', 50_000, null, { body: 'x' }), moment('y', 50_500, null, { body: 'y' })] });
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(100);
    const [x, y] = await screen.findAllByRole('button', { name: /^Annotation \d at/ });
    expect(x).toHaveStyle({ top: '0px' });
    expect(y).toHaveStyle({ top: '26px' });
  });

  it('has no accessibility violations with markers, a selected one and a pending range', async () => {
    const { container } = renderStage({ ...playing, threads });
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(100);
    await screen.findByRole('group', { name: 'Comments on the timeline' });
    act(() => probe.ui?.selectThread('a'));
    at(50);
    await userEvent.click(screen.getByRole('button', { name: /Comment at/ }));
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('caption and playhead', () => {
  it('shows a chip with the number and first words while the playhead is inside a range, and hides it after', async () => {
    renderStage({ ...playing, threads });
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(100);
    at(30, 'play');
    expect(screen.queryByTestId('video-caption')).not.toBeInTheDocument();
    at(45);
    expect(await screen.findByTestId('video-caption')).toHaveTextContent('2Music too loud in this stretch');
    at(60.5);
    expect(screen.queryByTestId('video-caption')).not.toBeInTheDocument();
  });

  it('keeps the caption of a single moment for about two seconds, and never captions a resolved thread', async () => {
    renderStage({ ...playing, threads });
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(100);
    at(26.5);
    expect(await screen.findByTestId('video-caption')).toHaveTextContent('1Cut here');
    at(27.2);
    expect(screen.queryByTestId('video-caption')).not.toBeInTheDocument();
    at(80.5);
    expect(screen.queryByTestId('video-caption')).not.toBeInTheDocument();
  });

  it('mirrors the playhead into the shared context at about 4 Hz', async () => {
    let now = 1_000_000;
    vi.spyOn(Date, 'now').mockImplementation(() => now);
    renderStage(playing);
    await screen.findByRole('button', { name: /Comment at/ });
    loaded(100);
    expect(probe.ui?.playheadMs).toBe(0);
    now += 100;
    at(1, 'timeupdate');
    expect(probe.ui?.playheadMs).toBe(0); // too soon
    now += PLAYHEAD_THROTTLE_MS;
    at(2, 'timeupdate');
    expect(probe.ui?.playheadMs).toBe(2000);
    now += 10;
    at(9, 'seeked'); // a seek is mirrored at once
    expect(probe.ui?.playheadMs).toBe(9000);
  });
});

it('does not draw regions or pins on a video', async () => {
  renderStage({ ...playing, threads: [pin('p', 0.5, 0.5)] });
  await screen.findByRole('button', { name: /Comment at/ });
  expect(screen.queryByTestId('annotation-layer')).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Annotate' })).not.toBeInTheDocument();
});
