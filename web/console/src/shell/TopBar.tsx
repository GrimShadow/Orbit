import { MagnifyingGlass, Question, UploadSimple } from '@phosphor-icons/react';
import { Button, Modal } from '@dam/ui-kit';
import { Fragment, useEffect, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useAuth } from '../auth/AuthProvider';
import { useAiCapabilities } from '../features/ai/aiApi';
import { NotificationBell } from '../features/notifications/NotificationBell';
import { Brand } from './Brand';
import { HELP_KEY, isHelpShortcut, isShortcutTarget, matchNavShortcut, NAV_SHORTCUTS } from './keyboardShortcuts';
import { ThemeSwitcher } from './ThemeSwitcher';
import { UserMenu } from './UserMenu';

const SEARCH_FOCUS_KEY = '/';

const WIDE = '(min-width: 640px)';
function useIsWide() {
  const [wide, setWide] = useState(() => window.matchMedia?.(WIDE).matches ?? true);
  useEffect(() => {
    const mq = window.matchMedia?.(WIDE);
    if (!mq) return;
    const onChange = () => setWide(mq.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);
  return wide;
}

/** The global search box: Enter takes the query to the Search page (`/search?q=`); `/` anywhere focuses it. On the
 * Search page itself the box mirrors the current query. When Smart search is available the placeholder invites a
 * description; the Search page decides Smart on/off itself (`?smart=`, else the remembered choice, else on). */
function GlobalSearch() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();
  const input = useRef<HTMLInputElement>(null);
  const [value, setValue] = useState(() => (location.pathname === '/search' ? (params.get('q') ?? '') : ''));
  // The longer invitation only fits the box from tablet width up; a phone keeps the short standard placeholder.
  const wide = useIsWide();
  const smartAvailable = useAiCapabilities().data?.semanticSearch === true && wide;

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === SEARCH_FOCUS_KEY && isShortcutTarget(e.target) && !e.ctrlKey && !e.metaKey && !e.altKey) {
        e.preventDefault();
        input.current?.focus();
      }
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const q = value.trim();
    // Searching again from the Search page keeps its Smart search choice (`?smart=`) rather than resetting it.
    const smart = location.pathname === '/search' ? params.get('smart') : null;
    const extra = smart === '0' || smart === '1' ? `smart=${smart}` : '';
    const qs = [q ? `q=${encodeURIComponent(q)}` : '', extra].filter(Boolean).join('&');
    navigate(qs ? `/search?${qs}` : '/search');
  };

  return (
    <form role="search" className="dam-gsearch" onSubmit={submit}>
      <MagnifyingGlass size={18} aria-hidden="true" className="dam-gsearch__icon" />
      <input
        ref={input}
        type="search"
        className="dam-gsearch__input"
        aria-label={t('shell.search.label')}
        placeholder={smartAvailable ? t('smartSearch.globalPlaceholder') : t('shell.search.placeholder')}
        value={value}
        onChange={(e) => setValue(e.target.value)}
        enterKeyHint="search"
      />
      <kbd className="dam-gsearch__kbd" aria-hidden="true">
        {SEARCH_FOCUS_KEY}
      </kbd>
    </form>
  );
}

/** Single-key navigation shortcuts (spec Step 1.8) plus the `?` help dialog - active only once signed in, and never
 * while focus is on a form control (`isShortcutTarget`), so shortcuts can never hijack ordinary typing. */
function useKeyboardShortcuts(setHelpOpen: (f: (o: boolean) => boolean) => void) {
  const navigate = useNavigate();
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
  }, [navigate, setHelpOpen]);
}

export function TopBar() {
  const { t } = useTranslation();
  const auth = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();
  const [helpOpen, setHelpOpen] = useState(false);
  const signedIn = auth.status === 'authenticated';
  useKeyboardShortcuts(setHelpOpen);

  return (
    <header className="dam-topbar">
      <div className="dam-topbar__brand">
        <Brand />
      </div>
      {signedIn ? (
        <>
          <GlobalSearch key={location.pathname === '/search' ? `q:${params.get('q') ?? ''}` : 'elsewhere'} />
          <div className="dam-topbar__actions">
            {location.pathname !== '/upload' && (
              <Button
                variant="primary"
                className="dam-topbar__upload"
                aria-label={t('nav.upload')}
                onClick={() => navigate('/upload')}
              >
                <UploadSimple size={16} weight="bold" aria-hidden="true" />
                <span className="dam-topbar__upload-label">{t('nav.upload')}</span>
              </Button>
            )}
            <button
              type="button"
              className="dam-topbar__iconbtn"
              aria-label={t('shell.shortcuts.button')}
              title={t('shell.shortcuts.button')}
              onClick={() => setHelpOpen(true)}
            >
              <Question size={20} aria-hidden="true" />
            </button>
            <NotificationBell />
            <UserMenu onShowShortcuts={() => setHelpOpen(true)} />
          </div>
        </>
      ) : (
        <div className="dam-topbar__actions">
          <ThemeSwitcher />
        </div>
      )}
      {signedIn && (
        <Modal open={helpOpen} title={t('shell.shortcuts.title')} onClose={() => setHelpOpen(false)}>
          <dl className="dam-dl dam-shortcuts">
            {NAV_SHORTCUTS.map((s) => (
              <Fragment key={s.key}>
                <dt>
                  <kbd>{s.key}</kbd>
                </dt>
                <dd>{t(s.labelKey)}</dd>
              </Fragment>
            ))}
            <dt>
              <kbd>{SEARCH_FOCUS_KEY}</kbd>
            </dt>
            <dd>{t('shell.shortcuts.search_focus')}</dd>
            <dt>
              <kbd>{HELP_KEY}</kbd>
            </dt>
            <dd>{t('shell.shortcuts.help')}</dd>
          </dl>
        </Modal>
      )}
    </header>
  );
}
