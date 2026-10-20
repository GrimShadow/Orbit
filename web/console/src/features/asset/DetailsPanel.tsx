import { useTranslation } from 'react-i18next';
import { Badge, Card, Skeleton, TagInput } from '../../ui';
import { AiPanel } from '../ai/asset/AiPanel';
import { AssetUsageCard } from '../insights/AssetUsageCard';
import { RightsPanel } from '../rights/asset/RightsPanel';
import { FileInfo } from './FileInfo';
import { WorkflowPanel } from './WorkflowPanel';
import { asCodes } from './channels';
import { useSchema, useTerms, useVocabularies, type AssetDto } from './assetApi';
import type { AssetDraft } from './useAssetDraft';

/** The systems this asset is made available to, as chips - read from the schema's "channel" field (a tenant decides the
 * field and the systems; see `./channels`). Renders nothing for a tenant/type without one. */
function Channels({ field, vocabulary, codes }: { field: string; vocabulary: string; codes: string[] }) {
  const { t } = useTranslation();
  const vocabsQuery = useVocabularies();
  const vocab = vocabsQuery.data?.find((v) => v.key === vocabulary);
  const termsQuery = useTerms(vocab?.id);
  const labels = new Map((termsQuery.data ?? []).map((term) => [term.code, term.label]));
  const loading = vocabsQuery.isPending || (!!vocab && termsQuery.isPending);

  return (
    <Card as="section" padding="md" aria-labelledby={`dam-channels-${field}`}>
      <h2 id={`dam-channels-${field}`} className="dam-metasection__title">
        {t('asset.channels.title')}
      </h2>
      {loading ? (
        <Skeleton height={22} width="60%" />
      ) : codes.length === 0 ? (
        <p className="dam-hint">{t('asset.channels.none')}</p>
      ) : (
        <ul className="dam-chips" aria-label={t('asset.channels.title')}>
          {codes.map((code) => (
            <li key={code}>
              <Badge tone="brand">{labels.get(code) ?? code}</Badge>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}

function ChannelsCard({ asset, draft }: { asset: AssetDto; draft: AssetDraft }) {
  const schema = useSchema(asset.type).data;
  const field = schema?.fields.find((f) => f.accessDimension === 'channel' && f.vocabulary);
  if (!field?.vocabulary) return null;
  return <Channels field={field.name} vocabulary={field.vocabulary} codes={asCodes(draft.values[field.name])} />;
}

/** Everything about the asset that is not its schema form: why it is in this state, where it is available, its tags, when it
 * is scheduled, and the facts about the file itself. */
export function DetailsPanel({ asset, draft }: { asset: AssetDto; draft: AssetDraft }) {
  const { t, i18n } = useTranslation();
  const date = new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium', timeStyle: 'short' });
  const schedule = [
    asset.publishAt && { key: 'publishAt', value: date.format(new Date(asset.publishAt)) },
    asset.expireAt && { key: 'expireAt', value: date.format(new Date(asset.expireAt)) },
  ].filter((x): x is { key: string; value: string } => !!x);

  return (
    <div className="dam-stack dam-stack--fill">
      {asset.status === 'draft' && asset.statusReason && (
        <p className="dam-asset-note" role="note">
          <strong>{t('asset.statusReason')}</strong> {asset.statusReason}
        </p>
      )}
      <WorkflowPanel assetId={asset.id} status={asset.status} />
      <RightsPanel asset={asset} />
      <ChannelsCard asset={asset} draft={draft} />
      <Card as="section" padding="md" aria-label={t('asset.tagsTitle')}>
        <TagInput
          label={t('upload.tags')}
          value={draft.tags}
          onChange={draft.setTags}
          hint={t('asset.tagsHint')}
          placeholder={t('asset.tagsPlaceholder')}
          removeLabel={(tag) => t('asset.removeTag', { tag })}
          listLabel={t('asset.tagList', { field: t('upload.tags') })}
        />
      </Card>
      <AiPanel asset={asset} />
      <AssetUsageCard assetId={asset.id} />
      {(schedule.length > 0 || asset.legalHold) && (
        <Card as="section" padding="md" aria-labelledby="dam-asset-lifecycle">
          <h2 id="dam-asset-lifecycle" className="dam-metasection__title">
            {t('asset.lifecycleTitle')}
          </h2>
          <dl className="dam-facts">
            {schedule.map((s) => (
              <div key={s.key} className="dam-facts__row">
                <dt>{t(`asset.schedule.${s.key}`)}</dt>
                <dd>{s.value}</dd>
              </div>
            ))}
            {asset.legalHold && (
              <div className="dam-facts__row">
                <dt>{t('asset.schedule.legalHold')}</dt>
                <dd>
                  <Badge tone="warning">{t('asset.schedule.legalHoldOn')}</Badge>
                </dd>
              </div>
            )}
          </dl>
        </Card>
      )}
      <FileInfo asset={asset} />
    </div>
  );
}
