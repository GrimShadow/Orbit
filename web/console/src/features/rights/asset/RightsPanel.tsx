import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { useMe } from '../../../api/me';
import { Badge, Button, Card, Modal, Skeleton, useToast } from '../../../ui';
import type { AssetDto } from '../../asset/assetApi';
import { useAssetRights, useSetAssetRights, type Rights, type RightsSummary } from '../rightsApi';
import { AvailabilityCheck } from './AvailabilityCheck';
import { RightsPicker } from './RightsPicker';
import { RightsStatusChip } from './RightsStatusChip';
import { blocksPublishing, effectiveAccess, licenceLabel, problemText, sameSet, validityText } from './rightsFormat';
import { useDimensionTerms, type DimensionTerms } from './useDimensionTerms';
import './rightsAsset.css';

/** The ids the "Review rights" jumps scroll to and focus. */
export const RIGHTS_PANEL_ID = 'dam-rights';
export const RIGHTS_TITLE_ID = 'dam-rights-title';

/** "All channels" / "Worldwide", the licence's chips, or - when the licence and the asset's own metadata leave nothing in
 * common - the plain statement that no channel (region) can get the asset. Never shows the server's `~restricted` marker. */
function Scope({
  dimension,
  codes,
  access,
  terms,
}: {
  dimension: 'channel' | 'region';
  codes: string[];
  access: { none: boolean; list: string[] | null };
  terms: DimensionTerms;
}) {
  const { t } = useTranslation();
  const channel = dimension === 'channel';
  if (access.none)
    return (
      <>
        <Badge tone="danger">{t(channel ? 'rightsAsset.restrictedChannels' : 'rightsAsset.restrictedRegions')}</Badge>
        <p className="dam-hint">{t('rightsAsset.restrictedHint')}</p>
      </>
    );
  const showEffective = access.list !== null && !sameSet(access.list, codes);
  return (
    <>
      {codes.length === 0 ? (
        <span>{t(channel ? 'rightsAsset.allChannels' : 'rightsAsset.worldwide')}</span>
      ) : (
        <ul className="dam-chips" aria-label={t(channel ? 'rightsAsset.channelList' : 'rightsAsset.regionList')}>
          {codes.map((c) => (
            <li key={c}>
              <Badge tone="brand" size="sm">
                {terms.labelOf(c)}
              </Badge>
            </li>
          ))}
        </ul>
      )}
      {showEffective && (
        <p className="dam-hint">{t('rightsAsset.effective', { list: (access.list ?? []).map(terms.labelOf).join(', ') })}</p>
      )}
    </>
  );
}

function Summary({ asset, rights, manager }: { asset: AssetDto; rights: RightsSummary; manager: boolean }) {
  const { t, i18n } = useTranslation();
  const channels = useDimensionTerms(asset.type, 'channel');
  const regions = useDimensionTerms(asset.type, 'region');
  return (
    <>
      <div className="dam-rights__identity">
        <p className="dam-rights__name">{rights.name}</p>
        <div className="dam-rights__badges">
          <Badge>{licenceLabel(t, rights.licenceType)}</Badge>
          <RightsStatusChip rights={rights} />
        </div>
      </div>
      <dl className="dam-facts dam-rights__facts">
        <div className="dam-facts__row">
          <dt>{t('rightsAsset.facts.validity')}</dt>
          <dd>{validityText(t, i18n.language, rights)}</dd>
        </div>
        <div className="dam-facts__row">
          <dt>{t('rightsAsset.facts.channels')}</dt>
          <dd>
            <Scope
              dimension="channel"
              codes={rights.channels}
              access={effectiveAccess(asset.attributes, 'channel')}
              terms={channels}
            />
          </dd>
        </div>
        <div className="dam-facts__row">
          <dt>{t('rightsAsset.facts.regions')}</dt>
          <dd>
            <Scope
              dimension="region"
              codes={rights.regions}
              access={effectiveAccess(asset.attributes, 'region')}
              terms={regions}
            />
          </dd>
        </div>
        {rights.watermarkRequired && (
          <div className="dam-facts__row">
            <dt>{t('rightsAsset.facts.previews')}</dt>
            <dd>
              <Badge tone="warning">{t('rightsAsset.watermark')}</Badge>
              <p className="dam-hint">{t('rightsAsset.watermarkHint')}</p>
            </dd>
          </div>
        )}
        <div className="dam-facts__row">
          <dt>{t('rightsAsset.facts.documents')}</dt>
          <dd>
            {rights.documentsCount > 0
              ? t('rightsAsset.documents', { count: rights.documentsCount })
              : t('rightsAsset.noDocuments')}
          </dd>
        </div>
        {manager && rights.licensor && (
          <div className="dam-facts__row">
            <dt>{t('rightsAsset.facts.licensor')}</dt>
            <dd>{rights.licensor}</dd>
          </div>
        )}
        {manager && rights.notes && (
          <div className="dam-facts__row">
            <dt>{t('rightsAsset.facts.notes')}</dt>
            <dd className="dam-rights__notes">{rights.notes}</dd>
          </div>
        )}
      </dl>
    </>
  );
}

/** Choose the record this asset is under. A published asset cannot go under a licence that is not in force (the server
 * refuses with 409), so those rows say so instead of failing after the click. */
function AssignDialog({ asset, current, onClose }: { asset: AssetDto; current: RightsSummary | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const set = useSetAssetRights(asset.id);
  const [choice, setChoice] = useState<Rights | null>(null);
  const [error, setError] = useState<string | null>(null);
  const published = asset.status === 'published';

  const submit = () => {
    if (!choice) return;
    setError(null);
    set.mutate(choice.id, {
      onSuccess: () => {
        push({ title: t('rightsAsset.assignDialog.done', { name: choice.name }), tone: 'success' });
        onClose();
      },
      onError: (e) => setError(problemText(e)),
    });
  };

  return (
    <div className="dam-rights-dialog">
      <p className="dam-hint">{t('rightsAsset.assignDialog.intro')}</p>
      <RightsPicker
        value={choice?.id ?? null}
        onChange={(r) => {
          setChoice(r);
          setError(null);
        }}
        currentId={current?.id}
        disabledReason={(r) => (published && blocksPublishing(r.status) ? t('rightsAsset.picker.notInForce') : undefined)}
      />
      {error && (
        <p className="dam-error" role="alert">
          {error}
        </p>
      )}
      <div className="dam-rights-dialog__actions">
        <Link className="dam-rights-dialog__link" to="/admin/rights" onClick={onClose}>
          {t('rightsAsset.actions.manage')}
        </Link>
        <Button variant="ghost" onClick={onClose}>
          {t('rightsAsset.assignDialog.cancel')}
        </Button>
        <Button variant="primary" disabled={!choice || choice.id === current?.id} loading={set.isPending} onClick={submit}>
          {current ? t('rightsAsset.assignDialog.submitChange') : t('rightsAsset.assignDialog.submit')}
        </Button>
      </div>
    </div>
  );
}

function RemoveDialog({ assetId, current, onClose }: { assetId: string; current: RightsSummary; onClose: () => void }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const set = useSetAssetRights(assetId);
  const [error, setError] = useState<string | null>(null);
  const remove = () =>
    set.mutate(null, {
      onSuccess: () => {
        push({ title: t('rightsAsset.removeDialog.done'), tone: 'success' });
        onClose();
      },
      onError: (e) => setError(problemText(e)),
    });
  return (
    <div className="dam-rights-dialog">
      <p>{t('rightsAsset.removeDialog.body', { name: current.name })}</p>
      {error && (
        <p className="dam-error" role="alert">
          {error}
        </p>
      )}
      <div className="dam-rights-dialog__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('rightsAsset.removeDialog.cancel')}
        </Button>
        <Button variant="danger" loading={set.isPending} onClick={remove}>
          {t('rightsAsset.removeDialog.confirm')}
        </Button>
      </div>
    </div>
  );
}

/** The asset's licence, on the asset page: what it is called, whether it is in force, where and until when the asset may be
 * used, whether previews must be watermarked - plus, for people who manage rights, who the licensor is and the controls to
 * assign, change or remove it. Anyone who can read the asset can see the summary and run the availability check. */
export function RightsPanel({ asset }: { asset: AssetDto }) {
  const { t } = useTranslation();
  const me = useMe().data;
  const query = useAssetRights(asset.id);
  const [dialog, setDialog] = useState<'assign' | 'remove' | null>(null);
  const close = () => setDialog(null);
  const rights = query.data ?? null;
  const manager = !!me?.permissions.includes('rights.manage') && asset.status !== 'deleted';

  const controls = manager && !query.isPending && !query.error && (
    <div className="dam-rights__controls">
      <Button size="sm" variant="secondary" onClick={() => setDialog('assign')}>
        {rights ? t('rightsAsset.actions.change') : t('rightsAsset.actions.assign')}
      </Button>
      {rights && (
        <Button size="sm" variant="ghost" onClick={() => setDialog('remove')}>
          {t('rightsAsset.actions.remove')}
        </Button>
      )}
    </div>
  );

  return (
    <Card as="section" padding="md" id={RIGHTS_PANEL_ID} aria-labelledby={RIGHTS_TITLE_ID} className="dam-rights">
      <div className="dam-rights__head">
        <h2 id={RIGHTS_TITLE_ID} tabIndex={-1} className="dam-metasection__title">
          {t('rightsAsset.title')}
        </h2>
        {controls}
      </div>

      {query.isPending ? (
        <div role="status" aria-label={t('rightsAsset.loading')}>
          <Skeleton width="55%" height={18} />
          <Skeleton height={48} />
        </div>
      ) : query.error ? (
        <div role="alert" className="dam-rights__empty">
          <p>{t('rightsAsset.loadFailed')}</p>
          <Button size="sm" onClick={() => void query.refetch()}>
            {t('rightsAsset.retry')}
          </Button>
        </div>
      ) : rights ? (
        <>
          <Summary asset={asset} rights={rights} manager={manager} />
          <AvailabilityCheck assetId={asset.id} assetType={asset.type} />
        </>
      ) : (
        <div className="dam-rights__empty">
          <p className="dam-rights__empty-title">{t('rightsAsset.empty.title')}</p>
          <p className="dam-hint">{t('rightsAsset.empty.hint')}</p>
        </div>
      )}

      <Modal
        open={dialog === 'assign'}
        onClose={close}
        title={rights ? t('rightsAsset.assignDialog.changeTitle') : t('rightsAsset.assignDialog.assignTitle')}
        closeLabel={t('rightsAsset.close')}
      >
        <AssignDialog asset={asset} current={rights} onClose={close} />
      </Modal>
      <Modal
        open={dialog === 'remove' && !!rights}
        onClose={close}
        title={t('rightsAsset.removeDialog.title')}
        closeLabel={t('rightsAsset.close')}
      >
        {rights && <RemoveDialog assetId={asset.id} current={rights} onClose={close} />}
      </Modal>
    </Card>
  );
}
