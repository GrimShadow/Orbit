import { useEffect, useMemo, type KeyboardEvent } from 'react';
import { useCommentsUi } from '../CommentsContext';
import { useThreads, type NewAnchor } from '../commentsApi';
import { onPage, shapeItems, stackRanks, type ShapeItem } from './items';
import { ignoreShortcut, useAnnotationsVisible, useCanComment } from './shared';

export interface ImageAnnotations {
  /** The stage's `<img>`: the layer and the pointer maths measure this element. */
  img: HTMLImageElement | null;
  /** 1-based page when the image is a document page; undefined for a plain image. */
  page: number | undefined;
  canAnnotate: boolean;
  annotating: boolean;
  toggleAnnotating: () => void;
  /** Every region/pin on this version (all pages) and the ones drawn right now. */
  total: number;
  items: ShapeItem[];
  ranks: Map<string, number>;
  visible: boolean;
  setVisible: (on: boolean) => void;
  /** The pending (unposted) region/pin if it belongs on what is showing. */
  draft: Extract<NewAnchor, { type: 'region' | 'point' }> | null;
  onStageKeyDown: (e: KeyboardEvent<HTMLElement>) => void;
}

/** Everything the image/page stage needs for the annotation layer: the version's region/pin threads, annotate mode, the
 * pending anchor, and the keyboard shortcuts. The stage renders `<AnnotationLayer>` and `<AnnotationTools>` from it. */
export function useImageAnnotations({
  assetId,
  img,
  page,
  zoom,
  onNavigate,
}: {
  assetId: string;
  /** The rendered `<img>` (held in the stage's state via a callback ref), once it exists. */
  img: HTMLImageElement | null;
  /** Set for a document page: anchors are recorded with it and only that page's annotations are drawn. */
  page?: number;
  zoom: 'fit' | 'actual';
  /** A paged stage jumps to a selected thread's page (a deep link, or a click on a pin of another page). */
  onNavigate?: (page: number) => void;
}): ImageAnnotations {
  const ui = useCommentsUi();
  const { versionId, annotating: modeOn, setAnnotating, draftAnchor, setDraftAnchor, selectedThreadId } = ui;
  const canAnnotate = useCanComment();
  const [visible, setVisible] = useAnnotationsVisible();
  const threads = useThreads(assetId, { versionId: versionId ?? undefined });

  const all = useMemo(() => shapeItems(threads.data?.threads ?? [], versionId), [threads.data, versionId]);
  const items = useMemo(() => onPage(all, page), [all, page]);
  const ranks = useMemo(() => stackRanks(items), [items]);
  const annotating = modeOn && canAnnotate;

  // Leaving the stage ends annotate mode.
  useEffect(() => () => setAnnotating(false), [setAnnotating]);

  // A selected thread on another page brings its page forward; at 100% the selected shape is scrolled into the middle.
  useEffect(() => {
    if (!selectedThreadId) return;
    const hit = all.find((i) => i.id === selectedThreadId);
    if (hit && page !== undefined && (hit.page ?? 1) !== page) onNavigate?.(hit.page ?? 1);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only a *change of selection* navigates, not paging by hand
  }, [selectedThreadId]);
  useEffect(() => {
    if (!selectedThreadId || zoom !== 'actual' || !img?.parentElement) return;
    const canvas = img.parentElement;
    const el = canvas.querySelector<HTMLElement>(`[data-annotation-id="${CSS.escape(selectedThreadId)}"]`);
    if (!el || typeof canvas.scrollBy !== 'function') return;
    const r = el.getBoundingClientRect();
    const c = canvas.getBoundingClientRect();
    canvas.scrollBy({ left: r.left + r.width / 2 - (c.left + c.width / 2), top: r.top + r.height / 2 - (c.top + c.height / 2) });
  }, [selectedThreadId, zoom, img, visible, items]);

  const draft =
    draftAnchor && draftAnchor.type !== 'timecode' && (page === undefined || (draftAnchor.page ?? 1) === page)
      ? draftAnchor
      : null;

  const toggleAnnotating = () => setAnnotating(!annotating);
  const onStageKeyDown = (e: KeyboardEvent<HTMLElement>) => {
    if (e.key === 'Escape') {
      // Cancels the pending region and leaves annotate mode.
      if (!annotating && !draftAnchor) return;
      setDraftAnchor(null);
      setAnnotating(false);
      e.stopPropagation();
      return;
    }
    if (e.key.toLowerCase() === 'a' && canAnnotate && !e.shiftKey && !ignoreShortcut(e)) {
      e.preventDefault();
      toggleAnnotating();
    }
  };

  return {
    img,
    page,
    canAnnotate,
    annotating,
    toggleAnnotating,
    total: all.length,
    items,
    ranks,
    visible,
    setVisible,
    draft,
    onStageKeyDown,
  };
}
