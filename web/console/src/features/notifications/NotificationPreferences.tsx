import { Info } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Skeleton, useToast } from '../../ui';
import { CategoryIcon } from './NotificationItem';
import { usePreferences, useUpdatePreferences, type NotificationCategory } from './notificationsApi';

type Channel = 'inApp' | 'email';
type Draft = Record<string, Partial<Record<Channel, boolean>>>;

function Switch({
  checked,
  disabled,
  label,
  name,
  onChange,
}: {
  checked: boolean;
  disabled?: boolean;
  /** The visible text next to the switch ("In-app"). */
  label: string;
  /** The full accessible name ("Approval tasks: In-app"). */
  name: string;
  onChange: (next: boolean) => void;
}) {
  return (
    <label className="dam-switch" data-disabled={disabled ? 'true' : undefined}>
      <input
        type="checkbox"
        role="switch"
        className="dam-switch__input"
        checked={checked}
        disabled={disabled}
        aria-label={name}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className="dam-switch__track" aria-hidden="true">
        <span className="dam-switch__thumb" />
      </span>
      <span className="dam-switch__label">{label}</span>
    </label>
  );
}

/** Per-category delivery choices. The category catalogue (label, description, current and default values) comes from
 * the server - nothing here names a category. Edits are held locally until Save. */
export function NotificationPreferences() {
  const { t } = useTranslation();
  const { push } = useToast();
  const prefs = usePreferences();
  const save = useUpdatePreferences();
  const [draft, setDraft] = useState<Draft>({});

  if (prefs.isPending) {
    return (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('notifications.prefs.loading')}</span>
        <div className="dam-prefs__list" aria-hidden="true">
          {[0, 1, 2, 3].map((i) => (
            <div key={i} className="dam-prefs__row">
              <Skeleton width="40%" height={16} />
              <Skeleton width="70%" height={12} />
            </div>
          ))}
        </div>
      </div>
    );
  }
  if (prefs.error || !prefs.data) {
    return (
      <div className="dam-notifs__error" role="alert">
        <span>{t('notifications.prefs.loadError')}</span>
        <Button variant="secondary" size="sm" onClick={() => void prefs.refetch()}>
          {t('notifications.retry')}
        </Button>
      </div>
    );
  }

  const { categories, emailAvailable } = prefs.data;
  const value = (c: NotificationCategory, ch: Channel): boolean => draft[c.key]?.[ch] ?? c[ch];
  const dirty = categories.some((c) => value(c, 'inApp') !== c.inApp || value(c, 'email') !== c.email);

  const set = (c: NotificationCategory, ch: Channel, next: boolean) =>
    setDraft((d) => ({ ...d, [c.key]: { ...d[c.key], [ch]: next } }));

  const onSave = () =>
    save.mutate(
      categories.map((c) => ({ category: c.key, inApp: value(c, 'inApp'), email: value(c, 'email') })),
      {
        onSuccess: () => {
          setDraft({});
          push({ title: t('notifications.prefs.saved'), tone: 'success' });
        },
        onError: () => push({ title: t('notifications.prefs.saveFailed'), tone: 'error' }),
      },
    );

  return (
    <section className="dam-prefs" aria-labelledby="notif-prefs-title">
      <div className="dam-prefs__intro">
        <h2 id="notif-prefs-title" className="dam-prefs__title">
          {t('notifications.prefs.title')}
        </h2>
        <p className="dam-prefs__hint">{t('notifications.prefs.intro')}</p>
      </div>

      {!emailAvailable && (
        <p className="dam-prefs__note" role="note">
          <Info size={18} aria-hidden="true" />
          <span>{t('notifications.prefs.emailUnavailable')}</span>
        </p>
      )}

      <ul className="dam-prefs__list" aria-label={t('notifications.prefs.listLabel')}>
        {categories.map((c) => (
          <li key={c.key} className="dam-prefs__row">
            <span className="dam-prefs__icon" aria-hidden="true">
              <CategoryIcon category={c.key} size={22} />
            </span>
            <div className="dam-prefs__text">
              <span className="dam-prefs__label">{c.label}</span>
              <span className="dam-prefs__desc">{c.description}</span>
            </div>
            <div className="dam-prefs__switches">
              <Switch
                label={t('notifications.prefs.inApp')}
                name={t('notifications.prefs.switchName', { category: c.label, channel: t('notifications.prefs.inApp') })}
                checked={value(c, 'inApp')}
                onChange={(next) => set(c, 'inApp', next)}
              />
              <Switch
                label={t('notifications.prefs.email')}
                name={t('notifications.prefs.switchName', { category: c.label, channel: t('notifications.prefs.email') })}
                checked={value(c, 'email')}
                disabled={!emailAvailable}
                onChange={(next) => set(c, 'email', next)}
              />
            </div>
          </li>
        ))}
      </ul>

      <div className="dam-prefs__actions">
        <Button variant="primary" disabled={!dirty} loading={save.isPending} onClick={onSave}>
          {t('notifications.prefs.save')}
        </Button>
        {dirty && (
          <Button variant="ghost" disabled={save.isPending} onClick={() => setDraft({})}>
            {t('notifications.prefs.discard')}
          </Button>
        )}
      </div>
    </section>
  );
}
