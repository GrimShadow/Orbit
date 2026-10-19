import { useEffect, useState, type CSSProperties, type PointerEvent as ReactPointerEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useCommentsUi } from '../CommentsContext';
import { ElementOverlay, viewportBox } from './ElementOverlay';
import { anchorFromGesture, normalizePoint, pct, rectBetween, type Pt } from './geometry';
import type { ImageAnnotations } from './useImageAnnotations';
import './annotations.css';

interface Drag {
  pointerId: number;
  start: Pt;
  current: Pt;
}

/** Everything drawn on top of the picture: the existing regions and pins, the pending one, and - in annotate mode - the
 * surface that turns a drag into a region and a click into a pin. All positions are percentages of the picture, so the
 * layer survives resizing and zoom without any recalculation. */
export function AnnotationLayer({ ann }: { ann: ImageAnnotations }) {
  const { t } = useTranslation();
  const ui = useCommentsUi();
  const { img, annotating, items, ranks, visible, draft, page } = ann;
  const [drag, setDrag] = useState<Drag | null>(null);
  // Leaving annotate mode mid-gesture (Escape, the toggle) drops the gesture.
  const [wasAnnotating, setWasAnnotating] = useState(annotating);
  if (wasAnnotating !== annotating) {
    setWasAnnotating(annotating);
    if (!annotating) setDrag(null);
  }
  // Escape during a drag cancels just the drag.
  useEffect(() => {
    if (!drag) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      e.stopPropagation();
      setDrag(null);
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [drag]);

  const pointAt = (e: { clientX: number; clientY: number }): Pt =>
    img ? normalizePoint(e.clientX, e.clientY, viewportBox(img)) : { x: 0, y: 0 };

  const onPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (!annotating || !e.isPrimary || (e.pointerType === 'mouse' && e.button !== 0)) return;
    if ((e.target as Element).closest('.dam-annot__shape')) return;
    // A fresh gesture replaces the pending anchor only once it ends; capture keeps the drag alive outside the picture.
    e.currentTarget.setPointerCapture?.(e.pointerId);
    const p = pointAt(e);
    setDrag({ pointerId: e.pointerId, start: p, current: p });
  };
  const onPointerMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (drag && e.pointerId === drag.pointerId) setDrag({ ...drag, current: pointAt(e) });
  };
  const onPointerUp = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (!drag || e.pointerId !== drag.pointerId) return;
    const end = pointAt(e);
    setDrag(null);
    e.currentTarget.releasePointerCapture?.(e.pointerId);
    ui.setDraftAnchor(anchorFromGesture(drag.start, end, page));
    ui.focusComposer();
  };
  const onPointerCancel = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (drag?.pointerId === e.pointerId) setDrag(null);
  };

  const live = drag ? rectBetween(drag.start, drag.current) : null;
  const pending = live ?? (draft?.type === 'region' ? draft : null);
  const pendingPin = !live && draft?.type === 'point' ? draft : null;

  return (
    <ElementOverlay
      target={img}
      className="dam-annot"
      data-annotating={annotating || undefined}
      data-testid="annotation-layer"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerCancel}
    >
      {visible &&
        items.map((it) => {
          const selected = ui.selectedThreadId === it.id;
          const hovered = ui.hoveredThreadId === it.id;
          const name = t(it.resolved ? 'annotations.itemResolved' : 'annotations.item', { n: it.number, text: it.excerpt });
          const style = {
            left: pct(it.x),
            top: pct(it.y),
            '--annot-z': ranks.get(it.id) ?? 1,
            ...(it.kind === 'region' ? { width: pct(it.w), height: pct(it.h) } : null),
          } as CSSProperties;
          return (
            <button
              key={it.id}
              type="button"
              className={
                it.kind === 'region' ? 'dam-annot__shape dam-annot__shape--region' : 'dam-annot__shape dam-annot__shape--pin'
              }
              style={style}
              data-annotation-id={it.id}
              data-resolved={it.resolved || undefined}
              data-selected={selected || undefined}
              data-hovered={hovered || undefined}
              aria-label={name}
              title={name}
              aria-pressed={selected}
              onClick={() => ui.selectThread(selected ? null : it.id)}
              onPointerEnter={() => ui.hoverThread(it.id)}
              onPointerLeave={() => ui.hoverThread(null)}
              onFocus={() => ui.hoverThread(it.id)}
              onBlur={() => ui.hoverThread(null)}
            >
              <span className="dam-annot__badge" aria-hidden="true">
                {it.number}
              </span>
            </button>
          );
        })}
      {pending && (
        <div
          className="dam-annot__pending"
          data-live={live ? true : undefined}
          aria-hidden="true"
          style={{ left: pct(pending.x), top: pct(pending.y), width: pct(pending.w), height: pct(pending.h) }}
        />
      )}
      {pendingPin && (
        <div
          className="dam-annot__pending dam-annot__pending--pin"
          aria-hidden="true"
          style={{ left: pct(pendingPin.x), top: pct(pendingPin.y) }}
        />
      )}
    </ElementOverlay>
  );
}
