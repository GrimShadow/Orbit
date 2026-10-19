import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SegmentedControl } from '@dam/ui-kit';
import { MAX_DAYS, PRESETS, periodDays, type CustomUnit, type Period } from './period';

const UNITS: CustomUnit[] = ['days', 'weeks', 'months', 'years'];

/** "30 days / 6 months / 1 year / Custom". Custom reveals an amount and a unit; the window is applied as soon as the
 * amount is a valid number, with no separate apply button to forget. */
export function PeriodPicker({ value, onChange }: { value: Period; onChange: (p: Period) => void }) {
  const { t } = useTranslation();
  const [draft, setDraft] = useState(() => (value.kind === 'custom' ? String(value.amount) : '90'));
  const unit: CustomUnit = value.kind === 'custom' ? value.unit : 'days';
  const amount = Number(draft);
  const invalid = value.kind === 'custom' && (!Number.isInteger(amount) || amount < 1 || amount * unitDays(unit) > MAX_DAYS);

  const pick = (v: string) => {
    if (v === 'custom') onChange({ kind: 'custom', amount: Number(draft) >= 1 ? Math.round(Number(draft)) : 90, unit });
    else onChange({ kind: v as (typeof PRESETS)[number] });
  };

  return (
    <div className="dam-period">
      <SegmentedControl
        label={t('analytics.period.label')}
        value={value.kind}
        onChange={pick}
        options={[
          ...PRESETS.map((p) => ({ value: p, label: t(`analytics.period.${p}`) })),
          { value: 'custom', label: t('analytics.period.custom') },
        ]}
      />
      {value.kind === 'custom' && (
        <div className="dam-period__custom" role="group" aria-label={t('analytics.period.customLabel')}>
          <input
            className="dam-input dam-period__amount"
            type="number"
            inputMode="numeric"
            min={1}
            step={1}
            value={draft}
            aria-label={t('analytics.period.amount')}
            aria-invalid={invalid || undefined}
            onChange={(e) => {
              setDraft(e.target.value);
              const n = Number(e.target.value);
              if (Number.isInteger(n) && n >= 1 && n * unitDays(unit) <= MAX_DAYS) onChange({ kind: 'custom', amount: n, unit });
            }}
          />
          <select
            className="dam-select dam-period__unit"
            value={unit}
            aria-label={t('analytics.period.unit')}
            onChange={(e) => {
              const u = e.target.value as CustomUnit;
              if (Number.isInteger(amount) && amount >= 1 && amount * unitDays(u) <= MAX_DAYS)
                onChange({ kind: 'custom', amount, unit: u });
            }}
          >
            {UNITS.map((u) => (
              <option key={u} value={u}>
                {t(`analytics.period.unit_${u}`)}
              </option>
            ))}
          </select>
          {invalid && (
            <span className="dam-period__error" role="alert">
              {t('analytics.period.invalid', { max: Math.floor(MAX_DAYS / 365) })}
            </span>
          )}
        </div>
      )}
    </div>
  );
}

function unitDays(u: CustomUnit): number {
  return periodDays({ kind: 'custom', amount: 1, unit: u });
}

/** A spoken/readable form of the window, e.g. "6 months" or "90 days". */
export function usePeriodLabel() {
  const { t } = useTranslation();
  return (p: Period) =>
    p.kind === 'custom' ? t(`analytics.period.count_${p.unit}`, { count: p.amount }) : t(`analytics.period.${p.kind}`);
}
