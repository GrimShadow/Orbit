import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { SegmentedControl } from '@dam/ui-kit';
import { INSIGHTS_MAX_DAYS, INSIGHTS_PRESETS, parseDays } from './useInsightsPeriod';

/** "30 days / 90 days / 365 days / Custom". Custom reveals a number of days (1-365) that is applied as soon as it is valid,
 * so there is no separate apply button to forget. */
export function InsightsPeriodPicker({ days, onChange }: { days: number; onChange: (days: number) => void }) {
  const { t } = useTranslation();
  const id = useId();
  const preset = (INSIGHTS_PRESETS as readonly number[]).includes(days);
  const [custom, setCustom] = useState(!preset);
  const [draft, setDraft] = useState(String(days));
  const customShown = custom || !preset;
  const invalid = customShown && parseDays(draft) === null;

  const pick = (v: string) => {
    if (v === 'custom') {
      setCustom(true);
      return;
    }
    setCustom(false);
    setDraft(v);
    onChange(Number(v));
  };

  return (
    <div className="dam-period">
      <SegmentedControl
        label={t('insights.period.label')}
        value={customShown ? 'custom' : String(days)}
        onChange={pick}
        options={[
          ...INSIGHTS_PRESETS.map((d) => ({ value: String(d), label: t('insights.period.days', { count: d }) })),
          { value: 'custom', label: t('insights.period.custom') },
        ]}
      />
      {customShown && (
        <div className="dam-period__custom">
          <label className="dam-sr-only" htmlFor={id}>
            {t('insights.period.customDays')}
          </label>
          <input
            id={id}
            className="dam-input dam-period__amount"
            type="number"
            inputMode="numeric"
            min={1}
            max={INSIGHTS_MAX_DAYS}
            step={1}
            value={draft}
            aria-invalid={invalid || undefined}
            aria-describedby={invalid ? `${id}-err` : undefined}
            onChange={(e) => {
              setDraft(e.target.value);
              const n = parseDays(e.target.value);
              if (n !== null) onChange(n);
            }}
          />
          <span aria-hidden="true">{t('insights.period.daysUnit')}</span>
          {invalid && (
            <span id={`${id}-err`} className="dam-period__error" role="alert">
              {t('insights.period.invalid', { max: INSIGHTS_MAX_DAYS })}
            </span>
          )}
        </div>
      )}
    </div>
  );
}
