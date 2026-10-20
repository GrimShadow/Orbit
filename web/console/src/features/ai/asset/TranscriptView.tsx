import { Copy, MagnifyingGlass } from '@phosphor-icons/react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, useToast } from '../../../ui';
import { useCommentsUi } from '../../comments/CommentsContext';
import { formatTimestamp, type AiTranscript } from '../aiApi';
import { deepLinkIndex, filterSegments, languageName, matchParts, segmentIndexAt, transcriptAsText } from './aiFormat';

/** The transcript text with the filter's matches marked. */
function Highlighted({ text, query }: { text: string; query: string }) {
  const parts = matchParts(text, query);
  return (
    <>
      {parts.map((p, i) =>
        p.match ? (
          <mark key={i} className="dam-ai-tx__mark">
            {p.text}
          </mark>
        ) : (
          <span key={i}>{p.text}</span>
        ),
      )}
    </>
  );
}

/**
 * The words spoken in a video or recording, one timestamped line each. With a player on the page, a line seeks it and the line
 * being played is highlighted (and kept in view); without one (audio, a draft video shown as a still) the timestamps are just
 * text. `deepLinkMs` is a `?t=` link: that line is highlighted and scrolled to even when there is no player to seek.
 */
export function TranscriptView({ transcript, deepLinkMs }: { transcript: AiTranscript; deepLinkMs: number | null }) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const { canSeek, seekTo, playheadMs } = useCommentsUi();
  const [query, setQuery] = useState('');
  const root = useRef<HTMLDivElement>(null);
  const list = useRef<HTMLOListElement>(null);
  const hovering = useRef(false);
  const segments = transcript.segments;

  const shown = useMemo(() => filterSegments(segments, query), [segments, query]);
  const linkIndex = useMemo(() => (deepLinkMs === null ? -1 : deepLinkIndex(segments, deepLinkMs)), [segments, deepLinkMs]);
  // The player's position wins once it has moved off the start; before that (and with no player) the link says where we are.
  const following = canSeek && playheadMs !== null && playheadMs > 0;
  const active = following ? segmentIndexAt(segments, playheadMs) : linkIndex;

  const centre = (index: number) => {
    const el = list.current;
    const row = el?.querySelector<HTMLElement>(`[data-index="${index}"]`);
    if (!el || !row) return;
    el.scrollTop = Math.max(0, row.offsetTop - (el.clientHeight - row.offsetHeight) / 2);
  };

  // Keep the line being played in view - unless the person is reading or scrolling the list themselves.
  useEffect(() => {
    if (active >= 0 && following && !hovering.current && !list.current?.contains(document.activeElement)) centre(active);
  }, [active, following]);

  // A `?t=` link: bring the card into view once and put the linked line in the middle of the list.
  const revealed = useRef<number | null>(null);
  useEffect(() => {
    if (deepLinkMs === null || linkIndex < 0 || revealed.current === deepLinkMs) return;
    revealed.current = deepLinkMs;
    root.current?.scrollIntoView?.({ block: 'nearest' });
    centre(linkIndex);
  }, [deepLinkMs, linkIndex]);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(transcriptAsText(segments));
      push({ title: t('aiAsset.transcript.copied'), tone: 'success', durationMs: 3000 });
    } catch {
      push({ title: t('aiAsset.transcript.copyFailed'), tone: 'error' });
    }
  };

  const meta = [
    transcript.language ? languageName(transcript.language, i18n.language) : null,
    transcript.durationMs ? formatTimestamp(transcript.durationMs) : null,
    t('aiAsset.transcript.lines', { count: segments.length }),
    transcript.model,
  ].filter(Boolean);

  return (
    <section ref={root} className="dam-ai-sec" aria-labelledby="dam-ai-transcript-title">
      <div className="dam-ai-sec__head">
        <h3 id="dam-ai-transcript-title" className="dam-ai-sub">
          {t('aiAsset.transcript.title')}
        </h3>
        <Button size="sm" variant="ghost" onClick={() => void copy()}>
          <Copy size={16} weight="bold" aria-hidden="true" />
          {t('aiAsset.transcript.copy')}
        </Button>
      </div>
      <p className="dam-hint dam-ai-hint">{meta.join(' · ')}</p>
      <div className="dam-ai-search">
        <MagnifyingGlass size={16} weight="bold" aria-hidden="true" className="dam-ai-search__icon" />
        <input
          type="search"
          className="dam-input dam-ai-search__input"
          aria-label={t('aiAsset.transcript.filter')}
          placeholder={t('aiAsset.transcript.filterPlaceholder')}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
      </div>
      <p className="dam-hint dam-ai-count" role="status">
        {query.trim()
          ? shown.length > 0
            ? t('aiAsset.transcript.count', { shown: shown.length, total: segments.length })
            : t('aiAsset.transcript.none', { query: query.trim() })
          : ''}
      </p>
      {shown.length > 0 && (
        <ol
          ref={list}
          className="dam-ai-tx"
          aria-label={t('aiAsset.transcript.list')}
          // A scrolling region needs the keyboard; when its lines are buttons they are the way in already.
          tabIndex={canSeek ? undefined : 0}
          onPointerEnter={() => (hovering.current = true)}
          onPointerLeave={() => (hovering.current = false)}
        >
          {shown.map(({ segment, index }) => {
            const time = formatTimestamp(segment.startMs);
            const body = (
              <>
                <span className="dam-ai-tx__time">{time}</span>
                <span className="dam-ai-tx__text">
                  <Highlighted text={segment.text} query={query} />
                </span>
              </>
            );
            return (
              <li key={index} className="dam-ai-tx__row" data-index={index} data-active={index === active || undefined}>
                {canSeek ? (
                  <button
                    type="button"
                    className="dam-ai-tx__line"
                    aria-current={index === active ? 'true' : undefined}
                    title={t('aiAsset.transcript.jump', { time })}
                    onClick={() => seekTo(segment.startMs)}
                  >
                    {body}
                  </button>
                ) : (
                  <div className="dam-ai-tx__line" aria-current={index === active ? 'true' : undefined}>
                    {body}
                  </div>
                )}
              </li>
            );
          })}
        </ol>
      )}
    </section>
  );
}
