import { useTranslation } from 'react-i18next';
import { Badge, type BadgeTone } from '../../ui';
import type { ConnectionStatus, ConnectorStatus } from './integrationsTypes';

const CONNECTOR_TONE: Record<ConnectorStatus, BadgeTone> = { available: 'success', preview: 'info', planned: 'neutral' };
const CONNECTION_TONE: Record<ConnectionStatus, BadgeTone> = { ok: 'success', error: 'danger', new: 'neutral' };

/** Available, Preview or Planned. The text carries the meaning; the colour only backs it up. */
export function ConnectorStatusBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  const known = (status in CONNECTOR_TONE ? status : 'planned') as ConnectorStatus;
  return (
    <Badge tone={CONNECTOR_TONE[known]} size="sm">
      {t(`integrations.connectorStatus.${known}`)}
    </Badge>
  );
}

/** Working, Needs attention or Not tested yet. */
export function ConnectionStatusBadge({ status }: { status: string }) {
  const { t } = useTranslation();
  const known = (status in CONNECTION_TONE ? status : 'new') as ConnectionStatus;
  return (
    <Badge tone={CONNECTION_TONE[known]} size="sm">
      {t(`integrations.connectionStatus.${known}`)}
    </Badge>
  );
}
