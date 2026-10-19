import { Check, Copy, IdentificationBadge, Key, ShieldCheck, SignOut, Users, WarningCircle } from '@phosphor-icons/react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useMe, type Me } from '../api/me';
import { useAuth } from '../auth/AuthProvider';
import { Avatar, Badge, Button, Card, IconButton, LoadingRegion, PageHeader, Skeleton, useToast } from '@dam/ui-kit';
import './pages.css';
import { usePageTitle } from './usePageTitle';

function Section({ icon, title, hint, children }: { icon: ReactNode; title: string; hint?: string; children: ReactNode }) {
  const id = `acct-${title.replace(/\W+/g, '-').toLowerCase()}`;
  return (
    <Card as="section" padding="lg" aria-labelledby={id} className="dam-account__section">
      <div className="dam-account__sectionhead">
        <span className="dam-account__sectionicon" aria-hidden="true">
          {icon}
        </span>
        <div>
          <h2 id={id} className="dam-account__heading">
            {title}
          </h2>
          {hint && <p className="dam-hint">{hint}</p>}
        </div>
      </div>
      {children}
    </Card>
  );
}

function Chips({ children }: { children: ReactNode }) {
  return <ul className="dam-chips">{children}</ul>;
}

function TenantId({ id }: { id: string }) {
  const { t } = useTranslation();
  const toast = useToast();
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(id);
      setCopied(true);
      setTimeout(() => setCopied(false), 1800);
      toast.push({ title: t('me.copied'), tone: 'success', durationMs: 3000 });
    } catch {
      toast.push({ title: t('me.copyFailed'), tone: 'error' });
    }
  };
  return (
    <div className="dam-account__id">
      <code className="dam-account__code">{id}</code>
      <IconButton label={t('me.copyTenant')} size="sm" variant="secondary" onClick={() => void copy()}>
        {copied ? <Check size={16} weight="bold" aria-hidden="true" /> : <Copy size={16} aria-hidden="true" />}
      </IconButton>
    </div>
  );
}

/** Permissions are dotted codes ("assets.read"); group them by the part before the first dot for scanning. */
function groupPermissions(permissions: string[]): [string, string[]][] {
  const groups = new Map<string, string[]>();
  for (const p of [...permissions].sort()) {
    const area = p.includes('.') ? p.slice(0, p.indexOf('.')) : p;
    groups.set(area, [...(groups.get(area) ?? []), p]);
  }
  return [...groups.entries()];
}

function AccountLoading() {
  return (
    <LoadingRegion>
      <div className="dam-account">
        <Card padding="lg">
          <div className="dam-account__profile">
            <Skeleton width={72} height={72} />
            <div className="dam-account__identity">
              <Skeleton width={200} height={24} />
              <Skeleton width={260} height={16} />
            </div>
          </div>
        </Card>
        <div className="dam-account__grid">
          {[0, 1, 2, 3].map((i) => (
            <Card key={i} padding="lg">
              <Skeleton width={140} height={18} />
              <div className="dam-account__skeletonbody">
                <Skeleton height={32} />
              </div>
            </Card>
          ))}
        </div>
      </div>
    </LoadingRegion>
  );
}

function AccountError({ error, onRetry }: { error: Error; onRetry: () => void }) {
  const { t } = useTranslation();
  const api = error instanceof ApiError ? error : undefined;
  return (
    <Card padding="lg" role="alert" className="dam-account__error">
      <span className="dam-account__erroricon" aria-hidden="true">
        <WarningCircle size={28} weight="duotone" />
      </span>
      <h2 className="dam-account__heading">{t('me.loadFailed')}</h2>
      <p className="dam-account__errortext">{api ? `${api.status} ${api.title}` : error.message}</p>
      <p className="dam-hint">{t('me.loadFailedBody')}</p>
      {api?.correlationId && <p className="dam-hint dam-account__ref">{t('me.reference', { id: api.correlationId })}</p>}
      <Button onClick={onRetry}>{t('me.retry')}</Button>
    </Card>
  );
}

function AccountDetails({ me }: { me: Me }) {
  const { t } = useTranslation();
  const auth = useAuth();
  const name = me.displayName ?? auth.name ?? me.email ?? auth.email ?? '';
  const email = me.email ?? auth.email;
  const inactive = new Set(me.inactiveRoles);
  const attributes = Object.entries(me.attributes);

  return (
    <div className="dam-account">
      <Card padding="lg">
        <div className="dam-account__profile">
          <Avatar name={name} size="lg" className="dam-account__avatar" />
          <div className="dam-account__identity">
            <p className="dam-account__name">{name}</p>
            {email && <p className="dam-account__email">{email}</p>}
          </div>
          <Button variant="secondary" onClick={() => void auth.signOut()}>
            <SignOut size={16} aria-hidden="true" />
            {t('me.signOut')}
          </Button>
        </div>
      </Card>

      {me.inactiveRoles.length > 0 && (
        <div role="alert" className="dam-account__warning">
          <WarningCircle size={22} weight="fill" aria-hidden="true" />
          <div>
            <p className="dam-account__warningtitle">{t('me.inactiveTitle')}</p>
            <p>{t('me.inactiveRoles', { roles: me.inactiveRoles.join(', ') })}</p>
          </div>
        </div>
      )}

      <div className="dam-account__grid">
        <Section icon={<IdentificationBadge size={20} />} title={t('me.workspace')}>
          <dl className="dam-account__facts">
            <div>
              <dt>{t('me.tenant')}</dt>
              <dd>
                <TenantId id={me.tenantId} />
              </dd>
            </div>
            <div>
              <dt>{t('me.groups')}</dt>
              <dd>
                {me.groups.length ? (
                  <Chips>
                    {me.groups.map((g) => (
                      <li key={g}>
                        <Badge>{g}</Badge>
                      </li>
                    ))}
                  </Chips>
                ) : (
                  <span className="dam-hint">{t('me.noGroups')}</span>
                )}
              </dd>
            </div>
          </dl>
        </Section>

        <Section icon={<Users size={20} />} title={t('me.roles')} hint={t('me.rolesHint')}>
          {me.roles.length ? (
            <Chips>
              {me.roles.map((r) => (
                <li key={r}>
                  <Badge tone={inactive.has(r) ? 'warning' : 'brand'}>
                    {r}
                    {inactive.has(r) && <span className="dam-account__off">{t('me.switchedOff')}</span>}
                  </Badge>
                </li>
              ))}
            </Chips>
          ) : (
            <p className="dam-hint">{t('me.noRoles')}</p>
          )}
        </Section>

        <Section icon={<Key size={20} />} title={t('me.permissions')} hint={t('me.permissionsHint')}>
          <p className="dam-account__count">{t('me.permissionCount', { count: me.permissions.length })}</p>
          {me.permissions.length > 0 ? (
            <details className="dam-account__details">
              <summary>{t('me.showPermissions')}</summary>
              <dl className="dam-account__perms">
                {groupPermissions(me.permissions).map(([area, items]) => (
                  <div key={area}>
                    <dt>{area}</dt>
                    <dd>
                      <Chips>
                        {items.map((p) => (
                          <li key={p}>
                            <code className="dam-account__perm">{p}</code>
                          </li>
                        ))}
                      </Chips>
                    </dd>
                  </div>
                ))}
              </dl>
            </details>
          ) : (
            <p className="dam-hint">{t('me.noPermissions')}</p>
          )}
        </Section>

        <Section icon={<ShieldCheck size={20} />} title={t('me.attributes')} hint={t('me.attributesHint')}>
          {attributes.length ? (
            <dl className="dam-account__facts">
              {attributes.map(([key, values]) => (
                <div key={key}>
                  <dt>{key}</dt>
                  <dd>
                    <Chips>
                      {values.map((v) => (
                        <li key={v}>
                          <Badge tone="info">{v}</Badge>
                        </li>
                      ))}
                    </Chips>
                  </dd>
                </div>
              ))}
            </dl>
          ) : (
            <p className="dam-hint">{t('me.noAttributes')}</p>
          )}
        </Section>
      </div>
    </div>
  );
}

export function MePage() {
  const { t } = useTranslation();
  const { data, error, isPending, refetch } = useMe();
  usePageTitle(t('pageTitle.account'));

  return (
    <div className="dam-page dam-page--medium">
      <PageHeader title={t('me.title')} description={t('me.subtitle')} />
      {isPending ? (
        <AccountLoading />
      ) : error ? (
        <AccountError error={error} onRetry={() => void refetch()} />
      ) : (
        <AccountDetails me={data} />
      )}
    </div>
  );
}
