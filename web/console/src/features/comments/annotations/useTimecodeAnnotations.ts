import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { useCommentsUi } from '../CommentsContext';
import { useThreads } from '../commentsApi';
import { activeAt, timeItems, type TimeItem } from './items';
import { ignoreShortcut, useCanComment } from './shared';

/** Playhead updates reach the shared context at about 4 Hz. */
export const PLAYHEAD_THROTTLE_MS = 250;
/** The longest time an anchor may have (24 h, ADR-0032). */
const MAX_MS = 24 * 3600 * 1000;

export interface TimecodeAnnotations {
  video: HTMLVideoElement | null;
  canAnnotate: boolean;
  /** Where the player is (ms), and how long it is (0 until the metadata has loaded). */
  timeMs: number;
  durationMs: number;
  items: TimeItem[];
  /** Captions to show right now. */
  active: TimeItem[];
  /** The pending timecode (start, optional end), if any. */
  draft: { startMs: number; endMs: number | null } | null;
  capture: () => void;
  setEnd: () => void;
  seek: (ms: number) => void;
  onStageKeyDown: (e: KeyboardEvent<HTMLElement>) => void;
}

const seekElement = (el: HTMLMediaElement, ms: number) => {
  el.currentTime = Math.max(0, ms) / 1000;
};
const toMs = (seconds: number) => Math.min(MAX_MS, Math.max(0, Math.round(seconds * 1000)));

/** The video stage's side of comments: timecode capture (pausing the player), the version's timecode threads for the
 * timeline and captions, and the seek handler the thread cards use. */
export function useTimecodeAnnotations(assetId: string, video: HTMLVideoElement | null): TimecodeAnnotations {
  const ui = useCommentsUi();
  const { versionId, draftAnchor, setDraftAnchor, setPlayheadMs, registerSeek } = ui;
  const canAnnotate = useCanComment();
  const [timeMs, setTimeMs] = useState(0);
  const [durationMs, setDurationMs] = useState(0);
  const threads = useThreads(assetId, { versionId: versionId ?? undefined });
  const items = useMemo(() => timeItems(threads.data?.threads ?? [], versionId), [threads.data, versionId]);
  const active = useMemo(() => activeAt(items, timeMs), [items, timeMs]);

  const lastMirror = useRef(0);
  useEffect(() => {
    if (!video) return;
    const sync = () => {
      const ms = toMs(video.currentTime);
      setTimeMs(ms);
      setPlayheadMs(ms);
      lastMirror.current = Date.now();
    };
    const onTimeUpdate = () => {
      if (Date.now() - lastMirror.current >= PLAYHEAD_THROTTLE_MS) sync();
    };
    const onDuration = () => setDurationMs(Number.isFinite(video.duration) ? toMs(video.duration) : 0);
    onDuration();
    sync();
    const immediate = ['seeked', 'pause', 'play', 'ended'] as const;
    video.addEventListener('timeupdate', onTimeUpdate);
    video.addEventListener('loadedmetadata', onDuration);
    video.addEventListener('durationchange', onDuration);
    for (const ev of immediate) video.addEventListener(ev, sync);
    return () => {
      video.removeEventListener('timeupdate', onTimeUpdate);
      video.removeEventListener('loadedmetadata', onDuration);
      video.removeEventListener('durationchange', onDuration);
      for (const ev of immediate) video.removeEventListener(ev, sync);
      setPlayheadMs(null);
    };
  }, [video, setPlayheadMs]);

  const seek = useCallback(
    (ms: number) => {
      if (!video) return;
      seekElement(video, ms);
    },
    [video],
  );
  // Panel -> stage: a thread card seeks the player.
  useEffect(() => {
    if (!video) return;
    registerSeek(seek);
    return () => registerSeek(null);
  }, [video, seek, registerSeek]);

  const draft = draftAnchor?.type === 'timecode' ? { startMs: draftAnchor.startMs, endMs: draftAnchor.endMs ?? null } : null;

  const capture = () => {
    if (!video) return;
    video.pause();
    setDraftAnchor({ type: 'timecode', startMs: toMs(video.currentTime) });
    ui.focusComposer();
  };
  const setEnd = () => {
    if (!video || draftAnchor?.type !== 'timecode') return;
    const end = toMs(video.currentTime);
    if (end > draftAnchor.startMs) setDraftAnchor({ type: 'timecode', startMs: draftAnchor.startMs, endMs: end });
  };

  const onStageKeyDown = (e: KeyboardEvent<HTMLElement>) => {
    if (e.key === 'Escape') {
      if (!draftAnchor) return;
      setDraftAnchor(null);
      e.stopPropagation();
      return;
    }
    if (e.key.toLowerCase() === 'm' && canAnnotate && video && !e.shiftKey && !ignoreShortcut(e)) {
      e.preventDefault();
      // `m` is unused app-wide (`c` is Collections), but the stage still owns the key while it has focus.
      e.stopPropagation();
      capture();
    }
  };

  return { video, canAnnotate, timeMs, durationMs, items, active, draft, capture, setEnd, seek, onStageKeyDown };
}
