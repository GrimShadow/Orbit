import { Images, PaperPlaneTilt, ShareNetwork } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthProvider';
import { Button, Logo } from '@dam/ui-kit';
import loginBackdrop from './assets/login-bg.jpg';
import './pages.css';
import { usePageTitle } from './usePageTitle';

const FEATURES = [
  { key: 'organise', Icon: Images },
  { key: 'review', Icon: PaperPlaneTilt },
  { key: 'deliver', Icon: ShareNetwork },
] as const;

/** Signed-out landing: a brand panel beside the sign-in card (stacked on phones). Sign-in is a redirect to the
 * organisation's identity provider, so the only control is one button. */
export function LoginPage() {
  const { t } = useTranslation();
  const auth = useAuth();
  const from = (useLocation().state as { from?: string } | null)?.from;
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  usePageTitle(t('pageTitle.login'));

  const start = async () => {
    setBusy(true);
    setFailed(false);
    try {
      await auth.signIn(from);
    } catch {
      setBusy(false);
      setFailed(true);
    }
  };

  return (
    <div className="dam-login">
      <div className="dam-login__bg" aria-hidden="true" style={{ backgroundImage: `url(${loginBackdrop})` }} />
      <aside className="dam-login__panel" aria-label={t('login.panel')}>
        <Logo size={36} />
        <p className="dam-login__tagline">{t('login.tagline')}</p>
        <ul className="dam-login__features">
          {FEATURES.map(({ key, Icon }) => (
            <li key={key}>
              <span className="dam-login__feature-icon" aria-hidden="true">
                <Icon size={20} weight="duotone" />
              </span>
              {t(`login.features.${key}`)}
            </li>
          ))}
        </ul>
      </aside>
      <section className="dam-login__card">
        <Logo size={32} wordmark={false} label="" />
        <h1 className="dam-login__title">{t('login.title')}</h1>
        <p className="dam-login__body">{t('login.body')}</p>
        <Button
          variant="primary"
          className="dam-login__action"
          loading={busy || auth.status === 'loading'}
          onClick={() => void start()}
        >
          {t('login.action')}
        </Button>
        {failed && (
          <p role="alert" className="dam-login__error">
            {t('login.failed')}
          </p>
        )}
        <p className="dam-login__note">{t('login.note')}</p>
      </section>
    </div>
  );
}
