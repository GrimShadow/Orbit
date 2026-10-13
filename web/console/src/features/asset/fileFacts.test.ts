import { aspectRatio, formatBitrate, formatBytes, formatDuration } from '../../lib/format';
import type { AssetDto, AssetVersionDto } from './assetApi';
import { buildFileFacts, formatLabel, summaryLine } from './fileFacts';

const t = (key: string, o?: Record<string, unknown>) => (o ? `${key}:${JSON.stringify(o)}` : key);
const asset = { createdAt: '2026-01-01T10:00:00Z', updatedAt: '2026-01-02T10:00:00Z' } as AssetDto;
const version = (over: Omit<Partial<AssetVersionDto>, 'technicalMetadata'> & { technicalMetadata?: unknown }): AssetVersionDto =>
  ({
    id: 'v1',
    versionNo: 2,
    isCurrent: true,
    sizeBytes: 416047,
    mime: 'image/jpeg',
    sha256: 'a'.repeat(64),
    changeNote: '',
    createdAt: '',
    technicalMetadata: {},
    ...over,
  }) as AssetVersionDto;
const get = (groups: ReturnType<typeof buildFileFacts>, id: string, label: string) =>
  groups.find((g) => g.id === id)?.facts.find((f) => f.label === label)?.value;

describe('formatters', () => {
  it('formats sizes, durations, bitrates and aspect ratios', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(416047)).toBe('406.3 KB');
    expect(formatBytes(5 * 1024 * 1024)).toBe('5.0 MB');
    expect(formatDuration(83.4)).toBe('1:23');
    expect(formatDuration(3725)).toBe('1:02:05');
    expect(formatBitrate(4_500_000)).toBe('4.5 Mbps');
    expect(formatBitrate(128_000)).toBe('128 kbps');
    expect(aspectRatio(1920, 1080)).toBe('16:9');
    expect(aspectRatio(400, 300)).toBe('4:3');
    expect(aspectRatio(1000, 707)).toBe('1.41:1');
  });
  it('labels a MIME type by its subtype', () => {
    expect(formatLabel('image/jpeg')).toBe('JPEG');
    expect(formatLabel('application/vnd.openxmlformats-officedocument.wordprocessingml.document')).toContain('vnd.openxml');
  });
});

describe('buildFileFacts', () => {
  it('describes an image: size, dimensions, megapixels, aspect, colour space, checksum, scan', () => {
    const v = version({
      technicalMetadata: {
        file: { fileName: 'hero.jpg' },
        scan: { verdict: 'clean', engine: 'clamav' },
        image: { width: 1920, height: 1080, hasAlpha: false, exif: { 'Color Space': 'sRGB', Orientation: 'Top', Make: 'Acme' } },
      },
    });
    const g = buildFileFacts(asset, v, 'en', t);
    expect(get(g, 'file', 'fileName')).toBe('hero.jpg');
    expect(get(g, 'file', 'size')).toBe('406.3 KB');
    expect(get(g, 'file', 'checksum')).toBe('a'.repeat(16) + '…');
    expect(get(g, 'file', 'scan')).toBe('asset.facts.scanClean (clamav)');
    expect(get(g, 'image', 'dimensions')).toBe('1920 × 1080 px');
    expect(get(g, 'image', 'megapixels')).toBe('2.1 MP');
    expect(get(g, 'image', 'aspectRatio')).toBe('16:9');
    expect(get(g, 'image', 'colorSpace')).toBe('sRGB');
    expect(get(g, 'camera', 'Make')).toBe('Acme');
    expect(g.find((x) => x.id === 'camera')?.facts.some((f) => f.label === 'Orientation')).toBe(false);
    expect(summaryLine(v)).toBe('JPEG · 1920 × 1080 · 406.3 KB');
  });

  it('describes a video with its audio track', () => {
    const v = version({
      mime: 'video/mp4',
      technicalMetadata: {
        media: {
          formatName: 'mov,mp4',
          durationSeconds: 83.4,
          bitRate: 4_500_000,
          streams: [
            { type: 'video', codec: 'h264', width: 1920, height: 1080, frameRate: 29.97 },
            { type: 'audio', codec: 'aac', channels: 2, sampleRate: 48000 },
          ],
        },
      },
    });
    const g = buildFileFacts(asset, v, 'en', t);
    expect(get(g, 'video', 'duration')).toBe('1:23');
    expect(get(g, 'video', 'resolution')).toBe('1920 × 1080 px');
    expect(get(g, 'video', 'frameRate')).toBe('29.97 fps');
    expect(get(g, 'video', 'codec')).toBe('H264');
    expect(get(g, 'audio', 'sampleRate')).toBe('48 kHz');
    expect(get(g, 'audio', 'channels')).toBe('asset.facts.stereo');
  });

  it('describes a PDF page count and size in millimetres, and omits facts that have no value', () => {
    const v = version({
      mime: 'application/pdf',
      technicalMetadata: { pdf: { pages: 12, pageWidthPt: 595, pageHeightPt: 842, author: 'Acme' } },
    });
    const g = buildFileFacts(asset, v, 'en', t);
    expect(get(g, 'document', 'pages')).toBe('12');
    expect(get(g, 'document', 'pageSize')).toBe('210 × 297 mm');
    expect(get(g, 'document', 'author')).toBe('Acme');
    expect(get(g, 'file', 'fileName')).toBeUndefined();
    expect(g.map((x) => x.id)).toEqual(['file', 'document']);
  });
});
