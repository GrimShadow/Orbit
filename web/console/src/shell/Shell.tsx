import { Select } from '@dam/ui-kit';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { NavLink, Outlet } from 'react-router-dom';
import { useMe } from '../api/me';
import { useAuth } from '../auth/AuthProvider';
import { LANGUAGES, setLanguage } from '../i18n';
import './shell.css';

function TenantSwitcher() {
  const { t } = useTranslation();
  const { data } = useMe();
  if (!data) return null;
  // One tenant per token today; the select is the seam for multi-tenant users later.
  const id = data.tenantId;
  return (
    <Select
      label={t('shell.tenant')}
      value={id}
      disabled={true}
      onChange={() => {}}
      options={[{ value: id, label: t('shell.defaultTenant', { id: id.slice(0, 8) }) }]}
    />
  );
}

function LanguageSwitcher() {
  const { t, i18n } = useTranslation();
  return (
    <Select label={t('shell.language')} value={i18n.language} onChange={(e) => setLanguage(e.target.value)} options={LANGUAGES} />
  );
}

function UserMenu() {
  const { t } = useTranslation();
  const auth = useAuth();
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
  return (
    <div className="dam-usermenu" ref={wrap}>
      <button
        ref={button}
        type="button"
        className="dam-btn dam-btn--ghost"
        aria-expanded={open}
        aria-controls="user-menu-panel"
        aria-label={`${t('shell.userMenu')}: ${auth.name ?? auth.email ?? ''}`}
        onClick={() => setOpen((o) => !o)}
      >
        <span aria-hidden="true" className="dam-avatar">
          {(auth.name ?? auth.email ?? '?').slice(0, 1).toUpperCase()}
        </span>
        <span className="dam-usermenu__name">{auth.name ?? auth.email}</span>
      </button>
      {open && (
        <div id="user-menu-panel" className="dam-usermenu__panel">
          <div className="dam-usermenu__who">
            <strong>{auth.name}</strong>
            <br />
            <span className="dam-hint">{auth.email}</span>
          </div>
          <button type="button" className="dam-btn dam-btn--secondary" onClick={() => void auth.signOut()}>
            {t('shell.signOut')}
          </button>
        </div>
      )}
    </div>
  );
}

export function Shell() {
  const { t } = useTranslation();
  const auth = useAuth();
  return (
    <div className="dam-root dam-shell">
      <a className="dam-skip" href="#main">
        {t('app.skip')}
      </a>
      <header className="dam-header">
        <span className="dam-brand">{t('app.name')}</span>
        {auth.status === 'authenticated' && (
          <nav aria-label={t('nav.primary')} className="dam-nav">
            <NavLink to="/" end>
              {t('nav.home')}
            </NavLink>
            <NavLink to="/me">{t('nav.account')}</NavLink>
          </nav>
        )}
        <div className="dam-header__tools">
          {auth.status === 'authenticated' && <TenantSwitcher />}
          <LanguageSwitcher />
          <UserMenu />
        </div>
      </header>
      <main id="main" className="dam-main" tabIndex={-1}>
        <Outlet />
      </main>
    </div>
  );
}
