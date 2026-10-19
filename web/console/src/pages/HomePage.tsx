import { Plus } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthProvider';
import { PageHeader } from '@dam/ui-kit';
import { dayPartOf, firstNameOf } from './home/homeData';
import { isForbidden, useOverview } from '../features/analytics/analyticsApi';
import { ActivityCard, HealthCard, Kpis, UnusedCard, UsageCard, USAGE_DAYS } from './home/Insights';
import '../features/analytics/analytics.css';
import './pages.css';
import { usePageTitle } from './usePageTitle';

export function HomePage() {
  const { t, i18n } = useTranslation();
  const auth = useAuth();
  usePageTitle(t('pageTitle.home'));
  const analytics = !isForbidden(useOverview(USAGE_DAYS).error);
  const now = new Date();
  const name = firstNameOf(auth.name ?? auth.email);
  const hello = t(`home.greeting.${dayPartOf(now)}`, { name });
  const greeting = name ? hello : hello.replace(/,\s*$/, '');
  const date = new Intl.DateTimeFormat(i18n.language, { weekday: 'long', day: 'numeric', month: 'long' }).format(now);

  return (
    <div className="dam-page dam-home dam-home--fit">
      <PageHeader
        eyebrow={date}
        title={greeting}
        actions={
          <Link to="/collections" className="dam-btn dam-btn--secondary">
            <Plus size={16} weight="bold" aria-hidden="true" />
            {t('home.quick.collection.title')}
          </Link>
        }
      />
      <Kpis />
      <div className={`dam-home__grid${analytics ? '' : ' dam-home__grid--basic'}`}>
        <UsageCard />
        <HealthCard />
        <UnusedCard />
        <ActivityCard locale={i18n.language} />
      </div>
    </div>
  );
}
