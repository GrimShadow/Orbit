import { Sparkle } from '@phosphor-icons/react';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Button } from '../../ui';
import { useSchema, type AssetDto } from '../asset/assetApi';
import { mlFields, TranslateDialog } from './TranslateDialog';
import { useTranslateSupport } from './useTranslateSupport';
import './translation.css';

/**
 * "Translate…" for an asset: opens the translate dialog. Shown to editors of an asset whose type has multilingual fields.
 * When AI is off or the provider cannot translate the button is disabled and says why, with a link to the AI settings
 * for those who can change them.
 */
export function TranslateAssetButton({ asset }: { asset: Pick<AssetDto, 'id' | 'type' | 'status'> }) {
  const { t } = useTranslation();
  const support = useTranslateSupport();
  const schema = useSchema(asset.type);
  const [open, setOpen] = useState(false);
  const hintId = useId();

  if (!support.canEdit || asset.status === 'deleted') return null;
  if (support.state === 'loading' || mlFields(schema.data?.fields ?? []).length === 0) return null;

  const ready = support.state === 'ready';
  return (
    <div className="dam-tbtn">
      <Button
        size="sm"
        variant="secondary"
        disabled={!ready}
        aria-describedby={ready ? undefined : hintId}
        onClick={() => setOpen(true)}
      >
        <Sparkle size={14} weight="fill" aria-hidden="true" />
        {t('translation.button')}
      </Button>
      {!ready && (
        <p id={hintId} className="dam-hint dam-tbtn__hint">
          {t(support.state === 'off' ? 'translation.inline.off' : 'translation.inline.unsupported')}{' '}
          {support.canManageAi && <Link to="/settings/ai">{t('translation.inline.settings')}</Link>}
        </p>
      )}
      <TranslateDialog assetId={asset.id} assetType={asset.type} open={open} onClose={() => setOpen(false)} />
    </div>
  );
}
