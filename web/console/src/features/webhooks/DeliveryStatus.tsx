import { useTranslation } from 'react-i18next';
import { Badge } from '../../ui';
import { deliveryState, shortDuration, type DeliveryState } from './format';
import type { DeliveryDto } from './webhooksApi';

const TONE = { succeeded: 'success', failed: 'danger', pending: 'warning', retrying: 'warning' } as const;

/** A status badge by state: succeeded green, pending or retrying amber, failed red. The text always carries the meaning. */
export function StatusBadge({ state, size = 'sm' }: { state: DeliveryState | string; size?: 'sm' | 'md' }) {
  const { t } = useTranslation();
  const known = state in TONE ? (state as DeliveryState) : 'pending';
  return (
    <Badge tone={TONE[known]} size={size}>
      {t(`webhooks.status.${known}`)}
    </Badge>
  );
}

/** "Retry in 5 min" for a pending delivery with a scheduled next attempt; nothing when there is nothing to wait for. */
export function RetryText({
  delivery: d,
  now,
}: {
  delivery: Pick<DeliveryDto, 'status' | 'nextAttemptAt' | 'lastStatusCode' | 'lastError'>;
  now: number;
}) {
  const { t, i18n } = useTranslation();
  if (d.status !== 'pending' || !d.nextAttemptAt) return null;
  const wait = new Date(d.nextAttemptAt).getTime() - now;
  // A delivery with no failure yet is just waiting for its first attempt; "retry" would be wrong.
  const first = deliveryState(d) === 'pending';
  const text =
    wait <= 5_000
      ? t(first ? 'webhooks.deliveries.sendingNow' : 'webhooks.deliveries.retryingNow')
      : t(first ? 'webhooks.deliveries.sendIn' : 'webhooks.deliveries.retryIn', { time: shortDuration(wait, i18n.language) });
  return <span className="dam-whretry">{text}</span>;
}
