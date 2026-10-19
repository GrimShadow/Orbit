import { CheckCircle, Clock, Warning, XCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Badge } from '../../../ui';
import { statusText, statusTone, type StatusFacts } from './rightsFormat';
import './rightsAsset.css';

const ICON = { valid: CheckCircle, expiring: Warning, expired: XCircle, not_yet_valid: Clock } as const;

/** A licence's state as a tone-coded chip: the colour only reinforces the words and the icon. */
export function RightsStatusChip({ rights, size = 'md' }: { rights: StatusFacts; size?: 'sm' | 'md' }) {
  const { t, i18n } = useTranslation();
  const Icon = ICON[rights.status as keyof typeof ICON];
  return (
    <Badge tone={statusTone(rights.status)} size={size}>
      <span className="dam-rights-chip">
        {Icon && <Icon size={size === 'sm' ? 12 : 14} weight="fill" aria-hidden="true" />}
        {statusText(t, i18n.language, rights)}
      </span>
    </Badge>
  );
}
