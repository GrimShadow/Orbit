import {
  CheckCircle,
  Clock,
  Images,
  NotePencil,
  PaperPlaneTilt,
  Plus,
  Stack,
  UploadSimple,
  FolderSimple,
} from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { useAuth } from '../auth/AuthProvider';
import { useAssets } from '../features/library/assets';
import { useCollections } from '../features/collections/collectionsApi';
import { useThumbnailLinks } from '../features/asset/assetApi';
import { PageHeader, Skeleton, StatCard } from '@dam/ui-kit';
import { dayPartOf, facetCount, firstNameOf, useLibrarySnapshot } from './home/homeData';
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
import './pages.css';
import { usePageTitle } from './usePageTitle';

const RECENT_COUNT = 8;
const REVIEW_COUNT = 5;
const COLLECTION_COUNT = 4;

function QuickActions() {
  const { t } = useTranslation();
  const actions = [
    { to: '/upload', Icon: UploadSimple, key: 'upload' },
    { to: '/library', Icon: Images, key: 'library' },
    { to: '/collections', Icon: Plus, key: 'collection' },
  ] as const;
  return (
    <ul className="dam-home__quickgrid" aria-label={t('home.quick.label')}>
      {actions.map(({ to, Icon, key }) => (
        <li key={key}>
          <Link to={to} className="dam-home__quick">
            <span className="dam-home__quickicon" aria-hidden="true">
              <Icon size={22} weight="bold" />
            </span>
            <span className="dam-home__quicktext">
              <span className="dam-home__quicktitle">{t(`home.quick.${key}.title`)}</span>
              <span className="dam-home__quickbody">{t(`home.quick.${key}.body`)}</span>
            </span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

function Stats() {
  const { t, i18n } = useTranslation();
  const snapshot = useLibrarySnapshot();
  const fmt = (n: number) => new Intl.NumberFormat(i18n.language).format(n);
  const facets = snapshot.data?.facets;
  const stats = [
    { key: 'total', to: '/library', icon: <Stack size={20} weight="duotone" />, tone: 'brand', value: snapshot.data?.total },
    {
      key: 'review',
      to: '/library?status=in_review',
      icon: <Clock size={20} weight="duotone" />,
      tone: 'info',
      value: snapshot.data && facetCount(facets, 'status', 'in_review'),
    },
    {
      key: 'published',
      to: '/library?status=published',
      icon: <CheckCircle size={20} weight="duotone" />,
      tone: 'success',
      value: snapshot.data && facetCount(facets, 'status', 'published'),
    },
    {
      key: 'drafts',
      to: '/library?status=draft',
      icon: <NotePencil size={20} weight="duotone" />,
      tone: 'neutral',
      value: snapshot.data && facetCount(facets, 'status', 'draft'),
    },
  ] as const;

  return (
    <ul className="dam-home__stats" aria-label={t('home.stats.label')}>
      {stats.map((s) => (
        <li key={s.key}>
          {snapshot.isPending ? (
            <div className="dam-stat" aria-hidden="true">
              <Skeleton width={40} height={40} />
              <div className="dam-stat__body">
                <Skeleton width={90} height={12} />
                <Skeleton width={56} height={26} />
              </div>
            </div>
          ) : (
            <Link to={s.to} className="dam-home__statlink">
              <StatCard
                label={t(`home.stats.${s.key}`)}
                value={typeof s.value === 'number' ? fmt(s.value) : '–'}
                hint={typeof s.value === 'number' ? t(`home.stats.${s.key}Hint`) : t('home.stats.unavailable')}
                icon={s.icon}
                tone={s.tone}
              />
            </Link>
          )}
        </li>
      ))}
    </ul>
  );
}

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
      <PageHeader eyebrow={date} title={greeting} description={t('home.subtitle')} />
      <QuickActions />
      <Stats />
      <Recent locale={i18n.language} />
      <div className="dam-home__split">
        <Review locale={i18n.language} />
        <Collections />
      </div>
    </div>
  );
}
