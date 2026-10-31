import { Lock } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Navigate, NavLink, Outlet, useLocation } from 'react-router-dom';
import { useMe } from '../../api/me';
import { Button, EmptyState, PageHeader, Skeleton, WarningCircle } from '../../ui';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { firstSettingsPath, sectionForPath, settingsNavigation, settingsSections } from './settingsModel';
import { useSettingsEyebrow } from './useSettingsEyebrow';
import './settings.css';

/** A Settings state that is not a section's own page (nothing to manage, no access to this section): the area title and a
 * calm message. */
function SettingsNotice({ title, message, hint, eyebrow }: { title: string; message: string; hint: string; eyebrow?: string }) {
  useDocumentTitle(title);
  return (
    <div className="dam-page">
      <PageHeader title={title} eyebrow={eyebrow} />
      <EmptyState icon={<Lock size={28} />} title={message} description={hint} />
    </div>
  );
}

function SettingsNav({ permissions }: { permissions: readonly string[] }) {
  const { t } = useTranslation();
  return (
    <nav className="dam-settings__nav" aria-label={t('settings.navLabel')}>
      <p className="dam-settings__navtitle">{t('settings.title')}</p>
      {settingsNavigation(permissions).map(({ group, sections }) => (
        <div key={group.id} className="dam-settings__group" role="group" aria-labelledby={`settings-group-${group.id}`}>
          <p id={`settings-group-${group.id}`} className="dam-settings__grouplabel">
            {t(group.labelKey)}
          </p>
          <ul className="dam-settings__list">
            {sections.map(({ id, to, labelKey, Icon }) => (
              <li key={id}>
                <NavLink to={to} className="dam-settings__link">
                  <Icon size={20} weight="regular" aria-hidden="true" className="dam-settings__icon" />
                  <span>{t(labelKey)}</span>
                </NavLink>
              </li>
            ))}
          </ul>
        </div>
      ))}
    </nav>
  );
}

/** `/settings`: the administrative area, for people who hold at least one section's permission (ADR-0042). A section list
 * (a vertical rail on wide screens, a scrolling row on narrow ones) beside the section's own page. `/settings` itself
 * goes to the first section the person may use; a section URL without its permission shows a forbidden state. */
export function SettingsLayout() {
  const { t } = useTranslation();
  const { pathname } = useLocation();
  const me = useMe();
  const eyebrow = useSettingsEyebrow();

  if (me.isPending)
    return (
      <div role="status" aria-live="polite" className="dam-page dam-stack dam-stack--fill">
        <span className="dam-sr-only">{t('settings.loading')}</span>
        <Skeleton height={36} width="40%" />
        <Skeleton height={140} />
      </div>
    );
  if (me.isError)
    return (
      <div role="alert" className="dam-page">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('settings.loadFailed')}
          description={t('settings.loadFailedHint')}
          action={<Button onClick={() => void me.refetch()}>{t('settings.retry')}</Button>}
        />
      </div>
    );

  const permissions = me.data.permissions;
  const allowed = settingsSections(permissions);
  if (allowed.length === 0)
    return <SettingsNotice title={t('settings.title')} message={t('settings.none.title')} hint={t('settings.none.hint')} />;

  const section = sectionForPath(pathname);
  if (pathname.replace(/\/+$/, '') === '/settings') return <Navigate to={firstSettingsPath(permissions)!} replace />;
  const focus = section?.focus?.(pathname) ?? false;
  const permitted = !section || permissions.includes(section.permission);

  return (
    <div className={`dam-settings${focus ? ' dam-settings--focus' : ''}`}>
      {!focus && <SettingsNav permissions={permissions} />}
      <div className="dam-settings__content">
        {permitted ? (
          <Outlet />
        ) : (
          <SettingsNotice
            title={t(section.labelKey)}
            eyebrow={eyebrow}
            message={t('settings.forbidden.title')}
            hint={t('settings.forbidden.hint')}
          />
        )}
      </div>
    </div>
  );
}
