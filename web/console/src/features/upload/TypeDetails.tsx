import { useTranslation } from 'react-i18next';
import { MetadataFields, type Json, type Values } from '../asset/MetadataFields';
import { useSchema } from '../asset/assetApi';
import { CaretDown, FileTypeIcon, Skeleton } from '../../ui';
import { compactMetadata } from './uploadQueue';

interface TypeDetailsProps {
  type: string;
  count: number;
  values: Values;
  onChange: (name: string, value: Json) => void;
}

/** The metadata form for the files of one asset type, generated from that type's current schema (the same fields the
 * asset page edits). Every field is optional at upload - a draft may stay incomplete, and required fields are
 * enforced when the asset is submitted for review - so this only offers a head start. */
export function TypeDetails({ type, count, values, onChange }: TypeDetailsProps) {
  const { t } = useTranslation();
  const schemaQuery = useSchema(type);

  if (schemaQuery.isPending) return <Skeleton height={48} />;
  if (schemaQuery.error || schemaQuery.data.fields.length === 0) return null; // nothing to offer for this type
  const hasRequired = schemaQuery.data.fields.some((f) => f.required);
  const filled = Object.keys(compactMetadata(values) ?? {}).length;

  return (
    <details className="dam-upload__details">
      <summary>
        <FileTypeIcon type={type} size={18} weight="duotone" aria-hidden="true" />
        <span className="dam-upload__details-title">
          <span>{t('upload.details.title', { type: t(`library.type.${type}`) })}</span>
          <span className="dam-hint">
            {t('upload.details.files', { count })}
            {filled > 0 && ` · ${t('upload.details.filled', { count: filled })}`}
          </span>
        </span>
        <CaretDown size={14} weight="bold" aria-hidden="true" className="dam-upload__details-caret" />
      </summary>
      <div className="dam-stack dam-stack--fill dam-upload__details-body">
        {hasRequired && <p className="dam-hint">{t('upload.details.requiredLater')}</p>}
        <MetadataFields fields={schemaQuery.data.fields} values={values} onChange={onChange} />
      </div>
    </details>
  );
}
