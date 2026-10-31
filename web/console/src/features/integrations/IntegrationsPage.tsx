import { Lock } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMe } from '../../api/me';
import { Button, EmptyState, PageHeader, Skeleton, WarningCircle } from '../../ui';
import { useDocumentTitle } from '../library/useDocumentTitle';
import '../webhooks/webhooks.css';
import { ConnectionForm } from './ConnectionForm';
import { ConnectionList } from './ConnectionList';
import { ConnectorCatalog } from './ConnectorCatalog';
import { useConnections, useConnectors } from './integrationsApi';
import type { ConnectionDto, ConnectorDto } from './integrationsTypes';
import './integrations.css';
import { useSettingsEyebrow } from '../settings/useSettingsEyebrow';

interface FormState {
  connector: ConnectorDto;
  connection: ConnectionDto | null;
  key: number;
}

/** `/integrations`: connect Orbit to other systems by their APIs (ADR-0040). Needs `integrations.manage`. */
export function IntegrationsPage() {
  const { t } = useTranslation();
  useDocumentTitle(t('integrations.title'));
  const eyebrow = useSettingsEyebrow();
  const me = useMe();
  const allowed = !!me.data?.permissions.includes('integrations.manage');
  const connectors = useConnectors(allowed);
  const connections = useConnections(allowed);
  const [form, setForm] = useState<FormState | null>(null);
  const [formKey, setFormKey] = useState(0);

  const open = (connector: ConnectorDto, connection: ConnectionDto | null) => {
    setFormKey((k) => k + 1);
    setForm({ connector, connection, key: formKey + 1 });
  };

  let body;
  if (me.isPending) {
    body = (
      <div role="status" aria-live="polite" className="dam-stack dam-stack--fill">
        <span className="dam-sr-only">{t('integrations.loading')}</span>
        <Skeleton height={36} width="50%" />
        <Skeleton height={140} />
      </div>
    );
  } else if (me.isError) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('integrations.loadFailed')}
          description={t('integrations.loadFailedHint')}
          action={<Button onClick={() => void me.refetch()}>{t('integrations.retry')}</Button>}
        />
      </div>
    );
  } else if (!allowed) {
    body = (
      <EmptyState
        icon={<Lock size={28} />}
        title={t('integrations.forbidden.title')}
        description={t('integrations.forbidden.description')}
      />
    );
  } else {
    body = (
      <>
        <section className="dam-intsection" aria-labelledby="int-catalog-h">
          <div className="dam-intsection__head">
            <h2 id="int-catalog-h" className="dam-intsection__title">
              {t('integrations.catalog.title')}
            </h2>
            <p className="dam-hint">{t('integrations.catalog.intro')}</p>
          </div>
          <ConnectorCatalog
            connectors={connectors.data}
            loading={connectors.isPending}
            error={connectors.error}
            onRetry={() => void connectors.refetch()}
            onConnect={(c) => open(c, null)}
          />
        </section>

        <section className="dam-intsection" aria-labelledby="int-connections-h">
          <div className="dam-intsection__head">
            <h2 id="int-connections-h" className="dam-intsection__title">
              {t('integrations.connections.title')}
            </h2>
            <p className="dam-hint">{t('integrations.connections.intro')}</p>
          </div>
          <ConnectionList
            connections={connections.data}
            connectors={connectors.data}
            loading={connections.isPending}
            error={connections.error}
            onRetry={() => void connections.refetch()}
            onEdit={(connection, connector) => open(connector, connection)}
          />
        </section>

        {form && (
          <ConnectionForm
            key={form.key}
            open
            connector={form.connector}
            connection={form.connection}
            onClose={() => setForm(null)}
          />
        )}
      </>
    );
  }

  return (
    <div className="dam-page dam-intpage">
      <PageHeader title={t('integrations.title')} eyebrow={eyebrow} description={t('integrations.description')} />
      {body}
    </div>
  );
}
