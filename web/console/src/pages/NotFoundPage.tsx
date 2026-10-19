import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { StatusPanel } from './StatusPanel';
import { usePageTitle } from './usePageTitle';

export function NotFoundPage() {
  const { t } = useTranslation();
  usePageTitle(t('pageTitle.notFound'));
  return (
    <StatusPanel
      variant="lost"
      title={t('notFound.title')}
      description={t('notFound.body')}
      actions={
        <>
          <Link to="/" className="dam-btn dam-btn--primary">
            {t('notFound.home')}
          </Link>
          <Link to="/library" className="dam-btn dam-btn--secondary">
            {t('notFound.library')}
          </Link>
        </>
      }
    />
  );
}
