import { useEffect, useRef } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useCommentsUi } from '../../comments/CommentsContext';
import { useAssetAi } from '../aiApi';
import { deepLinkSeekMs, parseDeepLinkTime } from './aiFormat';

/** The moment (ms) a `?t=<seconds>` link on the asset page points at - what search results link to ("jump to this moment"). */
export function useDeepLinkTime(): number | null {
  const [params] = useSearchParams();
  return parseDeepLinkTime(params.get('t'));
}

/**
 * Mounted once by the asset page: when the link carries `?t=`, seeks the video there as soon as the stage can seek (the
 * player has registered itself) and the transcript is known, so the seek can snap to the START of the line meant (a link holds
 * whole seconds; the line that starts at 83.4 s is linked as 83). Once per asset and time: the player is the user's afterwards.
 * Without a player (audio, a draft video shown as a still) nothing is seeked; the transcript panel still highlights the line.
 */
export function useDeepLinkSeek(assetId: string): void {
  const ms = useDeepLinkTime();
  const { canSeek, seekTo } = useCommentsUi();
  const ai = useAssetAi(assetId, ms !== null);
  const done = useRef<string | null>(null);
  const waiting = ai.isPending;
  const segments = ai.data?.transcript?.segments;

  useEffect(() => {
    if (ms === null || !canSeek || waiting) return;
    const key = `${assetId}:${ms}`;
    if (done.current === key) return;
    done.current = key;
    seekTo(deepLinkSeekMs(segments, ms));
  }, [assetId, ms, canSeek, waiting, segments, seekTo]);
}
