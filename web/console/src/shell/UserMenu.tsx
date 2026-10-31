import { Bell, CaretDown, GearSix, Keyboard, SignOut, UserCircle } from '@phosphor-icons/react';
import { Avatar, Select } from '@dam/ui-kit';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { useMe } from '../api/me';
import { useAuth } from '../auth/AuthProvider';
import { canUseSettings, SETTINGS_ROOT } from '../features/settings/settingsModel';
import { LANGUAGES, setLanguage } from '../i18n';
import { ThemeSwitcher } from './ThemeSwitcher';

/** Everything about "me" in one place: who I am, tenant, language, theme, account page, shortcuts help, sign out.
 * The trigger reports `aria-expanded`; Escape closes and returns focus to it. */
export function UserMenu({ onShowShortcuts }: { onShowShortcuts: () => void }) {
  const { t, i18n } = useTranslation();
  const auth = useAuth();
  const { data: me } = useMe();
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (!wrap.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpen(false);
        button.current?.focus();
      }
    };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  if (auth.status !== 'authenticated') return null;
  const name = auth.name ?? auth.email ?? '';
  const close = () => setOpen(false);

  return (
    <div className="dam-usermenu" ref={wrap}>
      <button
        ref={button}
        type="button"
        className="dam-usermenu__trigger"
        aria-expanded={open}
        aria-controls="user-menu-panel"
        aria-label={`${t('shell.userMenu')}: ${name}`}
        onClick={() => setOpen((o) => !o)}
      >
        <Avatar name={name} size="sm" decorative />
        <span className="dam-usermenu__name">{name}</span>
        <CaretDown size={12} weight="bold" aria-hidden="true" className="dam-usermenu__caret" />
      </button>
      {open && (
        <div id="user-menu-panel" className="dam-usermenu__panel">
          <div className="dam-usermenu__who">
            <Avatar name={name} size="lg" decorative />
            <div className="dam-usermenu__identity">
              <strong>{auth.name}</strong>
              <span className="dam-hint">{auth.email}</span>
            </div>
          </div>
          <div className="dam-usermenu__section">
            <Link to="/me" className="dam-usermenu__link" onClick={close}>
              <UserCircle size={18} aria-hidden="true" />
              {t('nav.account')}
            </Link>
            <Link to="/notifications" className="dam-usermenu__link" onClick={close}>
              <Bell size={18} aria-hidden="true" />
              {t('notifications.title')}
            </Link>
            {canUseSettings(me?.permissions) && (
              <Link to={SETTINGS_ROOT} className="dam-usermenu__link" onClick={close}>
                <GearSix size={18} aria-hidden="true" />
                {t('nav.settings')}
              </Link>
            )}
            <button
              type="button"
              className="dam-usermenu__link"
              onClick={() => {
                close();
                onShowShortcuts();
              }}
            >
              <Keyboard size={18} aria-hidden="true" />
              {t('shell.shortcuts.button')}
            </button>
          </div>
          <div className="dam-usermenu__section dam-usermenu__prefs">
            {me && (
              // One tenant per token today; the select is the seam for multi-tenant users later.
              <Select
                label={t('shell.tenant')}
                value={me.tenantId}
                disabled
                onChange={() => {}}
                options={[{ value: me.tenantId, label: t('shell.defaultTenant', { id: me.tenantId.slice(0, 8) }) }]}
              />
            )}
            <Select
              label={t('shell.language')}
              value={i18n.language}
              onChange={(e) => setLanguage(e.target.value)}
              options={LANGUAGES}
            />
            <div className="dam-usermenu__theme">
              <span className="dam-label">{t('theme.label')}</span>
              <ThemeSwitcher />
            </div>
          </div>
          <div className="dam-usermenu__section">
            <button type="button" className="dam-usermenu__link dam-usermenu__link--danger" onClick={() => void auth.signOut()}>
              <SignOut size={18} aria-hidden="true" />
              {t('shell.signOut')}
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
