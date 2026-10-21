import { Plug } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Card, EmptyState, Plus, Skeleton, WarningCircle } from '../../ui';
import { ConnectorStatusBadge } from './badges';
import { isForbidden } from './integrationsApi';
import type { ConnectorDto } from './integrationsTypes';

/** The server's category is a label such as "Digital signage"; its translation key is the lower-case, underscored form. */
const categoryKey = (category: string) =>
  category
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_');

interface Props {
  connectors: ConnectorDto[] | undefined;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  onConnect: (connector: ConnectorDto) => void;
}

/** The connectors that can be connected, as cards. A planned connector is listed but cannot be connected. */
export function ConnectorCatalog({ connectors, loading, error, onRetry, onConnect }: Props) {
  const { t } = useTranslation();
  if (loading) {
    return (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('integrations.loading')}</span>
        <ul className="dam-intgrid" aria-hidden="true">
          {[0, 1, 2].map((i) => (
            <li key={i}>
              <Card>
                <div className="dam-stack dam-stack--fill">
                  <Skeleton height={20} width="50%" />
                  <Skeleton height={14} />
                  <Skeleton height={14} width="80%" />
                  <Skeleton height={32} width={110} />
                </div>
              </Card>
            </li>
          ))}
        </ul>
      </div>
    );
  }
  if (error) {
    return (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          titleAs="p"
          title={isForbidden(error) ? t('integrations.forbidden.title') : t('integrations.catalog.loadFailed')}
          description={isForbidden(error) ? t('integrations.forbidden.description') : t('integrations.loadFailedHint')}
          action={isForbidden(error) ? undefined : <Button onClick={onRetry}>{t('integrations.retry')}</Button>}
        />
      </div>
    );
  }
  if (!connectors || connectors.length === 0) {
    return (
      <EmptyState
        icon={<Plug size={28} />}
        titleAs="p"
        title={t('integrations.catalog.empty.title')}
        description={t('integrations.catalog.empty.description')}
      />
    );
  }
  return (
    <ul className="dam-intgrid" aria-label={t('integrations.catalog.title')}>
      {connectors.map((c) => {
        const planned = c.status === 'planned';
        return (
          <li key={c.key}>
            <Card
              as="article"
              aria-labelledby={`int-connector-${c.key}`}
              className={planned ? 'dam-intcard dam-intcard--planned' : 'dam-intcard'}
            >
              <div className="dam-intcard__head">
                <h3 id={`int-connector-${c.key}`} className="dam-intcard__name">
                  {c.name}
                </h3>
                <ConnectorStatusBadge status={c.status} />
              </div>
              <p className="dam-intcard__desc">{c.description}</p>
              <div className="dam-intcard__meta">
                <Badge size="sm">{t(`integrations.category.${categoryKey(c.category)}`, { defaultValue: c.category })}</Badge>
                {c.status === 'preview' && <span className="dam-hint">{t('integrations.catalog.previewNote')}</span>}
                {planned && <span className="dam-hint">{t('integrations.catalog.plannedNote')}</span>}
              </div>
              {!planned && (
                <div className="dam-intcard__foot">
                  <Button
                    variant="primary"
                    aria-label={t('integrations.connectNamed', { name: c.name })}
                    onClick={() => onConnect(c)}
                  >
                    <Plus size={16} weight="bold" aria-hidden="true" />
                    {t('integrations.connect')}
                  </Button>
                </div>
              )}
            </Card>
          </li>
        );
      })}
    </ul>
  );
}
