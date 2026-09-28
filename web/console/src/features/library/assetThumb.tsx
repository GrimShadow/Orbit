import type { TFunction } from 'i18next';
import { relativeTime } from '../../lib/relativeTime';
import { FileTypeIcon, type Thumb } from '../../ui';

/** The fields every asset-like list item has in common (`AssetDto`, `SearchHitDto`) - enough to build one shared
 * `Thumb` mapping instead of three near-identical copies across the library, search and collection screens. */
interface ThumbSource {
  id: string;
  type: string;
  status: string;
  title: string;
  updatedAt: string;
}

export function toAssetThumb(t: TFunction, locale: string, asset: ThumbSource): Thumb {
  return {
    id: asset.id,
    title: asset.title,
    subtitle: t(`library.type.${asset.type}`),
    icon: <FileTypeIcon type={asset.type} size={14} weight="bold" aria-hidden="true" />,
    meta: t('library.updatedRelative', { time: relativeTime(asset.updatedAt, locale) }),
    badge: t(`library.status.${asset.status}`),
  };
}
