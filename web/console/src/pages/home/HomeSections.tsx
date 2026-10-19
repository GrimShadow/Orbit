import { CaretRight } from '@phosphor-icons/react';
import type { TFunction } from 'i18next';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { relativeTime } from '../../lib/relativeTime';
import type { AssetDto } from '../../features/asset/assetApi';
import { Button, EmptyState, FileTypeIcon, Skeleton } from '@dam/ui-kit';

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

export function ReviewList({ assets, thumbs, locale }: { assets: AssetDto[]; thumbs: Thumbs; locale: string }) {
  const { t } = useTranslation();
  return (
    <ul className="dam-home__reviews" aria-label={t('home.review.title')}>
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
