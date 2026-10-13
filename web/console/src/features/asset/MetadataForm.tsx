import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, LoadingRegion, Skeleton } from '../../ui';
import { useSchema, useUpdateAsset } from './assetApi';
import { MetadataFields, type Json, type Values } from './MetadataFields';

interface MetadataFormProps {
  assetId: string;
  assetType: string;
  metadata: Record<string, unknown>;
}

/** A form generated from the asset type's current schema (spec Step 1.8: "metadata form generated from schema").
 * Fields the schema no longer knows about (an old asset validated against an earlier version) are preserved untouched
 * in the saved payload - `UpdateAsset` replaces the whole document, so only rendered fields are ever changed here. */
export function MetadataForm({ assetId, assetType, metadata }: MetadataFormProps) {
  const { t } = useTranslation();
  const schemaQuery = useSchema(assetType);
  const update = useUpdateAsset(assetId);
  const [values, setValues] = useState<Values>(metadata as Values);
  const [dirty, setDirty] = useState(false);

  const setField = (name: string, value: Json) => {
    setValues((v) => ({ ...v, [name]: value }));
    setDirty(true);
  };

  if (schemaQuery.isPending)
    return (
      <LoadingRegion>
        <Skeleton height={200} />
      </LoadingRegion>
    );
  if (schemaQuery.error) return <p className="dam-hint">{t('asset.schemaFailed')}</p>;

  return (
    <form
      className="dam-stack"
      onSubmit={(e) => {
        e.preventDefault();
        update.mutate({ title: null, tags: null, metadata: values }, { onSuccess: () => setDirty(false) });
      }}
    >
      <MetadataFields fields={schemaQuery.data.fields} values={values} onChange={setField} />
      <Button type="submit" variant="primary" loading={update.isPending} disabled={!dirty}>
        {t('asset.saveMetadata')}
      </Button>
      {update.isError && (
        <p role="alert" className="dam-hint">
          {update.error.message}
        </p>
      )}
    </form>
  );
}
