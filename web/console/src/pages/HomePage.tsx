import { Images, PaperPlaneTilt, Plus, UploadSimple, FolderSimple } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthProvider';
import { useAssets } from '../features/library/assets';
import { useCollections } from '../features/collections/collectionsApi';
import { useThumbnailLinks } from '../features/asset/assetApi';
import { PageHeader } from '@dam/ui-kit';
import { dayPartOf, firstNameOf } from './home/homeData';
import { HealthCard, Kpis, TopUsedCard, UnusedCard, UsageCard } from './home/Insights';
import {
  CollectionCards,
  CollectionsSkeleton,
  ListSkeleton,
  RecentSkeleton,
  RecentStrip,
  ReviewList,
  SectionEmpty,
  SectionHead,
  SectionNote,
} from './home/HomeSections';
import '../features/analytics/analytics.css';
import './pages.css';
import { usePageTitle } from './usePageTitle';

const RECENT_COUNT = 8;
const REVIEW_COUNT = 5;
const COLLECTION_COUNT = 4;

function Recent({ locale }: { locale: string }) {
  const { t } = useTranslation();
  const query = useAssets({ limit: RECENT_COUNT });
  const assets = query.data?.items.slice(0, RECENT_COUNT) ?? [];
  const thumbs = useThumbnailLinks(assets.map((a) => a.id));
  return (
    <section aria-labelledby="home-recent" className="dam-home__section">
      <SectionHead id="home-recent" title={t('home.recent.title')} to="/library" linkLabel={t('home.recent.viewAll')} />
      {query.isPending ? (
        <RecentSkeleton />
      ) : query.error ? (
        <SectionNote onRetry={() => void query.refetch()} />
      ) : assets.length === 0 ? (
        <SectionEmpty
          icon={<Images />}
          title={t('home.recent.emptyTitle')}
          description={t('home.recent.emptyBody')}
          action={
            <Link to="/upload" className="dam-btn dam-btn--primary">
              <UploadSimple size={16} weight="bold" aria-hidden="true" />
              {t('home.recent.emptyAction')}
            </Link>
          }
        />
      ) : (
        <RecentStrip assets={assets} thumbs={thumbs.data} locale={locale} />
      )}
    </section>
  );
}

function Review({ locale }: { locale: string }) {
  const { t } = useTranslation();
  const query = useAssets({ status: 'in_review', limit: REVIEW_COUNT });
  const assets = query.data?.items.slice(0, REVIEW_COUNT) ?? [];
  const thumbs = useThumbnailLinks(assets.map((a) => a.id));
  return (
    <section aria-labelledby="home-review" className="dam-home__section">
      <SectionHead
        id="home-review"
        title={t('home.review.title')}
        to="/library?status=in_review"
        linkLabel={t('home.review.viewAll')}
      />
      {query.isPending ? (
        <ListSkeleton />
      ) : query.error ? (
        <SectionNote onRetry={() => void query.refetch()} />
      ) : assets.length === 0 ? (
        <SectionEmpty icon={<PaperPlaneTilt />} title={t('home.review.emptyTitle')} description={t('home.review.emptyBody')} />
      ) : (
        <ReviewList assets={assets} thumbs={thumbs.data} locale={locale} />
      )}
    </section>
  );
}

function Collections() {
  const { t } = useTranslation();
  const query = useCollections();
  const collections = [...(query.data ?? [])].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, COLLECTION_COUNT);
  const covers = collections.map((c) => c.assetIds[0]).filter((id): id is string => Boolean(id));
  const thumbs = useThumbnailLinks(covers);
  return (
    <section aria-labelledby="home-collections" className="dam-home__section">
      <SectionHead
        id="home-collections"
        title={t('home.collections.title')}
        to="/collections"
        linkLabel={t('home.collections.viewAll')}
      />
      {query.isPending ? (
        <CollectionsSkeleton />
      ) : query.error ? (
        <SectionNote onRetry={() => void query.refetch()} />
      ) : collections.length === 0 ? (
        <SectionEmpty
          icon={<FolderSimple />}
          title={t('home.collections.emptyTitle')}
          description={t('home.collections.emptyBody')}
          action={
            <Link to="/collections" className="dam-btn dam-btn--secondary">
              <Plus size={16} weight="bold" aria-hidden="true" />
              {t('home.collections.emptyAction')}
            </Link>
          }
        />
      ) : (
        <CollectionCards collections={collections} thumbs={thumbs.data} />
      )}
    </section>
  );
}

export function HomePage() {
  const { t, i18n } = useTranslation();
  const auth = useAuth();
  usePageTitle(t('pageTitle.home'));
  const now = new Date();
  const name = firstNameOf(auth.name ?? auth.email);
  const hello = t(`home.greeting.${dayPartOf(now)}`, { name });
  const greeting = name ? hello : hello.replace(/,\s*$/, '');
  const date = new Intl.DateTimeFormat(i18n.language, { weekday: 'long', day: 'numeric', month: 'long' }).format(now);

  return (
    <div className="dam-page dam-home">
      <PageHeader
        eyebrow={date}
        title={greeting}
        description={t('home.subtitle')}
        actions={
          <>
            <Link to="/collections" className="dam-btn dam-btn--secondary">
              <Plus size={16} weight="bold" aria-hidden="true" />
              {t('home.quick.collection.title')}
            </Link>
          </>
        }
      />
      <Kpis />
      <div className="dam-home__insights">
        <UsageCard />
        <HealthCard />
      </div>
      <UnusedCard />
      <div className="dam-home__split dam-home__split--even">
        <TopUsedCard />
        <Review locale={i18n.language} />
      </div>
      <Recent locale={i18n.language} />
      <Collections />
    </div>
  );
}
