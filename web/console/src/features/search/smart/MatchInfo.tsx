import { Eye, Microphone, Play, Scan, TextAa } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { formatTimestamp } from '../../ai/aiApi';
import type { SearchHitDto } from '../searchApi';
import { MATCH_KINDS, toSeconds, type MatchKind } from './smartSearch';
import './smart.css';

function visibleKinds(matchedBy: readonly string[] | null | undefined, quietText: boolean): MatchKind[] {
  const kinds = MATCH_KINDS.filter((k) => matchedBy?.includes(k));
  return quietText && kinds.length === 1 && kinds[0] === 'text' ? [] : kinds;
}

const ICONS = { text: TextAa, image: Eye, ocr: Scan, transcript: Microphone } as const;

/** Small "why it matched" chips from `hit.matchedBy`. Each is an icon with its text kept for assistive tech; in a narrow
 * container (a grid card) the words collapse and the icon strip relies on tooltips. `quietText` hides a lone "Text" chip,
 * which on a plain keyword search is true of every hit and so says nothing. */
export function MatchChips({
  matchedBy,
  quietText = false,
}: {
  matchedBy: readonly string[] | null | undefined;
  quietText?: boolean;
}) {
  const { t } = useTranslation();
  const kinds = visibleKinds(matchedBy, quietText);
  if (kinds.length === 0) return null;
  return (
    <div className="dam-matchwrap">
      <ul className="dam-matchchips" aria-label={t('smartSearch.match.label')}>
        {kinds.map((k: MatchKind) => {
          const Icon = ICONS[k];
          const label = t(`smartSearch.match.${k}`);
          return (
            <li key={k} className={`dam-matchchip dam-matchchip--${k}`} title={label}>
              <Icon size={14} weight="bold" aria-hidden="true" />
              <span className="dam-matchchip__label">{label}</span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

/** Up to three spoken moments that matched, each a link to the asset page at that time (`?t=<seconds>`). */
export function TranscriptMatches({ assetId, matches }: { assetId: string; matches: SearchHitDto['transcriptMatches'] }) {
  const { t } = useTranslation();
  if (!matches || matches.length === 0) return null;
  return (
    <ul className="dam-tmatches" aria-label={t('smartSearch.transcript.label')}>
      {matches.map((m) => {
        const time = formatTimestamp(m.startMs);
        return (
          <li key={m.startMs}>
            <Link to={`/library/assets/${assetId}?t=${toSeconds(m.startMs)}`} className="dam-tmatch" title={m.text}>
              <Play size={12} weight="fill" aria-hidden="true" className="dam-tmatch__icon" />
              <span className="dam-sr-only">{t('smartSearch.transcript.jump', { time })}: </span>
              <span className="dam-tmatch__time" aria-hidden="true">
                {time}
              </span>
              <span className="dam-tmatch__text">{m.text}</span>
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

/** Both extras for one hit (each renders nothing when it has nothing to say). */
export function HitMatchInfo({ hit, quietText }: { hit: SearchHitDto; quietText?: boolean }) {
  return (
    <>
      <MatchChips matchedBy={hit.matchedBy} quietText={quietText} />
      <TranscriptMatches assetId={hit.id} matches={hit.transcriptMatches} />
    </>
  );
}

/** True when {@link HitMatchInfo} would render something - lets a card skip its footer slot entirely. */
export function hasMatchInfo(hit: SearchHitDto, quietText: boolean): boolean {
  return visibleKinds(hit.matchedBy, quietText).length > 0 || (hit.transcriptMatches?.length ?? 0) > 0;
}
