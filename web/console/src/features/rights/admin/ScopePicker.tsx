import { useTranslation } from 'react-i18next';
import { CheckboxGroup, Skeleton, TagInput, WarningCircle } from '../../../ui';
import { slugify } from './rightsLabels';
import { useScopeOptions, type ScopeDimension } from './useScopeOptions';

interface ScopePickerProps {
  dimension: ScopeDimension;
  value: string[];
  onChange: (next: string[]) => void;
  error?: string;
}

/** Channels or regions a licence covers. The tenant's own terms when it has them, else free entry of lower-case codes.
 * Nothing chosen means "every channel" / "worldwide" - that is what an empty list means to the server. */
export function ScopePicker({ dimension, value, onChange, error }: ScopePickerProps) {
  const { t } = useTranslation();
  const { loading, options } = useScopeOptions(dimension);
  const label = t(`rights.scope.${dimension}.label`);
  const hint = t(`rights.scope.${dimension}.hint`);

  let body;
  if (loading) {
    body = (
      <div role="status" aria-live="polite" className="dam-stack dam-stack--fill">
        <span className="dam-sr-only">{t('rights.scope.loading', { what: label })}</span>
        <Skeleton height={20} width="60%" />
        <Skeleton height={20} width="45%" />
      </div>
    );
  } else if (options) {
    // A value the vocabulary no longer lists (a retired term) stays visible so it can be unticked.
    const known = new Set(options.map((o) => o.value));
    const extra = value.filter((v) => !known.has(v)).map((v) => ({ value: v, label: t('rights.scope.retired', { code: v }) }));
    body = (
      <CheckboxGroup label={label} hint={hint} columns={2} options={[...options, ...extra]} value={value} onChange={onChange} />
    );
  } else {
    body = (
      <TagInput
        label={label}
        hint={`${hint} ${t(`rights.scope.${dimension}.freeEntry`)}`}
        placeholder={t(`rights.scope.${dimension}.placeholder`)}
        value={value}
        onChange={(next) => onChange([...new Set(next.map(slugify).filter(Boolean))])}
        removeLabel={(tag) => t('rights.scope.remove', { tag })}
        listLabel={t('rights.scope.selected', { what: label })}
      />
    );
  }

  return (
    <div className="dam-rscope">
      {body}
      {error && (
        <span className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </span>
      )}
    </div>
  );
}
