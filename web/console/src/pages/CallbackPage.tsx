import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router-dom';
import { getUserManager } from '../auth/AuthProvider';
import { LoadingRegion, Logo } from '@dam/ui-kit';
import { StatusPanel } from './StatusPanel';
import { usePageTitle } from './usePageTitle';

/** The OIDC redirect target: exchanges the one-time code, then returns the user to where they were headed. */
export function CallbackPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [failed, setFailed] = useState(false);
  const ran = useRef(false); // StrictMode runs effects twice; the auth code is single-use
  usePageTitle(t('pageTitle.callback'));
  useEffect(() => {
    if (ran.current) return;
    ran.current = true;
    getUserManager()
      .signinRedirectCallback()
      .then((user) => navigate(typeof user.state === 'string' ? user.state : '/', { replace: true }))
      .catch(() => setFailed(true));
  }, [navigate]);

  if (failed) {
    return (
      <StatusPanel
        variant="fault"
        role="alert"
        title={t('callback.failed')}
        description={t('callback.body')}
        actions={
          <Link to="/login" className="dam-btn dam-btn--primary">
            {t('callback.retry')}
          </Link>
        }
      />
    );
  }
  return (
    <LoadingRegion label={t('callback.signingIn')}>
      <div className="dam-callback" aria-hidden="true">
        <Logo size={40} wordmark={false} label="" />
        <p className="dam-callback__title">
          <span className="dam-spinner" /> {t('callback.signingIn')}
        </p>
        <p className="dam-callback__hint">{t('callback.hint')}</p>
      </div>
    </LoadingRegion>
  );
}
