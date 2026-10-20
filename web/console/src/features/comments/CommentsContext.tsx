import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import type { NewAnchor } from './commentsApi';

/** State shared between the preview stage (where regions are drawn and timecodes captured) and the side panel (the thread
 * list and composer) on one asset page. Nothing here is persisted: it is just who is selected, hovered or being drafted. */
export interface CommentsUi {
  assetId: string;
  /** The asset type (image, video, document, audio, ...) - decides which anchors make sense. */
  assetType: string;
  /** The version comments are written against and annotations are drawn on (the asset's current version). */
  versionId: string | null;
  /** The thread (root comment id) the user selected - the stage highlights its anchor, the panel scrolls to it. */
  selectedThreadId: string | null;
  selectThread: (id: string | null) => void;
  /** The thread under the pointer in either place, so hovering a card lights up its region and the other way round. */
  hoveredThreadId: string | null;
  hoverThread: (id: string | null) => void;
  /** True while the stage is in annotate mode (drag to draw a region / "comment at current time"). */
  annotating: boolean;
  setAnnotating: (on: boolean) => void;
  /** An anchor just drawn or captured on the stage that the composer should attach to the next comment; null = none. */
  draftAnchor: NewAnchor | null;
  setDraftAnchor: (a: NewAnchor | null) => void;
  /** The stage's current page/time, mirrored here so the panel can show "at 1:23" next to the composer. Stages write it. */
  playheadMs: number | null;
  setPlayheadMs: (ms: number | null) => void;
  /** Panel -> stage: seek the video to a timecode (stages register a handler). */
  seekTo: (ms: number) => void;
  /** True while a stage that can seek is mounted (a playable video); `seekTo` does nothing otherwise. */
  canSeek: boolean;
  registerSeek: (fn: ((ms: number) => void) | null) => void;
  /** Panel -> stage: show this page of a document. */
  showPage: (page: number) => void;
  registerShowPage: (fn: ((page: number) => void) | null) => void;
  /** Stage -> panel: bring the Comments tab forward and focus the composer (after a region is drawn or a time captured). */
  focusComposer: () => void;
  registerFocusComposer: (fn: (() => void) | null) => void;
}

const Ctx = createContext<CommentsUi | null>(null);

export function CommentsProvider({
  assetId,
  assetType,
  versionId,
  children,
}: {
  assetId: string;
  assetType: string;
  versionId: string | null;
  children: ReactNode;
}) {
  const [selectedThreadId, selectThread] = useState<string | null>(null);
  const [hoveredThreadId, hoverThread] = useState<string | null>(null);
  const [annotating, setAnnotating] = useState(false);
  const [draftAnchor, setDraftAnchor] = useState<NewAnchor | null>(null);
  const [playheadMs, setPlayheadMs] = useState<number | null>(null);
  const [seek, setSeek] = useState<((ms: number) => void) | null>(null);
  const [page, setPage] = useState<((p: number) => void) | null>(null);
  const registerSeek = useCallback((fn: ((ms: number) => void) | null) => setSeek(() => fn), []);
  const registerShowPage = useCallback((fn: ((p: number) => void) | null) => setPage(() => fn), []);
  const [focus, setFocus] = useState<(() => void) | null>(null);
  const registerFocusComposer = useCallback((fn: (() => void) | null) => setFocus(() => fn), []);
  const focusComposer = useCallback(() => focus?.(), [focus]);
  const seekTo = useCallback((ms: number) => seek?.(ms), [seek]);
  const showPage = useCallback((p: number) => page?.(p), [page]);
  const value = useMemo<CommentsUi>(
    () => ({
      assetId,
      assetType,
      versionId,
      selectedThreadId,
      selectThread,
      hoveredThreadId,
      hoverThread,
      annotating,
      setAnnotating,
      draftAnchor,
      setDraftAnchor,
      playheadMs,
      setPlayheadMs,
      seekTo,
      canSeek: seek !== null,
      registerSeek,
      showPage,
      registerShowPage,
      focusComposer,
      registerFocusComposer,
    }),
    [
      assetId,
      assetType,
      versionId,
      selectedThreadId,
      hoveredThreadId,
      annotating,
      draftAnchor,
      playheadMs,
      seek,
      seekTo,
      registerSeek,
      showPage,
      registerShowPage,
      focusComposer,
      registerFocusComposer,
    ],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

/** The shared comments UI state; outside a provider (e.g. an isolated stage test) it returns an inert no-op instance. */
export function useCommentsUi(): CommentsUi {
  const v = useContext(Ctx);
  if (v) return v;
  const noop = () => undefined;
  return {
    assetId: '',
    assetType: '',
    versionId: null,
    selectedThreadId: null,
    selectThread: noop,
    hoveredThreadId: null,
    hoverThread: noop,
    annotating: false,
    setAnnotating: noop,
    draftAnchor: null,
    setDraftAnchor: noop,
    playheadMs: null,
    setPlayheadMs: noop,
    seekTo: noop,
    canSeek: false,
    registerSeek: noop,
    showPage: noop,
    registerShowPage: noop,
    focusComposer: noop,
    registerFocusComposer: noop,
  };
}
