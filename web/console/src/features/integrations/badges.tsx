import { useTranslation } from 'react-i18next';
import { Badge, type BadgeTone } from '../../ui';
import type { ConnectionStatus, ConnectorStatus } from './integrationsTypes';

const CONNECTOR_TONE: Record<ConnectorStatus, BadgeTone> = { available: 'success', preview: 'info', planned: 'neutral' };
const CONNECTION_TONE: Record<ConnectionStatus, BadgeTone> = { ok: 'success', error: 'danger', new: 'neutral' };

/** Available, Preview or Planned. The text carries the meaning; the colour only backs it up. A preview connector that cannot be
 * tested (`storedOnly`) says what it does instead: it saves settings only. */
export function ConnectorStatusBadge({ status, storedOnly = false }: { status: string; storedOnly?: boolean }) {
  const { t } = useTranslation();
  const known = (status in CONNECTOR_TONE ? status : 'planned') as ConnectorStatus;
  const key = known === 'preview' && storedOnly ? 'previewStored' : known;
  return (
    <Badge tone={CONNECTOR_TONE[known]} size="sm">
      {t(`integrations.connectorStatus.${key}`)}
    </Badge>
  );
}

/** Working, Needs attention or Not tested yet. A connection of a connector that cannot be tested (`saved`) is simply Saved: it
 * was never meant to be tested, so "not tested" would read as a problem. */
export function ConnectionStatusBadge({ status, saved = false }: { status: string; saved?: boolean }) {
  const { t } = useTranslation();
  if (saved) {
    return (
      <Badge tone="info" size="sm">
        {t('integrations.connectionStatus.saved')}
      </Badge>
    );
  }
  const known = (status in CONNECTION_TONE ? status : 'new') as ConnectionStatus;
  return (
    <Badge tone={CONNECTION_TONE[known]} size="sm">
      {t(`integrations.connectionStatus.${known}`)}
    </Badge>
  );
}
