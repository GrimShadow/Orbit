import { useId, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Card, CaretDown, CheckboxGroup, Input, SegmentedControl, Skeleton, TagInput, WarningCircle } from '../../ui';
import { useTerms, useVocabularies, type FieldDto } from './assetApi';

/** The same five languages the search index analyzes (ADR-0011) - every multilingual field edits all of them, not just
 * the console's own current UI language, since metadata content and UI locale are independent. */
const ML_LANGUAGES = ['en', 'hi', 'mr', 'ta', 'te'];
/** Lists longer than this scroll inside the form instead of pushing everything below them off-screen. */
const LONG_LIST = 8;

export type Json = string | number | boolean | null | Json[] | { [key: string]: Json };
export type Values = Record<string, Json>;

function asString(v: Json | undefined): string {
  return typeof v === 'string' ? v : v == null ? '' : String(v);
}
function asStringArray(v: Json | undefined): string[] {
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : typeof v === 'string' && v ? [v] : [];
}
function asMlText(v: Json | undefined): Record<string, string> {
  return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, string>) : {};
}

/** True when a value carries nothing a person entered (used for required-field markers). */
export function isEmptyValue(v: Json | undefined): boolean {
  if (v === null || v === undefined || v === '') return true;
  if (Array.isArray(v)) return v.length === 0;
  if (typeof v === 'object') return Object.values(v).every((x) => isEmptyValue(x));
  return false;
}

interface Shared {
  /** Fires when a control loses focus or changes, so the form knows which required fields the user has been at. */
  onTouch?: () => void;
  /** Set once the user has interacted with a required field that is still empty. */
  error?: string;
}

/** Label + control + hint/error, with the required marker. The control gets `id`, `aria-describedby` from `ids`. */
function Labeled({
  id,
  label,
  required,
  hint,
  error,
  children,
}: {
  id: string;
  label: ReactNode;
  required?: boolean;
  hint?: string;
  error?: string;
  children: ReactNode;
}) {
  return (
    <div className="dam-field">
      <label className="dam-label" htmlFor={id}>
        {label}
        {required && (
          <span className="dam-required" aria-hidden="true">
            {' '}
            *
          </span>
        )}
      </label>
      {children}
      {hint && (
        <span id={`${id}-hint`} className="dam-hint">
          {hint}
        </span>
      )}
      {error && (
        <span id={`${id}-error`} className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </span>
      )}
    </div>
  );
}

const describedBy = (id: string, hint?: string, error?: string) =>
  [hint && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(' ') || undefined;

const withMarker = (field: FieldDto) => (field.required ? `${field.label} *` : field.label);

function SelectControl({
  id,
  value,
  options,
  onChange,
  onBlur,
  required,
  error,
  hint,
}: {
  id: string;
  value: string;
  options: { value: string; label: string }[];
  onChange: (v: string) => void;
  onBlur?: () => void;
  required?: boolean;
  error?: string;
  hint?: string;
}) {
  return (
    <span className="dam-select-wrap">
      <select
        id={id}
        className="dam-select"
        value={value}
        aria-required={required || undefined}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(id, hint, error)}
        onChange={(e) => onChange(e.target.value)}
        onBlur={onBlur}
      >
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
      <CaretDown size={14} weight="bold" aria-hidden="true" className="dam-select__chevron" />
    </span>
  );
}

function VocabularyField({
  field,
  value,
  onChange,
  onTouch,
  error,
}: { field: FieldDto; value: Json | undefined; onChange: (v: Json) => void } & Shared) {
  const { t } = useTranslation();
  const id = useId();
  const vocabsQuery = useVocabularies();
  const vocab = vocabsQuery.data?.find((v) => v.key === field.vocabulary);
  const termsQuery = useTerms(vocab?.id);
  const terms = termsQuery.data ?? [];

  const loading = vocabsQuery.isPending || (!!vocab && termsQuery.isPending);
  if (loading)
    return (
      <div className="dam-field" role="status" aria-label={t('asset.optionsLoading', { field: field.label })}>
        <span className="dam-label">{withMarker(field)}</span>
        <Skeleton height={32} />
        <Skeleton height={32} />
      </div>
    );
  if (vocabsQuery.error || termsQuery.error || !vocab)
    return (
      <div className="dam-field">
        <span className="dam-label">{withMarker(field)}</span>
        <span className="dam-hint">{t('asset.optionsFailed')}</span>
      </div>
    );

  if (field.multi) {
    const selected = asStringArray(value);
    return (
      <div className={`dam-metafield${terms.length > LONG_LIST ? ' dam-metafield--long' : ''}`}>
        <CheckboxGroup
          label={withMarker(field)}
          // A term tree is indented by depth with em spaces (they are preserved, unlike ordinary leading whitespace).
          options={terms.map((term) => ({ value: term.code, label: `${' '.repeat(term.depth)}${term.label}` }))}
          value={selected}
          onChange={(next) => {
            onTouch?.();
            onChange(next);
          }}
          hint={selected.length > 0 ? t('asset.selectedCount', { count: selected.length }) : undefined}
        />
        {error && (
          <span className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {error}
          </span>
        )}
      </div>
    );
  }
  return (
    <Labeled id={id} label={field.label} required={field.required} error={error}>
      <SelectControl
        id={id}
        value={asString(value)}
        required={field.required}
        error={error}
        onBlur={onTouch}
        onChange={(v) => {
          onTouch?.();
          onChange(v);
        }}
        options={[
          { value: '', label: t('asset.metadataFieldEmpty') },
          ...terms.map((term) => ({ value: term.code, label: `${'—'.repeat(term.depth)} ${term.label}`.trim() })),
        ]}
      />
    </Labeled>
  );
}

/** Five languages in one compact block: a language switch and the one input being edited, instead of five stacked inputs. */
function MlTextField({
  field,
  value,
  onChange,
  onTouch,
  error,
}: { field: FieldDto; value: Json | undefined; onChange: (v: Json) => void } & Shared) {
  const { t, i18n } = useTranslation();
  const id = useId();
  const current = asMlText(value);
  const [lang, setLang] = useState(ML_LANGUAGES[0]!);
  const names = useMemo(() => {
    try {
      return new Intl.DisplayNames([i18n.language], { type: 'language' });
    } catch {
      return null;
    }
  }, [i18n.language]);
  const filled = ML_LANGUAGES.filter((l) => (current[l] ?? '').trim() !== '').length;
  const langName = names?.of(lang) ?? lang;

  return (
    <div className="dam-field dam-ml">
      <div className="dam-ml__head">
        <span className="dam-label">{withMarker(field)}</span>
        <span className="dam-hint">{t('asset.ml.filled', { filled, total: ML_LANGUAGES.length })}</span>
      </div>
      <SegmentedControl
        label={t('asset.ml.language', { field: field.label })}
        value={lang}
        onChange={setLang}
        options={ML_LANGUAGES.map((l) => ({
          value: l,
          label: l.toUpperCase(),
          icon: (current[l] ?? '').trim() ? <span className="dam-ml__dot" aria-hidden="true" /> : undefined,
        }))}
      />
      <label className="dam-sr-only" htmlFor={id}>
        {`${field.label} - ${langName}`}
      </label>
      <textarea
        id={id}
        key={lang}
        lang={lang}
        rows={3}
        className="dam-input dam-metafield__textarea"
        placeholder={langName}
        value={current[lang] ?? ''}
        aria-required={field.required || undefined}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-error` : undefined}
        onBlur={onTouch}
        onChange={(e) => onChange({ ...current, [lang]: e.target.value })}
      />
      {error && (
        <span id={`${id}-error`} className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </span>
      )}
    </div>
  );
}

function FieldInput({
  field,
  value,
  onChange,
  onTouch,
  error,
}: { field: FieldDto; value: Json | undefined; onChange: (v: Json) => void } & Shared) {
  const { t } = useTranslation();
  const id = useId();
  switch (field.type) {
    case 'ml_text':
      return <MlTextField field={field} value={value} onChange={onChange} onTouch={onTouch} error={error} />;
    case 'number':
      return (
        <Input
          type="number"
          label={withMarker(field)}
          value={typeof value === 'number' ? value : ''}
          min={field.min ?? undefined}
          max={field.max ?? undefined}
          step={field.wholeNumbers ? 1 : 'any'}
          aria-required={field.required || undefined}
          error={error}
          onBlur={onTouch}
          onChange={(e) => onChange(e.target.value === '' ? null : Number(e.target.value))}
        />
      );
    case 'date':
      return (
        <Input
          type="date"
          label={withMarker(field)}
          value={asString(value)}
          aria-required={field.required || undefined}
          error={error}
          onBlur={onTouch}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    case 'boolean':
      return (
        <label className="dam-check dam-metafield__bool">
          <input
            type="checkbox"
            className="dam-check__input"
            checked={value === true}
            onChange={(e) => {
              onTouch?.();
              onChange(e.target.checked);
            }}
          />
          <span className="dam-check__label">{field.label}</span>
        </label>
      );
    case 'enum':
      return (
        <Labeled id={id} label={field.label} required={field.required} error={error}>
          <SelectControl
            id={id}
            value={asString(value)}
            required={field.required}
            error={error}
            onBlur={onTouch}
            onChange={(v) => {
              onTouch?.();
              onChange(v);
            }}
            options={[
              { value: '', label: t('asset.metadataFieldEmpty') },
              ...(field.options ?? []).map((o) => ({ value: o, label: o })),
            ]}
          />
        </Labeled>
      );
    case 'vocabulary':
    case 'term_tree':
      return <VocabularyField field={field} value={value} onChange={onChange} onTouch={onTouch} error={error} />;
    default:
      if (field.multi)
        return (
          <TagInput
            label={withMarker(field)}
            value={asStringArray(value)}
            onChange={(next) => {
              onTouch?.();
              onChange(next);
            }}
            hint={t('asset.tagsHint')}
            removeLabel={(tag) => t('asset.removeTag', { tag })}
            listLabel={t('asset.tagList', { field: field.label })}
          />
        );
      return (
        <Input
          label={withMarker(field)}
          value={asString(value)}
          maxLength={field.maxLength ?? undefined}
          aria-required={field.required || undefined}
          error={error}
          onBlur={onTouch}
          onChange={(e) => onChange(e.target.value)}
        />
      );
  }
}

type SectionId = 'content' | 'properties' | 'classification' | 'availability';
const SECTION_ORDER: SectionId[] = ['content', 'properties', 'classification', 'availability'];
/** The one access dimension the policy engine calls "channel": where an asset is made available (see the item actions). */
const CHANNEL_DIMENSION = 'channel';

function sectionOf(field: FieldDto): SectionId {
  switch (field.type) {
    case 'vocabulary':
    case 'term_tree':
      return field.accessDimension === CHANNEL_DIMENSION ? 'availability' : 'classification';
    case 'number':
    case 'date':
    case 'boolean':
    case 'enum':
      return 'properties';
    case 'text':
      return field.multi ? 'classification' : 'content';
    default:
      return 'content';
  }
}

interface MetadataFieldsProps {
  fields: FieldDto[];
  values: Values;
  onChange: (name: string, value: Json) => void;
  /** Asset detail: fields grouped into titled cards. Upload: a plain stack inside the caller's own card. */
  grouped?: boolean;
  /** Names of fields the user has touched; a required one that is empty and touched shows an error. */
  touched?: ReadonlySet<string>;
  onTouch?: (name: string) => void;
}

/** The schema-generated inputs for one asset type's fields - shared by the asset detail form (edit) and the upload page
 * (set before upload). Purely presentational: the caller owns the values and decides what saving means. */
export function MetadataFields({ fields, values, onChange, grouped = false, touched, onTouch }: MetadataFieldsProps) {
  const { t } = useTranslation();
  const base = useId();

  const render = (field: FieldDto) => {
    const error =
      touched?.has(field.name) && field.required && isEmptyValue(values[field.name])
        ? t('asset.requiredError', { field: field.label })
        : undefined;
    return (
      <FieldInput
        key={field.name}
        field={field}
        value={values[field.name]}
        onChange={(v) => {
          onTouch?.(field.name);
          onChange(field.name, v);
        }}
        onTouch={onTouch ? () => onTouch(field.name) : undefined}
        error={error}
      />
    );
  };

  if (!grouped) return <>{fields.map(render)}</>;

  return (
    <>
      {SECTION_ORDER.map((section) => {
        const inSection = fields.filter((f) => sectionOf(f) === section);
        if (inSection.length === 0) return null;
        const headingId = `${base}-${section}`;
        return (
          <Card key={section} as="section" aria-labelledby={headingId} className="dam-metasection">
            <h2 id={headingId} className="dam-metasection__title">
              {t(`asset.sections.${section}`)}
            </h2>
            <div className="dam-metasection__body">{inSection.map(render)}</div>
          </Card>
        );
      })}
    </>
  );
}
