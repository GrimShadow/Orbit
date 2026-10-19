import { useTranslation } from 'react-i18next';
import { Badge, type BadgeTone } from '../../ui';

const STATUS_TONE: Record<string, BadgeTone> = {
  draft: 'neutral',
  in_review: 'info',
  approved: 'success',
  published: 'success',
  expired: 'warning',
  archived: 'neutral',
  deleted: 'danger',
};

/** The lifecycle status as a tone-coded badge; the label carries the meaning, the tone only reinforces it. */
export function StatusBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  return <Badge tone={STATUS_TONE[status] ?? 'neutral'}>{t(`library.status.${status}`)}</Badge>;
}

export function TypeBadge({ type }: { type: string }) {
  const { t } = useTranslation();
  return <Badge>{t(`library.type.${type}`)}</Badge>;
}
