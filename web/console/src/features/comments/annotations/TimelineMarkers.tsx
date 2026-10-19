import type { CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useCommentsUi } from '../CommentsContext';
import { formatTimecode } from '../commentsApi';
import { assignLanes } from './items';
import { pct } from './geometry';
import type { TimecodeAnnotations } from './useTimecodeAnnotations';
import './annotations.css';

const LANE_PX = 26;

/** The strip under the player: one marker per timecode thread of this version at startMs / duration (a range is a bar),
 * numbered like the thread cards. A marker is a button - click seeks the player and selects the thread. Percent-based, so
 * it re-lays out on its own whenever the strip resizes; it appears once the duration is known. */
export function TimelineMarkers({ tc }: { tc: TimecodeAnnotations }) {
  const { t } = useTranslation();
  const ui = useCommentsUi();
  const { items, durationMs, draft, timeMs } = tc;
  if (durationMs <= 0 || (items.length === 0 && !draft)) return null;

  const lanes = assignLanes(items, durationMs);
  const laneCount = Math.max(1, ...[...lanes.values()].map((l) => l + 1));
  const frac = (ms: number) => Math.min(1, Math.max(0, ms / durationMs));

  return (
    <div className="dam-timeline" role="group" aria-label={t('annotations.timeline')}>
      <div className="dam-timeline__track" style={{ height: laneCount * LANE_PX }}>
        <span className="dam-timeline__playhead" aria-hidden="true" style={{ insetInlineStart: pct(frac(timeMs)) }} />
        {items.map((it) => {
          const selected = ui.selectedThreadId === it.id;
          const hovered = ui.hoveredThreadId === it.id;
          const range = it.endMs != null && it.endMs > it.startMs;
          const text =
            it.endMs != null && range ? `${formatTimecode(it.startMs)}-${formatTimecode(it.endMs)}` : formatTimecode(it.startMs);
          const name = t(it.resolved ? 'annotations.markerResolved' : 'annotations.marker', {
            n: it.number,
            time: text,
            text: it.excerpt,
          });
          const style = {
            insetInlineStart: pct(frac(it.startMs)),
            top: (lanes.get(it.id) ?? 0) * LANE_PX,
            ...(range ? { width: pct(frac(it.endMs as number) - frac(it.startMs)) } : null),
          } as CSSProperties;
          return (
            <button
              key={it.id}
              type="button"
              className="dam-timeline__marker"
              data-kind={range ? 'range' : 'point'}
              data-resolved={it.resolved || undefined}
              data-selected={selected || undefined}
              data-hovered={hovered || undefined}
              data-annotation-id={it.id}
              style={style}
              aria-label={name}
              title={name}
              aria-pressed={selected}
              onClick={() => {
                tc.seek(it.startMs);
                ui.selectThread(it.id);
              }}
              onPointerEnter={() => ui.hoverThread(it.id)}
              onPointerLeave={() => ui.hoverThread(null)}
              onFocus={() => ui.hoverThread(it.id)}
              onBlur={() => ui.hoverThread(null)}
            >
              <span className="dam-timeline__badge" aria-hidden="true">
                {it.number}
              </span>
            </button>
          );
        })}
        {draft && (
          <span
            className="dam-timeline__pending"
            aria-hidden="true"
            data-kind={draft.endMs != null ? 'range' : 'point'}
            style={{
              insetInlineStart: pct(frac(draft.startMs)),
              ...(draft.endMs != null ? { width: pct(frac(draft.endMs) - frac(draft.startMs)) } : null),
            }}
          />
        )}
      </div>
    </div>
  );
}
