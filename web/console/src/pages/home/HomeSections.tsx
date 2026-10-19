import { CaretRight, FolderSimple, MagnifyingGlass } from '@phosphor-icons/react';
import type { TFunction } from 'i18next';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { relativeTime } from '../../lib/relativeTime';
import type { AssetDto } from '../../features/asset/assetApi';
import type { CollectionDto } from '../../features/collections/collectionsApi';
import { Badge, Button, EmptyState, FileTypeIcon, Skeleton } from '@dam/ui-kit';
import { statusTone } from './statusTone';

type Thumbs = Record<string, string | null> | undefined;

export function SectionHead({ title, id, to, linkLabel }: { title: string; id: string; to?: string; linkLabel?: string }) {
  return (
    <div className="dam-home__sectionhead">
      <h2 id={id} className="dam-home__h2">
        {title}
      </h2>
      {to && linkLabel && (
        <Link to={to} className="dam-home__more">
          {linkLabel}
          <CaretRight size={14} weight="bold" aria-hidden="true" />
        </Link>
      )}
    </div>
  );
}

/** A request failed: say so in one quiet line with a retry, instead of an error wall - the rest of Home still works. */
export function SectionNote({ onRetry }: { onRetry: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="dam-home__note">
      <span>{t('home.loadFailed')}</span>
      <Button variant="ghost" size="sm" onClick={onRetry}>
        {t('home.retry')}
      </Button>
    </div>
  );
}

function AssetImage({ type, url }: { type: string; url?: string | null }) {
  return url ? (
    <img className="dam-home__img" src={url} alt="" loading="lazy" />
  ) : (
    <span className="dam-home__img dam-home__img--empty" aria-hidden="true">
      <FileTypeIcon type={type} size={28} />
    </span>
  );
}

function assetMeta(t: TFunction, locale: string, a: AssetDto) {
  return `${t(`library.type.${a.type}`)} · ${relativeTime(a.updatedAt, locale)}`;
}

export function RecentStrip({ assets, thumbs, locale }: { assets: AssetDto[]; thumbs: Thumbs; locale: string }) {
  const { t } = useTranslation();
  return (
    <ul className="dam-home__strip" aria-labelledby="home-recent">
      {assets.map((a) => (
        <li key={a.id}>
          <Link to={`/library/assets/${a.id}`} className="dam-home__asset">
            <span className="dam-home__thumb">
              <AssetImage type={a.type} url={thumbs?.[a.id]} />
              <Badge tone={statusTone(a.status)} size="sm" className="dam-home__status">
                {t(`library.status.${a.status}`)}
              </Badge>
            </span>
            <span className="dam-home__assettitle">{a.title}</span>
            <span className="dam-home__assetmeta">{assetMeta(t, locale, a)}</span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

export function RecentSkeleton() {
  return (
    <div className="dam-home__strip" aria-hidden="true">
      {[0, 1, 2, 3, 4, 5].map((i) => (
        <div key={i} className="dam-home__asset">
          <span className="dam-home__thumb">
            <Skeleton height="100%" />
          </span>
          <Skeleton width="70%" height={14} />
          <Skeleton width="45%" height={12} />
        </div>
      ))}
    </div>
  );
}

export function ReviewList({ assets, thumbs, locale }: { assets: AssetDto[]; thumbs: Thumbs; locale: string }) {
  const { t } = useTranslation();
  return (
    <ul className="dam-home__reviews" aria-labelledby="home-review">
      {assets.map((a) => (
        <li key={a.id}>
          <Link to={`/library/assets/${a.id}`} className="dam-home__review" aria-label={t('home.open', { title: a.title })}>
            <span className="dam-home__reviewthumb">
              <AssetImage type={a.type} url={thumbs?.[a.id]} />
            </span>
            <span className="dam-home__reviewtext">
              <span className="dam-home__assettitle">{a.title}</span>
              <span className="dam-home__assetmeta">{assetMeta(t, locale, a)}</span>
            </span>
            <CaretRight size={16} aria-hidden="true" className="dam-home__chev" />
          </Link>
        </li>
      ))}
    </ul>
  );
}

export function ListSkeleton({ rows = 3 }: { rows?: number }) {
  return (
    <div className="dam-home__reviews" aria-hidden="true">
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="dam-home__review">
          <span className="dam-home__reviewthumb">
            <Skeleton height="100%" />
          </span>
          <span className="dam-home__reviewtext">
            <Skeleton width="60%" height={14} />
            <Skeleton width="35%" height={12} />
          </span>
        </div>
      ))}
    </div>
  );
}

export function CollectionCards({ collections, thumbs }: { collections: CollectionDto[]; thumbs: Thumbs }) {
  const { t } = useTranslation();
  return (
    <ul className="dam-home__collections" aria-labelledby="home-collections">
      {collections.map((c) => {
        const cover = c.assetIds[0] ? thumbs?.[c.assetIds[0]] : undefined;
        return (
          <li key={c.id}>
            <Link to={`/collections/${c.id}`} className="dam-home__collection">
              <span className="dam-home__cover">
                {cover ? (
                  <img className="dam-home__img" src={cover} alt="" loading="lazy" />
                ) : (
                  <span className="dam-home__img dam-home__img--empty" aria-hidden="true">
                    {c.type === 'smart' ? <MagnifyingGlass size={28} /> : <FolderSimple size={28} />}
                  </span>
                )}
              </span>
              <span className="dam-home__assettitle">{c.name}</span>
              <span className="dam-home__assetmeta">
                {c.type === 'smart' ? t('home.collections.smart') : t('home.collections.count', { count: c.assetIds.length })}
              </span>
            </Link>
          </li>
        );
      })}
    </ul>
  );
}

export function CollectionsSkeleton() {
  return (
    <div className="dam-home__collections" aria-hidden="true">
      {[0, 1, 2].map((i) => (
        <div key={i} className="dam-home__collection">
          <span className="dam-home__cover">
            <Skeleton height="100%" />
          </span>
          <Skeleton width="65%" height={14} />
          <Skeleton width="40%" height={12} />
        </div>
      ))}
    </div>
  );
}

export function SectionEmpty({
  icon,
  title,
  description,
  action,
}: {
  icon: ReactNode;
  title: string;
  description: string;
  action?: ReactNode;
}) {
  return (
    <div className="dam-home__empty">
      <EmptyState icon={icon} title={title} description={description} action={action} titleAs="p" />
    </div>
  );
}
