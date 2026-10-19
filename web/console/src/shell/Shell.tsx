import { useCallback, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Outlet } from 'react-router-dom';
import { useAuth } from '../auth/AuthProvider';
import { MobileTabBar, Sidebar } from './Sidebar';
import { TopBar } from './TopBar';
import './shell.css';

const SIDEBAR_KEY = 'dam.sidebar';

function readCollapsed(): boolean {
  try {
    return localStorage.getItem(SIDEBAR_KEY) === 'collapsed';
  } catch {
    return false;
  }
}

/** The product frame: a collapsible navigation sidebar on wide screens (a bottom tab bar on phones), a top bar with
 * the global search, the upload action and the account menu, and the page itself in `<main>`. Signed-out pages
 * (login, auth callback) get the top bar only. */
export function Shell() {
  const { t } = useTranslation();
  const auth = useAuth();
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const toggle = useCallback(() => {
    setCollapsed((c) => {
      try {
        localStorage.setItem(SIDEBAR_KEY, c ? 'expanded' : 'collapsed');
      } catch {
        /* a remembered width is a convenience only */
      }
      return !c;
    });
  }, []);
  const signedIn = auth.status === 'authenticated';

  return (
    <div className={`dam-root dam-app${signedIn ? '' : ' dam-app--anonymous'}${collapsed ? ' dam-app--collapsed' : ''}`}>
      <a className="dam-skip" href="#main">
        {t('app.skip')}
      </a>
      {signedIn && <Sidebar collapsed={collapsed} onToggle={toggle} />}
      <div className="dam-app__body">
        <TopBar />
        <main id="main" className="dam-main" tabIndex={-1}>
          <Outlet />
        </main>
      </div>
      {signedIn && <MobileTabBar />}
    </div>
  );
}
