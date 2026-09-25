import { Badge, Button, LoadingRegion, Skeleton } from '../ui';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { ApiError } from '../api/client';
import { useMe } from '../api/me';
import { getUserManager, useAuth } from '../auth/AuthProvider';

export function LoginPage() {
  const { t } = useTranslation();
  const auth = useAuth();
  const from = (useLocation().state as { from?: string } | null)?.from;
  return (
    <div className="dam-page dam-stack">
      <h1>{t('login.title')}</h1>
      <p>{t('login.body')}</p>
      <Button variant="primary" loading={auth.status === 'loading'} onClick={() => void auth.signIn(from)}>
        {t('login.action')}
      </Button>
    </div>
  );
}

export function CallbackPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [failed, setFailed] = useState(false);
  const ran = useRef(false); // StrictMode runs effects twice; the auth code is single-use
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
      <div role="alert" className="dam-page dam-stack">
        <h1>{t('callback.failed')}</h1>
        <Link to="/login">{t('callback.retry')}</Link>
      </div>
    );
  }
  return (
    <LoadingRegion label={t('callback.signingIn')}>
      <Skeleton height={24} width={240} />
    </LoadingRegion>
  );
}

export function HomePage() {
  const { t } = useTranslation();
  const auth = useAuth();
  return (
    <div className="dam-page dam-stack">
      <h1>{t('home.title', { name: auth.name ?? auth.email ?? '' })}</h1>
      <p>{t('home.body')}</p>
    </div>
  );
}

export function MePage() {
  const { t } = useTranslation();
  const { data, error, isPending, refetch } = useMe();

  if (isPending)
    return (
      <LoadingRegion>
        <Skeleton height={28} width={220} />
        <br />
        <Skeleton height={120} />
      </LoadingRegion>
    );
  if (error) {
    const api = error instanceof ApiError ? error : undefined;
    return (
      <div role="alert" className="dam-page dam-stack">
        <h1>{t('me.loadFailed')}</h1>
        <p>{api ? `${api.status} ${api.title}` : error.message}</p>
        {api?.correlationId && <p className="dam-hint">{t('me.reference', { id: api.correlationId })}</p>}
        <Button onClick={() => void refetch()}>{t('me.retry')}</Button>
      </div>
    );
  }
  return (
    <div className="dam-page dam-stack">
      <h1>{t('me.title')}</h1>
      <dl className="dam-dl">
        <dt>{t('me.name')}</dt>
        <dd>{data.displayName}</dd>
        <dt>{t('me.email')}</dt>
        <dd>{data.email}</dd>
        <dt>{t('me.tenant')}</dt>
        <dd>{data.tenantId}</dd>
        <dt>{t('me.roles')}</dt>
        <dd>
          {data.roles.length ? (
            <ul className="dam-chips">
              {data.roles.map((r) => (
                <li key={r}>
                  <Badge>{r}</Badge>
                </li>
              ))}
            </ul>
          ) : (
            t('me.noRoles')
          )}
        </dd>
      </dl>
    </div>
  );
}

export function NotFoundPage() {
  const { t } = useTranslation();
  return (
    <div className="dam-page dam-stack">
      <h1>{t('notFound.title')}</h1>
      <p>{t('notFound.body')}</p>
      <Link to="/">{t('notFound.home')}</Link>
    </div>
  );
}
