import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../lib/format';
import { relativeTime } from '../../lib/relativeTime';
import { Badge, Button, EmptyState, LoadingRegion, Skeleton, useToast, WarningCircle } from '../../ui';
import { errorMessage, useAssetVersions, useRestoreVersion } from './assetApi';
import { formatLabel } from './fileFacts';

/** The version history as a timeline, newest first, with the current version emphasised and every older one restorable. */
export function VersionsPanel({ assetId }: { assetId: string }) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const versionsQuery = useAssetVersions(assetId);
  const restore = useRestoreVersion(assetId);

  if (versionsQuery.isPending)
    return (
      <LoadingRegion label={t('asset.versionsLoading')}>
        <div className="dam-stack dam-stack--fill">
          <Skeleton height={72} />
          <Skeleton height={72} />
        </div>
      </LoadingRegion>
    );
  if (versionsQuery.error)
    return (
      <EmptyState
        icon={<WarningCircle />}
        titleAs="h2"
        title={t('asset.versionsFailed')}
        action={
          <Button variant="secondary" onClick={() => void versionsQuery.refetch()}>
            {t('library.retry')}
          </Button>
        }
      />
    );
  if (versionsQuery.data.length === 0) return <EmptyState titleAs="h2" title={t('asset.noVersions')} />;

  const versions = [...versionsQuery.data].sort((a, b) => b.versionNo - a.versionNo);
  const restoring = restore.isPending ? restore.variables : undefined;

  return (
    <ol className="dam-timeline" aria-label={t('asset.versions')}>
      {versions.map((v) => (
        <li key={v.id} className={`dam-timeline__item${v.isCurrent ? ' dam-timeline__item--current' : ''}`}>
          <span className="dam-timeline__dot" aria-hidden="true" />
          <div className="dam-timeline__card">
            <div className="dam-timeline__head">
              <span className="dam-timeline__name">{t('asset.versionLabel', { n: v.versionNo })}</span>
              {v.isCurrent && <Badge tone="brand">{t('asset.current')}</Badge>}
            </div>
            <p className="dam-timeline__meta">
              <span>{formatLabel(v.mime)}</span>
              <span>{formatBytes(v.sizeBytes)}</span>
              <time dateTime={v.createdAt} title={new Date(v.createdAt).toLocaleString(i18n.language)}>
                {relativeTime(v.createdAt, i18n.language)}
              </time>
            </p>
            {v.changeNote && <p className="dam-timeline__note">{v.changeNote}</p>}
            {!v.isCurrent && (
              <Button
                variant="secondary"
                size="sm"
                loading={restoring === v.versionNo}
                disabled={restore.isPending}
                onClick={() =>
                  restore.mutate(v.versionNo, {
                    onSuccess: () => push({ title: t('asset.restored', { n: v.versionNo }), tone: 'success' }),
                    onError: (e) => push({ title: t('asset.restoreFailed'), message: errorMessage(e), tone: 'error' }),
                  })
                }
              >
                {t('asset.restore')}
              </Button>
            )}
          </div>
        </li>
      ))}
    </ol>
  );
}
