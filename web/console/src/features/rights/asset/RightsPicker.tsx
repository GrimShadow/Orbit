import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Input, Skeleton } from '../../../ui';
import { useRightsList, type Rights } from '../rightsApi';
import { RightsStatusChip } from './RightsStatusChip';
import { licenceLabel, validityText } from './rightsFormat';
import './rightsAsset.css';

interface RightsPickerProps {
  value: string | null;
  onChange: (record: Rights) => void;
  /** The record the asset has now, marked "Current". */
  currentId?: string | null;
  /** Why a record cannot be chosen (shown under it, and the radio is disabled). */
  disabledReason?: (r: Rights) => string | undefined;
}

/** A searchable list of the tenant's rights records as radio rows (so the arrow keys, Tab and screen readers behave natively),
 * each with its status chip - an expired record is plainly expired before anyone picks it. Needs `rights.manage`. */
export function RightsPicker({ value, onChange, currentId, disabledReason }: RightsPickerProps) {
  const { t, i18n } = useTranslation();
  const list = useRightsList();
  const [q, setQ] = useState('');
  const name = useId();
  const needle = q.trim().toLowerCase();
  const items = (list.data ?? []).filter(
    (r) =>
      !needle ||
      [r.name, r.licensor, r.licenceType, licenceLabel(t, r.licenceType)].some((s) => s.toLowerCase().includes(needle)),
  );

  return (
    <div className="dam-rights-picker">
      <Input
        label={t('rightsAsset.picker.search')}
        type="search"
        value={q}
        onChange={(e) => setQ(e.target.value)}
        placeholder={t('rightsAsset.picker.searchPlaceholder')}
        autoComplete="off"
      />
      {list.isPending ? (
        <div role="status" aria-label={t('rightsAsset.picker.loading')} className="dam-rights-picker__loading">
          <Skeleton height={48} />
          <Skeleton height={48} />
        </div>
      ) : list.error ? (
        <div role="alert" className="dam-rights-picker__empty">
          <p>{t('rightsAsset.picker.failed')}</p>
          <Button size="sm" onClick={() => void list.refetch()}>
            {t('rightsAsset.retry')}
          </Button>
        </div>
      ) : items.length === 0 ? (
        <p className="dam-rights-picker__empty" role="status">
          {needle ? t('rightsAsset.picker.noMatches', { q: q.trim() }) : t('rightsAsset.picker.none')}
        </p>
      ) : (
        <div role="radiogroup" aria-label={t('rightsAsset.picker.list')} className="dam-rights-picker__list">
          {items.map((r) => {
            const reason = disabledReason?.(r);
            const hintId = `${name}-${r.id}-hint`;
            return (
              <label key={r.id} className="dam-rights-opt" data-disabled={reason ? '' : undefined}>
                <input
                  type="radio"
                  name={name}
                  value={r.id}
                  checked={value === r.id}
                  disabled={!!reason}
                  aria-describedby={reason ? hintId : undefined}
                  onChange={() => onChange(r)}
                />
                <span className="dam-rights-opt__body">
                  <span className="dam-rights-opt__name">{r.name}</span>
                  <span className="dam-rights-opt__meta">
                    {licenceLabel(t, r.licenceType)} · {validityText(t, i18n.language, r)}
                  </span>
                  {reason && (
                    <span id={hintId} className="dam-rights-opt__reason">
                      {reason}
                    </span>
                  )}
                </span>
                <span className="dam-rights-opt__chips">
                  {r.id === currentId && <Badge size="sm">{t('rightsAsset.picker.current')}</Badge>}
                  <RightsStatusChip rights={r} size="sm" />
                </span>
              </label>
            );
          })}
        </div>
      )}
    </div>
  );
}
