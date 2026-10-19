import { ChatCircleDots, Plus } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Button } from '../../../ui';
import { formatTimecode } from '../commentsApi';
import type { TimecodeAnnotations } from './useTimecodeAnnotations';

/** Toolbar controls for the video stage: "Comment at <time>" (shortcut `m`, for marker) and, once a time is pending, "+ end time" to
 * turn it into a range. Hidden without `comments.write`; with no playable video it stays visible but disabled, and says why. */
export function TimecodeTools({ tc, unavailable }: { tc: TimecodeAnnotations; unavailable?: string }) {
  const { t } = useTranslation();
  if (!tc.canAnnotate) return null;
  const canSetEnd = tc.draft !== null && tc.timeMs > tc.draft.startMs;
  return (
    <>
      <Button
        variant="ghost"
        size="sm"
        aria-keyshortcuts="C"
        disabled={Boolean(unavailable)}
        title={unavailable ?? t('annotations.captureHint')}
        onClick={tc.capture}
      >
        <ChatCircleDots size={16} weight="bold" aria-hidden="true" />
        {t('annotations.commentAt', { time: formatTimecode(tc.timeMs) })}
      </Button>
      {tc.draft && (
        <Button
          variant="ghost"
          size="sm"
          disabled={!canSetEnd}
          title={canSetEnd ? undefined : t('annotations.endHint')}
          onClick={tc.setEnd}
        >
          <Plus size={16} weight="bold" aria-hidden="true" />
          {t('annotations.endTime', { time: formatTimecode(Math.max(tc.timeMs, tc.draft.startMs)) })}
        </Button>
      )}
    </>
  );
}
