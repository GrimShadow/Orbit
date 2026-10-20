import { Sparkle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { useMe } from '../../../api/me';
import { useAssetAi } from '../aiApi';
import { AI_TYPES } from './aiFormat';
import './aiAsset.css';

/** A small chip beside the asset's status when the AI has suggestions waiting for an editor ("3 AI suggestions"); it jumps to
 * the AI panel. Nothing for readers (they cannot decide), for assets with nothing waiting, or while the lookup is loading. */
export function AiSuggestionsChip({ assetId, type, onOpen }: { assetId: string; type: string; onOpen: () => void }) {
  const { t } = useTranslation();
  const canEdit = !!useMe().data?.permissions.includes('assets.update');
  const ai = useAssetAi(assetId, AI_TYPES.has(type) && canEdit).data;
  // The query is shared with the panel, so a reader may well have the data: the chip is for those who can act on it.
  const count = canEdit ? (ai?.suggestions.filter((s) => s.status === 'pending').length ?? 0) : 0;
  if (count === 0) return null;
  return (
    <button type="button" className="dam-badge dam-badge--brand dam-ai-chip" onClick={onOpen} title={t('aiAsset.chip.hint')}>
      <Sparkle size={14} weight="fill" aria-hidden="true" />
      {t('aiAsset.chip.label', { count })}
    </button>
  );
}
