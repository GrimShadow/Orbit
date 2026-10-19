import { useTranslation } from 'react-i18next';
import { Button } from '../../ui';

/** Sticky "unsaved changes" bar. Rendered only while the draft differs from the saved asset; Save and Discard act on the
 * whole draft (metadata and tags together), whichever tab is showing. */
export function SaveBar({ saving, onSave, onDiscard }: { saving: boolean; onSave: () => void; onDiscard: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="dam-savebar" role="group" aria-label={t('asset.unsaved')}>
      <p className="dam-savebar__text">{t('asset.unsaved')}</p>
      <div className="dam-savebar__actions">
        <Button variant="ghost" onClick={onDiscard} disabled={saving}>
          {t('asset.discard')}
        </Button>
        <Button variant="primary" onClick={onSave} loading={saving}>
          {t('asset.saveChanges')}
        </Button>
      </div>
    </div>
  );
}
