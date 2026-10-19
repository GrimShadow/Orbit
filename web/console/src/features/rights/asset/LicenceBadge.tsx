import { Clock, Warning, XCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { useAssetRights } from '../rightsApi';
import { headerText, statusTone } from './rightsFormat';
import './rightsAsset.css';

const ICON = { expiring: Warning, expired: XCircle, not_yet_valid: Clock } as const;

/** A small chip beside the asset's status, only when the licence needs attention (ending soon, over, not begun): "Licence
 * expires in 5 days" / "Licence expired". It jumps to the rights panel. Renders nothing for a healthy licence or none. */
export function LicenceBadge({ assetId, onReview }: { assetId: string; onReview?: () => void }) {
  const { t, i18n } = useTranslation();
  const rights = useAssetRights(assetId).data;
  const text = rights ? headerText(t, i18n.language, rights) : null;
  const Icon = rights ? ICON[rights.status as keyof typeof ICON] : undefined;
  if (!rights || !text) return null;
  const cls = `dam-badge dam-badge--${statusTone(rights.status)} dam-rights-licence`;
  const inner = (
    <>
      {Icon && <Icon size={14} weight="fill" aria-hidden="true" />}
      {text}
    </>
  );
  return onReview ? (
    <button type="button" className={`${cls} dam-rights-licence--link`} onClick={onReview} title={t('rightsAsset.header.hint')}>
      {inner}
    </button>
  ) : (
    <span className={cls}>{inner}</span>
  );
}
