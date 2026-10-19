import { ElementOverlay } from './ElementOverlay';
import type { TimecodeAnnotations } from './useTimecodeAnnotations';
import './annotations.css';

/** A small chip over the top corner of the video while the playhead is inside an open annotation's time range (or just
 * after a single moment): its number and first words. Purely visual - the timeline markers are the accessible route. */
export function VideoCaption({ tc }: { tc: TimecodeAnnotations }) {
  if (tc.active.length === 0) return null;
  return (
    <ElementOverlay target={tc.video} className="dam-annot dam-annot--caption" aria-hidden="true">
      <div className="dam-caption-stack">
        {tc.active.slice(0, 2).map((it) => (
          <span key={it.id} className="dam-caption" data-testid="video-caption">
            <span className="dam-caption__badge">{it.number}</span>
            <span className="dam-caption__text">{it.excerpt}</span>
          </span>
        ))}
      </div>
    </ElementOverlay>
  );
}
