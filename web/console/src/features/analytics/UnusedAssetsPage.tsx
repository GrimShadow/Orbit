import { ArrowLeft, Lock } from '@phosphor-icons/react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { EmptyState, PageHeader } from '@dam/ui-kit';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { isForbidden, useOverview } from './analyticsApi';
import { UnusedAssetsPanel } from './UnusedAssets';
import './analytics.css';

/** `/insights/unused` - the whole list behind the dashboard card: every asset with no recorded use in the window. */
export function UnusedAssetsPage() {
  const { t } = useTranslation();
  useDocumentTitle(t('analytics.unused.pageTitle'));
  // The overview call doubles as the permission probe: analytics.read is role-level, so a 403 here means no access.
  const probe = useOverview(30);
  return (
    <div className="dam-page dam-unusedpage">
      <Link to="/" className="dam-unusedpage__back">
        <ArrowLeft size={14} weight="bold" aria-hidden="true" />
        {t('analytics.unused.back')}
      </Link>
      <PageHeader title={t('analytics.unused.pageTitle')} description={t('analytics.unused.pageDesc')} />
      {probe.error && isForbidden(probe.error) ? (
        <EmptyState icon={<Lock />} title={t('analytics.noAccessTitle')} description={t('analytics.noAccessBody')} />
      ) : (
        <div className="dam-unusedpage__card">
          <UnusedAssetsPanel titleAs="h2" />
        </div>
      )}
    </div>
  );
}
