import { useTranslation } from 'react-i18next';
import { Button, EmptyState, LoadingRegion, Skeleton } from '../../ui';
import { TranslateAssetButton } from '../translation/TranslateAssetButton';
import { isEmptyValue, MetadataFields } from './MetadataFields';
import type { AssetDraft } from './useAssetDraft';
import { useSchema, type AssetDto } from './assetApi';

interface MetadataFormProps {
  asset: Pick<AssetDto, 'id' | 'type' | 'status'>;
  draft: AssetDraft;
}

/** The form generated from the asset type's current schema (spec Step 1.8: "metadata form generated from schema"). The
 * values live in `draft` (see `useAssetDraft`), which also keeps fields the schema no longer knows about untouched in the
 * saved document; this only renders the fields it is told about. */
export function MetadataForm({ asset, draft }: MetadataFormProps) {
  const { t } = useTranslation();
  const schemaQuery = useSchema(asset.type);

  if (schemaQuery.isPending)
    return (
      <LoadingRegion label={t('asset.metadataLoading')}>
        <div className="dam-stack dam-stack--fill">
          <Skeleton height={120} />
          <Skeleton height={160} />
        </div>
      </LoadingRegion>
    );
  if (schemaQuery.error)
    return (
      <EmptyState
        titleAs="h2"
        title={t('asset.schemaFailed')}
        action={
          <Button variant="secondary" onClick={() => void schemaQuery.refetch()}>
            {t('library.retry')}
          </Button>
        }
      />
    );

  const fields = schemaQuery.data.fields;
  if (fields.length === 0) return <EmptyState titleAs="h2" title={t('asset.noMetadataFields')} />;

  const missing = fields.filter((f) => f.required && isEmptyValue(draft.values[f.name])).length;
  return (
    <div className="dam-stack dam-stack--fill">
      <TranslateAssetButton asset={asset} />
      {missing > 0 && (
        <p className="dam-asset-note" role="note">
          {t('asset.requiredMissing', { count: missing })}
        </p>
      )}
      <MetadataFields
        fields={fields}
        values={draft.values}
        onChange={draft.setField}
        grouped
        touched={draft.touched}
        onTouch={draft.touch}
      />
    </div>
  );
}
