import type { TFunction } from 'i18next';
import { relativeTime } from '../../lib/relativeTime';
import { hasThumbnail } from '../../lib/thumbnails';
import { FileTypeIcon, type Thumb } from '../../ui';
import { statusTone } from './statusTone';

/** The fields every asset-like list item has in common (`AssetDto`, `SearchHitDto`) - enough to build one shared
 * `Thumb` mapping instead of three near-identical copies across the library, search and collection screens. */
interface ThumbSource {
  id: string;
  type: string;
  status: string;
  title: string;
  updatedAt: string;
}

/** `imageUrl` is a signed thumbnail link from `useThumbnailLinks` (batched per page, not fetched here) - omitted or
 * `null` falls back to the large file-type icon `ThumbnailGrid` draws for a card without an image (also used for types with no thumbnail rendition, such as audio, and when the image fails to load). */
export function toAssetThumb(t: TFunction, locale: string, asset: ThumbSource, imageUrl?: string | null): Thumb {
  return {
    id: asset.id,
    title: asset.title,
    subtitle: t(`library.type.${asset.type}`),
    icon: <FileTypeIcon type={asset.type} size={14} weight="bold" aria-hidden="true" />,
    meta: t('library.updatedRelative', { time: relativeTime(asset.updatedAt, locale) }),
    badge: t(`library.status.${asset.status}`),
    badgeTone: statusTone(asset.status),
    imageUrl: hasThumbnail(asset.type) ? (imageUrl ?? undefined) : undefined,
    placeholder: <FileTypeIcon type={asset.type} size={40} weight="light" aria-hidden="true" />,
  };
}
