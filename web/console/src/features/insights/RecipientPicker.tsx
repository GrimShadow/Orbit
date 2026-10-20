import { useTranslation } from 'react-i18next';
import { X } from '@dam/ui-kit';
import { PeopleCombobox } from '../workflow/designer/AssigneePicker';
import { useUserNames, useUserDirectory } from '../workflow/designer/designerApi';
import '../workflow/designer/designer.css';

/** Report recipients: people of this organisation, found by name or email, shown as removable chips. Ids are what the API
 * stores, so an address typed by hand is never accepted - the report cannot be mailed outside the organisation. */
export function RecipientPicker({
  value,
  onChange,
  error,
}: {
  value: string[];
  onChange: (ids: string[]) => void;
  error?: string;
}) {
  const { t } = useTranslation();
  const names = useUserNames(value);
  const directory = useUserDirectory();
  return (
    <fieldset className="dam-wfpicker dam-insrecip">
      <legend className="dam-label">{t('insights.schedules.recipients')}</legend>
      <PeopleCombobox
        chosen={value}
        label={t('insights.schedules.addRecipient')}
        placeholder={t('insights.schedules.recipientPlaceholder')}
        hint={t('insights.schedules.recipientsHint')}
        onPick={(u) => {
          names.remember(u);
          onChange([...value, u.id]);
        }}
      />
      {directory.isError && (
        <p className="dam-hint" role="status">
          {t('insights.schedules.directoryFailed')}
        </p>
      )}
      {value.length > 0 && (
        <ul className="dam-wfchips" aria-label={t('insights.schedules.selectedRecipients')}>
          {value.map((id) => (
            <li key={id} className="dam-chip">
              <span className="dam-chip__text">{names.nameOf(id)}</span>
              <button
                type="button"
                className="dam-chip__remove"
                aria-label={t('insights.schedules.removeRecipient', { name: names.nameOf(id) })}
                onClick={() => onChange(value.filter((x) => x !== id))}
              >
                <X size={12} weight="bold" aria-hidden="true" />
              </button>
            </li>
          ))}
        </ul>
      )}
      {error && (
        <span className="dam-error" role="alert">
          {error}
        </span>
      )}
    </fieldset>
  );
}
