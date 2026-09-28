import { useTranslation } from 'react-i18next';
import { LoadingRegion, Skeleton } from '../../ui';
import { useDeliveryLink } from './assetApi';

interface AssetPreviewProps {
  assetId: string;
  type: string;
  title: string;
}

/** A preview player for image/video/PDF (spec Step 1.8: "preview player for image/video/PDF"); anything else falls back
 * to a plain download link. Renditions are watermarked by the delivery origin itself while the asset is unpublished
 * (ADR-0006) - the console never needs to know or show that separately. */
export function AssetPreview({ assetId, type, title }: AssetPreviewProps) {
  const { t } = useTranslation();
  const linkQuery = useDeliveryLink(assetId, ['preview', 'original'], true);

  if (linkQuery.isPending)
    return (
      <LoadingRegion label={t('asset.previewLoading')}>
        <Skeleton height={320} />
      </LoadingRegion>
    );
  if (linkQuery.error || !linkQuery.data) return <p className="dam-hint">{t('asset.previewUnavailable')}</p>;

  const src = linkQuery.data.links.preview ?? linkQuery.data.links.original;
  if (!src) return <p className="dam-hint">{t('asset.previewUnavailable')}</p>;

  switch (type) {
    case 'image':
      return <img className="dam-asset-preview__image" src={src} alt={title} />;
    case 'video':
      return (
        // eslint-disable-next-line jsx-a11y/media-has-caption -- generated renditions carry no caption track (yet)
        <video className="dam-asset-preview__video" src={src} controls>
          {t('asset.previewNoVideo')}
        </video>
      );
    case 'document':
    case 'manual':
      return <iframe className="dam-asset-preview__frame" src={src} title={title} />;
    default:
      return (
        <a className="dam-btn dam-btn--secondary" href={src} download>
          {t('asset.downloadOriginal')}
        </a>
      );
  }
}
