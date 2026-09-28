import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, LoadingRegion, Select, Skeleton } from '../../ui';
import { useSchema, useTerms, useUpdateAsset, useVocabularies, type FieldDto } from './assetApi';

/** The same five languages the search index analyzes (ADR-0011) - every multilingual field edits all of them, not just
 * the console's own current UI language, since metadata content and UI locale are independent. */
const ML_LANGUAGES = ['en', 'hi', 'mr', 'ta', 'te'];

type Json = string | number | boolean | null | Json[] | { [key: string]: Json };
type Values = Record<string, Json>;

function asString(v: Json | undefined): string {
  return typeof v === 'string' ? v : v == null ? '' : String(v);
}
function asStringArray(v: Json | undefined): string[] {
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : typeof v === 'string' && v ? [v] : [];
}
function asMlText(v: Json | undefined): Record<string, string> {
  return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, string>) : {};
}

function VocabularyField({ field, value, onChange }: { field: FieldDto; value: Json | undefined; onChange: (v: Json) => void }) {
  const { t } = useTranslation();
  const vocabsQuery = useVocabularies();
  const vocab = vocabsQuery.data?.find((v) => v.key === field.vocabulary);
  const termsQuery = useTerms(vocab?.id);
  const options = (termsQuery.data ?? []).map((term) => ({
    value: term.code,
    label: `${'—'.repeat(term.depth)} ${term.label}`.trim(),
  }));

  if (field.multi) {
    const selected = new Set(asStringArray(value));
    return (
      <fieldset className="dam-field">
        <legend className="dam-label">{field.label}</legend>
        <select
          multiple
          className="dam-select"
          value={[...selected]}
          onChange={(e) => onChange(Array.from(e.target.selectedOptions, (o) => o.value))}
        >
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      </fieldset>
    );
  }
  return (
    <Select
      label={field.label}
      value={asString(value)}
      onChange={(e) => onChange(e.target.value)}
      options={[{ value: '', label: t('asset.metadataFieldEmpty') }, ...options]}
    />
  );
}

function FieldInput({ field, value, onChange }: { field: FieldDto; value: Json | undefined; onChange: (v: Json) => void }) {
  switch (field.type) {
    case 'ml_text': {
      const current = asMlText(value);
      return (
        <fieldset className="dam-field">
          <legend className="dam-label">{field.label}</legend>
          <div className="dam-upload__form">
            {ML_LANGUAGES.map((lang) => (
              <Input
                key={lang}
                label={lang}
                value={current[lang] ?? ''}
                onChange={(e) => onChange({ ...current, [lang]: e.target.value })}
              />
            ))}
          </div>
        </fieldset>
      );
    }
    case 'number':
      return (
        <Input
          type="number"
          label={field.label}
          value={typeof value === 'number' ? value : ''}
          min={field.min ?? undefined}
          max={field.max ?? undefined}
          step={field.wholeNumbers ? 1 : 'any'}
          onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))}
        />
      );
    case 'date':
      return <Input type="date" label={field.label} value={asString(value)} onChange={(e) => onChange(e.target.value)} />;
    case 'boolean':
      return (
        <label className="dam-field">
          <input type="checkbox" checked={value === true} onChange={(e) => onChange(e.target.checked)} /> {field.label}
        </label>
      );
    case 'enum':
      return (
        <Select
          label={field.label}
          value={asString(value)}
          onChange={(e) => onChange(e.target.value)}
          options={(field.options ?? []).map((o) => ({ value: o, label: o }))}
        />
      );
    case 'vocabulary':
    case 'term_tree':
      return <VocabularyField field={field} value={value} onChange={onChange} />;
    default:
      return (
        <Input
          label={field.label}
          value={asString(value)}
          maxLength={field.maxLength ?? undefined}
          onChange={(e) => onChange(e.target.value)}
        />
      );
  }
}

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
      {schemaQuery.data.fields.map((field) => (
        <div key={field.name}>
          <FieldInput field={field} value={values[field.name]} onChange={(v) => setField(field.name, v)} />
          {field.required && <span className="dam-hint">{t('asset.required')}</span>}
        </div>
      ))}
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
