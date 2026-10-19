import { useRecordUsage } from '../analytics/analyticsApi';
import { ArrowSquareOut, CaretLeft, CaretRight, Copy, Play } from '@phosphor-icons/react';
import { useCallback, useEffect, useState, type KeyboardEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { relativeTime } from '../../lib/relativeTime';
import { useCommentsUi } from '../comments/CommentsContext';
import { AnnotationLayer } from '../comments/annotations/AnnotationLayer';
import { AnnotationTools } from '../comments/annotations/AnnotationTools';
import { TimecodeTools } from '../comments/annotations/TimecodeTools';
import { TimelineMarkers } from '../comments/annotations/TimelineMarkers';
import { VideoCaption } from '../comments/annotations/VideoCaption';
import { useImageAnnotations } from '../comments/annotations/useImageAnnotations';
import { useTimecodeAnnotations } from '../comments/annotations/useTimecodeAnnotations';
import {
  EmptyState,
  FileTypeIcon,
  IconButton,
  Info,
  LoadingRegion,
  SegmentedControl,
  Skeleton,
  useToast,
  WarningCircle,
} from '../../ui';
import { useAssetRenditions, useDeliveryLink } from './assetApi';

interface AssetPreviewProps {
  assetId: string;
  type: string;
  title: string;
  status: string;
}

type Zoom = 'fit' | 'actual';

/** The preview stage: a neutral, viewport-sized surface the asset is shown on, with a small floating toolbar. Renditions
 * are watermarked by the delivery origin itself while the asset is unpublished (ADR-0006) - the console never needs to
 * know or show that separately.
 *
 * What is asked for depends on the kind of asset and on whether it is published, because the delivery origin rejects the
 * *entire* link request with a 409 when it is asked for something an unpublished asset may not have (ADR-0006/0007):
 * `original` (its bytes cannot be shared unwatermarked at all) and every video transcode (watermarking moving pictures is
 * not implemented) - and, for the same reason, every `pdf_pages-{n}` raster. So an unpublished image or document asks for the
 * watermarked `preview` only (page 1 of a document), and an unpublished video for its `poster` still; `original`, the
 * playable transcodes and the per-page rasters join once it is published. */
export function AssetPreview({ assetId, type, title, status }: AssetPreviewProps) {
  const published = status === 'published';
  switch (type) {
    case 'image':
      return <ImageStage assetId={assetId} title={title} published={published} />;
    case 'video':
      return <VideoStage assetId={assetId} title={title} published={published} />;
    case 'document':
    case 'manual':
      return <PagedStage assetId={assetId} title={title} published={published} />;
    default:
      return <OtherStage assetId={assetId} type={type} published={published} />;
  }
}

// ---- shared frame -------------------------------------------------------------------------------------------------

function Stage({
  children,
  toolbar,
  note,
  zoom,
  onKeyDown,
  onStageKeyDown,
  below,
  docked,
}: {
  children: ReactNode;
  toolbar?: ReactNode;
  note?: string;
  zoom?: Zoom;
  /** Keys pressed on the canvas itself (paging). */
  onKeyDown?: (e: KeyboardEvent<HTMLDivElement>) => void;
  /** Keys pressed anywhere in the stage, toolbar included (annotation shortcuts, Escape). */
  onStageKeyDown?: (e: KeyboardEvent<HTMLElement>) => void;
  /** A strip under the canvas (the video timeline markers). */
  below?: ReactNode;
  /** The toolbar sits under the canvas and strip rather than floating over the picture. */
  docked?: boolean;
}) {
  const { t } = useTranslation();
  return (
    // eslint-disable-next-line jsx-a11y/no-static-element-interactions -- shortcuts for the focus inside the stage; every action also has a button
    <div className="dam-stage" data-zoom={zoom} data-docked={docked || undefined} onKeyDown={onStageKeyDown}>
      {/* The canvas scrolls at 100%, so it is a focusable region (keyboard users can pan it with the arrow keys). */}
      {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions, jsx-a11y/no-noninteractive-tabindex -- a scrollable region must be focusable (axe: scrollable-region-focusable) */}
      <div className="dam-stage__canvas" role="region" aria-label={t('asset.stage.label')} tabIndex={0} onKeyDown={onKeyDown}>
        {children}
      </div>
      {below}
      {note && (
        <p className="dam-stage__note" role="note">
          <Info size={16} weight="fill" aria-hidden="true" />
          {note}
        </p>
      )}
      {toolbar && (
        <div className="dam-stage__toolbar" role="toolbar" aria-label={t('asset.stage.tools')}>
          {toolbar}
        </div>
      )}
    </div>
  );
}

function StageSkeleton() {
  const { t } = useTranslation();
  return (
    <div className="dam-stage">
      <LoadingRegion label={t('asset.previewLoading')}>
        <div className="dam-stage__loading">
          <Skeleton height="100%" />
        </div>
      </LoadingRegion>
    </div>
  );
}

function StageMessage({
  icon,
  title,
  description,
  action,
}: {
  icon: ReactNode;
  title: string;
  description?: string;
  action?: ReactNode;
}) {
  return (
    <div className="dam-stage">
      <div className="dam-stage__canvas dam-stage__canvas--message">
        <EmptyState icon={icon} title={title} description={description} action={action} titleAs="p" />
      </div>
    </div>
  );
}

/** Open-in-new-tab and copy-link, both pointing at the best delivery link there is (the original once published). */
function LinkTools({
  assetId,
  href,
  expiresAt,
  isOriginal,
}: {
  assetId: string;
  href: string | undefined;
  expiresAt: string | undefined;
  /** Only a link to the real file counts as use; a watermarked draft preview or poster is just looking. */
  isOriginal: boolean;
}) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const record = useRecordUsage();
  if (!href) return null;

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(href);
      if (isOriginal) record(assetId, 'share', 'original');
      push({
        title: t('asset.stage.linkCopied'),
        message: expiresAt ? t('asset.stage.linkExpires', { time: relativeTime(expiresAt, i18n.language) }) : undefined,
        tone: 'success',
      });
    } catch {
      push({ title: t('asset.stage.copyFailed'), tone: 'error' });
    }
  };

  return (
    <>
      <a
        className="dam-btn dam-btn--ghost dam-btn--icon dam-btn--sm"
        href={href}
        target="_blank"
        rel="noopener noreferrer"
        onClick={() => isOriginal && record(assetId, 'download', 'original')}
        aria-label={t('asset.stage.openNewTab')}
        title={t('asset.stage.openNewTab')}
      >
        <ArrowSquareOut size={16} weight="bold" aria-hidden="true" />
      </a>
      <IconButton size="sm" label={t('asset.stage.copyLink')} onClick={() => void copy()}>
        <Copy size={16} weight="bold" aria-hidden="true" />
      </IconButton>
    </>
  );
}

function ZoomTool({ zoom, onChange }: { zoom: Zoom; onChange: (z: Zoom) => void }) {
  const { t } = useTranslation();
  return (
    <SegmentedControl
      label={t('asset.stage.zoom')}
      value={zoom}
      onChange={(v) => onChange(v as Zoom)}
      options={[
        { value: 'fit', label: t('asset.stage.fit') },
        { value: 'actual', label: t('asset.stage.actual') },
      ]}
    />
  );
}

// ---- image --------------------------------------------------------------------------------------------------------

function ImageStage({
  assetId,
  title,
  published,
  note,
  page,
}: {
  assetId: string;
  title: string;
  published: boolean;
  /** Why this is less than the whole asset (shown quietly over the stage). */
  note?: string;
  /** A document page rather than a photo: no checkerboard, a paper-like shadow. */
  page?: boolean;
}) {
  const { t } = useTranslation();
  const linkQuery = useDeliveryLink(assetId, published ? ['preview', 'original'] : ['preview'], true);
  const [zoom, setZoom] = useState<Zoom>('fit');
  const [broken, setBroken] = useState(false);
  // A document shown as a single image (a draft, or no page rasters) is page 1.
  const [img, setImg] = useState<HTMLImageElement | null>(null);
  const ann = useImageAnnotations({ assetId, img, page: page ? 1 : undefined, zoom });

  if (linkQuery.isPending) return <StageSkeleton />;
  const links = linkQuery.data?.links;
  const src = links?.preview ?? links?.original;
  if (linkQuery.error || !src || broken) return <StageMessage icon={<WarningCircle />} title={t('asset.previewUnavailable')} />;

  return (
    <Stage
      zoom={zoom}
      note={note}
      onStageKeyDown={ann.onStageKeyDown}
      toolbar={
        <>
          <AnnotationTools ann={ann} />
          <ZoomTool zoom={zoom} onChange={setZoom} />
          <LinkTools
            assetId={assetId}
            href={links?.original ?? src}
            expiresAt={linkQuery.data?.expiresAt}
            isOriginal={Boolean(links?.original)}
          />
        </>
      }
    >
      <img
        ref={setImg}
        className={`dam-stage__img ${page ? 'dam-stage__img--page' : 'dam-stage__img--checker'}`}
        src={src}
        alt={title}
        onError={() => setBroken(true)}
      />
      <AnnotationLayer ann={ann} />
    </Stage>
  );
}

// ---- video --------------------------------------------------------------------------------------------------------

function VideoStage({ assetId, title, published }: { assetId: string; title: string; published: boolean }) {
  const { t } = useTranslation();
  // `mobile` is the full-length 720p transcode; `preview` is capped at a few seconds and only the fallback.
  const linkQuery = useDeliveryLink(assetId, published ? ['mobile', 'preview', 'poster', 'original'] : ['poster'], true);
  const [broken, setBroken] = useState(false);
  const [video, setVideo] = useState<HTMLVideoElement | null>(null);
  const tc = useTimecodeAnnotations(assetId, video);

  if (linkQuery.isPending) return <StageSkeleton />;
  const links = linkQuery.data?.links;
  const poster = links?.poster;
  const src = links?.mobile ?? links?.preview ?? links?.original;

  if (published && src && !linkQuery.error)
    return (
      <Stage
        docked
        onStageKeyDown={tc.onStageKeyDown}
        below={<TimelineMarkers tc={tc} />}
        toolbar={
          <>
            <TimecodeTools tc={tc} />
            <LinkTools
              assetId={assetId}
              href={links?.original ?? src}
              expiresAt={linkQuery.data?.expiresAt}
              isOriginal={Boolean(links?.original)}
            />
          </>
        }
      >
        {/* eslint-disable-next-line jsx-a11y/media-has-caption -- generated renditions carry no caption track (yet) */}
        <video ref={setVideo} className="dam-stage__video" src={src} poster={poster} controls preload="metadata">
          {t('asset.previewNoVideo')}
        </video>
        <VideoCaption tc={tc} />
      </Stage>
    );

  // Unpublished (or no playable rendition yet): the watermarked poster still, and a quiet note about why it does not play.
  if (linkQuery.error || !poster || broken)
    return (
      <StageMessage icon={<WarningCircle />} title={t('asset.previewUnavailable')} description={t('asset.stage.videoDraft')} />
    );
  return (
    <Stage
      note={t('asset.stage.videoDraft')}
      toolbar={
        <>
          {/* Video comments are timecodes, which need a player: say why the button is off. */}
          <TimecodeTools tc={tc} unavailable={t('annotations.needsPlayback')} />
          <LinkTools assetId={assetId} href={poster} expiresAt={linkQuery.data?.expiresAt} isOriginal={false} />
        </>
      }
    >
      <img className="dam-stage__img" src={poster} alt={title} onError={() => setBroken(true)} />
      <span className="dam-stage__play" aria-hidden="true">
        <Play size={28} weight="fill" />
      </span>
    </Stage>
  );
}

// ---- documents (PDF pages) ----------------------------------------------------------------------------------------

function PagedStage({ assetId, title, published }: { assetId: string; title: string; published: boolean }) {
  const { t } = useTranslation();
  const renditionsQuery = useAssetRenditions(assetId);
  if (renditionsQuery.isPending) return <StageSkeleton />;
  const pages = (renditionsQuery.data ?? [])
    .filter((r) => r.profile === 'pdf_pages' && (r.pageNo ?? 0) > 0)
    .map((r) => r.pageNo as number)
    .sort((a, b) => a - b);
  // Only a published document is served page by page: the delivery origin has no watermarked page rasters (ADR-0007), so a
  // draft shows its watermarked first page (`preview`). The same fallback covers a file with no page rasters at all (an
  // Office file, or ingest has not got to it yet).
  if (pages.length === 0 || !published)
    return (
      <ImageStage
        assetId={assetId}
        title={title}
        published={published}
        page
        note={!published && pages.length > 1 ? t('asset.stage.pdfDraft') : undefined}
      />
    );
  return <PdfStage assetId={assetId} title={title} pages={pages} />;
}

function PdfStage({ assetId, title, pages }: { assetId: string; title: string; pages: number[] }) {
  const { t } = useTranslation();
  const [index, setIndex] = useState(0);
  const [zoom, setZoom] = useState<Zoom>('fit');
  const [broken, setBroken] = useState(false);
  const pageName = (i: number) => `pdf_pages-${pages[i]}`;
  const linkQuery = useDeliveryLink(assetId, [pageName(index), 'original'], true);
  // Warm the next page's link so turning the page does not wait on a round trip.
  useDeliveryLink(assetId, [pageName(Math.min(index + 1, pages.length - 1))], pages.length > 1 && index < pages.length - 1);

  const go = (next: number) => {
    setBroken(false);
    setIndex(Math.min(Math.max(next, 0), pages.length - 1));
  };
  const ui = useCommentsUi();
  const { registerShowPage } = ui;
  const pagesKey = pages.join(',');
  const showPage = useCallback(
    (pageNo: number) => {
      const i = pagesKey.split(',').map(Number).indexOf(pageNo);
      if (i >= 0) {
        setBroken(false);
        setIndex(i);
      }
    },
    [pagesKey],
  );
  // Panel -> stage: a thread card jumps to its page.
  useEffect(() => {
    registerShowPage(showPage);
    return () => registerShowPage(null);
  }, [registerShowPage, showPage]);
  const [img, setImg] = useState<HTMLImageElement | null>(null);
  const ann = useImageAnnotations({ assetId, img, page: pages[index], zoom, onNavigate: showPage });
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.target !== e.currentTarget) return;
    if (e.key === 'ArrowRight' && zoom === 'fit') go(index + 1);
    else if (e.key === 'ArrowLeft' && zoom === 'fit') go(index - 1);
  };

  const pager = (
    <div className="dam-stage__pager">
      <IconButton size="sm" label={t('asset.stage.prevPage')} disabled={index === 0} onClick={() => go(index - 1)}>
        <CaretLeft size={16} weight="bold" aria-hidden="true" />
      </IconButton>
      <span className="dam-stage__pagelabel" aria-live="polite">
        {t('asset.stage.pageOf', { page: index + 1, total: pages.length })}
      </span>
      <IconButton size="sm" label={t('asset.stage.nextPage')} disabled={index === pages.length - 1} onClick={() => go(index + 1)}>
        <CaretRight size={16} weight="bold" aria-hidden="true" />
      </IconButton>
    </div>
  );

  if (linkQuery.isPending) return <StageSkeleton />;
  const links = linkQuery.data?.links;
  const src = links?.[pageName(index)];
  if (linkQuery.error || !src || broken)
    return (
      <Stage toolbar={pages.length > 1 ? pager : undefined}>
        <EmptyState icon={<WarningCircle />} title={t('asset.previewUnavailable')} titleAs="p" />
      </Stage>
    );

  return (
    <Stage
      zoom={zoom}
      onKeyDown={onKeyDown}
      onStageKeyDown={ann.onStageKeyDown}
      toolbar={
        <>
          {pages.length > 1 && pager}
          <AnnotationTools ann={ann} />
          <ZoomTool zoom={zoom} onChange={setZoom} />
          <LinkTools
            assetId={assetId}
            href={links?.original ?? src}
            expiresAt={linkQuery.data?.expiresAt}
            isOriginal={Boolean(links?.original)}
          />
        </>
      }
    >
      <img
        ref={setImg}
        className="dam-stage__img dam-stage__img--page"
        src={src}
        alt={t('asset.stage.pageAlt', { title, page: index + 1 })}
        onError={() => setBroken(true)}
      />
      <AnnotationLayer ann={ann} />
    </Stage>
  );
}

// ---- everything else ----------------------------------------------------------------------------------------------

function OtherStage({ assetId, type, published }: { assetId: string; type: string; published: boolean }) {
  const { t } = useTranslation();
  // No rendition exists for these kinds; the only thing to offer is the file itself, and only once it is published.
  const linkQuery = useDeliveryLink(assetId, ['original'], published);
  const original = linkQuery.data?.links.original;
  return (
    <StageMessage
      icon={<FileTypeIcon type={type} />}
      title={t('asset.stage.noPreview')}
      description={published ? undefined : t('asset.stage.noPreviewDraft')}
      action={
        original ? (
          <a className="dam-btn dam-btn--secondary" href={original} download>
            {t('asset.downloadOriginal')}
          </a>
        ) : undefined
      }
    />
  );
}
