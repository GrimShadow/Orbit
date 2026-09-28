import { Button, Modal, Select } from '@dam/ui-kit';
import { Fragment, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useMe } from '../api/me';
import { useAuth } from '../auth/AuthProvider';
import { LANGUAGES, setLanguage } from '../i18n';
import { HELP_KEY, isHelpShortcut, isShortcutTarget, matchNavShortcut, NAV_SHORTCUTS } from './keyboardShortcuts';
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

/** Single-key navigation shortcuts (spec Step 1.8: "keyboard shortcuts") plus a `?`-triggered help dialog listing
 * them - active only once signed in, and never while focus is on a form control (`isShortcutTarget`, so shortcuts
 * can never hijack ordinary typing elsewhere in the console). */
function KeyboardShortcuts() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [helpOpen, setHelpOpen] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!isShortcutTarget(e.target)) return;
      if (isHelpShortcut(e)) {
        e.preventDefault();
        setHelpOpen((o) => !o);
        return;
      }
      const path = matchNavShortcut(e);
      if (path) navigate(path);
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [navigate]);

  return (
    <>
      <Button variant="ghost" size="sm" onClick={() => setHelpOpen(true)} aria-label={t('shell.shortcuts.button')}>
        {HELP_KEY}
      </Button>
      <Modal open={helpOpen} title={t('shell.shortcuts.title')} onClose={() => setHelpOpen(false)}>
        <dl className="dam-dl">
          {NAV_SHORTCUTS.map((s) => (
            <Fragment key={s.key}>
              <dt>
                <kbd>{s.key}</kbd>
              </dt>
              <dd>{t(s.labelKey)}</dd>
            </Fragment>
          ))}
          <dt>
            <kbd>{HELP_KEY}</kbd>
          </dt>
          <dd>{t('shell.shortcuts.help')}</dd>
        </dl>
      </Modal>
    </>
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
            <NavLink to="/library">{t('nav.library')}</NavLink>
            <NavLink to="/search">{t('nav.search')}</NavLink>
            <NavLink to="/collections">{t('nav.collections')}</NavLink>
            <NavLink to="/upload">{t('nav.upload')}</NavLink>
            <NavLink to="/me">{t('nav.account')}</NavLink>
          </nav>
        )}
        <div className="dam-header__tools">
          {auth.status === 'authenticated' && <TenantSwitcher />}
          <LanguageSwitcher />
          {auth.status === 'authenticated' && <KeyboardShortcuts />}
          <UserMenu />
        </div>
      </header>
      <main id="main" className="dam-main" tabIndex={-1}>
        <Outlet />
      </main>
    </div>
  );
}
