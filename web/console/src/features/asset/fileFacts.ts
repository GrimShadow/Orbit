import { aspectRatio, formatBitrate, formatBytes, formatDuration } from '../../lib/format';
import type { AssetDto, AssetVersionDto } from './assetApi';

export interface Fact {
  /** Suffix of the `asset.facts.*` translation key used as the row label. */
  label: string;
  value: string;
  /** Long opaque values (checksums) render in a monospace face. */
  mono?: boolean;
  /** Full value shown on hover when `value` is shortened. */
  title?: string;
}
export interface FactGroup {
  /** Suffix of the `asset.factGroups.*` translation key. */
  id: 'file' | 'image' | 'video' | 'audio' | 'document' | 'camera';
  facts: Fact[];
}

type Translate = (key: string, options?: Record<string, unknown>) => string;
type Json = Record<string, unknown>;

const obj = (v: unknown): Json | undefined => (v && typeof v === 'object' && !Array.isArray(v) ? (v as Json) : undefined);
const num = (v: unknown): number | undefined => (typeof v === 'number' && Number.isFinite(v) ? v : undefined);
const str = (v: unknown): string | undefined => (typeof v === 'string' && v.trim() ? v : undefined);

/** "image/jpeg" -> "JPEG", "application/pdf" -> "PDF"; anything with a vendor prefix falls back to the full MIME type. */
export function formatLabel(mime: string): string {
  const sub = mime.split('/')[1] ?? mime;
  return /^[a-z0-9]{2,5}$/i.test(sub) ? sub.toUpperCase() : mime;
}

/** One line of the most useful facts for under the page title: "JPEG - 1920 x 1080 - 406.3 KB". */
export function summaryLine(version: AssetVersionDto): string {
  const tech = obj(version.technicalMetadata) ?? {};
  const image = obj(tech.image);
  const video = obj(tech.media) && streams(obj(tech.media)!).find((s) => s.type === 'video');
  const w = num(image?.width) ?? num(video && video.width);
  const h = num(image?.height) ?? num(video && video.height);
  return [formatLabel(version.mime), w && h ? `${w} × ${h}` : undefined, formatBytes(version.sizeBytes)]
    .filter(Boolean)
    .join(' · ');
}

function streams(media: Json): Json[] {
  return Array.isArray(media.streams) ? media.streams.map((s) => obj(s)).filter((s): s is Json => !!s) : [];
}

/** The facts worth showing about an asset's current file, grouped by what they describe. Everything comes from data the
 * ingest pipeline already stored (`AssetVersionDto` + its `technicalMetadata`); a fact with no value is simply omitted. */
export function buildFileFacts(asset: AssetDto, version: AssetVersionDto, locale: string, t: Translate): FactGroup[] {
  const tech = obj(version.technicalMetadata) ?? {};
  const date = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' });
  const add = (facts: Fact[], label: string, value: string | undefined, extra: Partial<Fact> = {}) => {
    if (value) facts.push({ label, value, ...extra });
  };

  const file: Fact[] = [];
  add(file, 'fileName', str(obj(tech.file)?.fileName));
  add(file, 'type', `${formatLabel(version.mime)} (${version.mime})`);
  add(file, 'size', formatBytes(version.sizeBytes));
  add(file, 'created', date.format(new Date(asset.createdAt)));
  add(file, 'modified', date.format(new Date(asset.updatedAt)));
  add(file, 'checksum', version.sha256 ? version.sha256.slice(0, 16) + '…' : undefined, { mono: true, title: version.sha256 });
  const scan = obj(tech.scan);
  if (str(scan?.verdict)) {
    const verdict = scan!.verdict as string;
    const label = verdict === 'clean' ? t('asset.facts.scanClean') : verdict;
    add(file, 'scan', str(scan?.engine) ? `${label} (${scan!.engine as string})` : label);
  }
  const groups: FactGroup[] = [{ id: 'file', facts: file }];

  const image = obj(tech.image);
  if (image) {
    const facts: Fact[] = [];
    const w = num(image.width);
    const h = num(image.height);
    if (w && h) {
      add(facts, 'dimensions', `${w} × ${h} px`);
      add(facts, 'megapixels', `${((w * h) / 1_000_000).toFixed(1)} MP`);
      add(facts, 'aspectRatio', aspectRatio(w, h));
    }
    const exif = obj(image.exif) ?? {};
    // EXIF's way of saying "not specified" is not a fact worth showing.
    const space = str(exif['Color Space']);
    add(facts, 'colorSpace', space && !/^(undefined|uncalibrated|unknown|none)$/i.test(space.trim()) ? space : undefined);
    if (typeof image.hasAlpha === 'boolean') add(facts, 'transparency', t(image.hasAlpha ? 'asset.facts.yes' : 'asset.facts.no'));
    groups.push({ id: 'image', facts });

    const camera = Object.entries(exif)
      .filter(([k, v]) => k !== 'Color Space' && k !== 'Orientation' && str(v))
      .slice(0, 8)
      .map(([k, v]): Fact => ({ label: k, value: v as string }));
    if (camera.length > 0) groups.push({ id: 'camera', facts: camera });
  }

  const media = obj(tech.media);
  if (media) {
    const all = streams(media);
    const video = all.find((s) => s.type === 'video');
    const audio = all.find((s) => s.type === 'audio');
    const common: Fact[] = [];
    const duration = num(media.durationSeconds);
    add(common, 'duration', duration !== undefined ? formatDuration(duration) : undefined);
    add(common, 'container', str(media.formatName));
    const bitRate = num(media.bitRate);
    add(common, 'bitRate', bitRate ? formatBitrate(bitRate) : undefined);
    if (video) {
      const vw = num(video.width);
      const vh = num(video.height);
      add(common, 'resolution', vw && vh ? `${vw} × ${vh} px` : undefined);
      const fps = num(video.frameRate);
      add(common, 'frameRate', fps ? `${Number(fps.toFixed(2))} fps` : undefined);
      add(common, 'codec', str(video.codec)?.toUpperCase());
    }
    groups.push({ id: video ? 'video' : 'audio', facts: common });
    if (audio && video) {
      const af: Fact[] = [];
      add(af, 'codec', str(audio.codec)?.toUpperCase());
      const ch = num(audio.channels);
      add(
        af,
        'channels',
        ch === 1
          ? t('asset.facts.mono')
          : ch === 2
            ? t('asset.facts.stereo')
            : ch
              ? t('asset.facts.channelCount', { n: ch })
              : undefined,
      );
      const sr = num(audio.sampleRate);
      add(af, 'sampleRate', sr ? `${Number((sr / 1000).toFixed(1))} kHz` : undefined);
      groups.push({ id: 'audio', facts: af });
    } else if (audio) {
      const target = groups.at(-1)!.facts;
      add(target, 'codec', str(audio.codec)?.toUpperCase());
      const ch = num(audio.channels);
      add(
        target,
        'channels',
        ch === 1
          ? t('asset.facts.mono')
          : ch === 2
            ? t('asset.facts.stereo')
            : ch
              ? t('asset.facts.channelCount', { n: ch })
              : undefined,
      );
      const sr = num(audio.sampleRate);
      add(target, 'sampleRate', sr ? `${Number((sr / 1000).toFixed(1))} kHz` : undefined);
    }
  }

  const pdf = obj(tech.pdf);
  const doc = obj(tech.document);
  if (pdf || doc) {
    const facts: Fact[] = [];
    const src = pdf ?? doc!;
    const pages = num(src.pages) ?? (str(src.pages) ? Number(src.pages) : undefined);
    add(facts, 'pages', pages ? String(pages) : undefined);
    add(facts, 'slides', str(src.slides));
    add(facts, 'words', str(src.words));
    const pw = num(src.pageWidthPt);
    const ph = num(src.pageHeightPt);
    // PDF user units are points (1/72 inch).
    add(facts, 'pageSize', pw && ph ? `${Math.round((pw * 25.4) / 72)} × ${Math.round((ph * 25.4) / 72)} mm` : undefined);
    add(facts, 'author', str(src.author));
    add(facts, 'lastModifiedBy', str(src.lastModifiedBy));
    add(facts, 'application', str(src.application) ?? str(src.producer));
    groups.push({ id: 'document', facts });
  }

  return groups.filter((g) => g.facts.length > 0);
}
