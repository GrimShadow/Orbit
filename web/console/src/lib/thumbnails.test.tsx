import { render } from '@testing-library/react';
import { toAssetThumb } from '../features/library/assetThumb';
import { hasThumbnail, thumbnailIds } from './thumbnails';

describe('thumbnail availability', () => {
  it('knows audio has no thumbnail rendition and every other type is asked for one', () => {
    expect(hasThumbnail('audio')).toBe(false);
    for (const type of ['image', 'video', 'document', 'manual', '3d', 'html5', 'other']) expect(hasThumbnail(type)).toBe(true);
  });

  it('keeps only the ids worth requesting', () => {
    expect(
      thumbnailIds([
        { id: 'a', type: 'image' },
        { id: 'b', type: 'audio' },
        { id: 'c', type: 'document' },
      ]),
    ).toEqual(['a', 'c']);
  });
});

describe('toAssetThumb', () => {
  const t = ((k: string) => k) as unknown as Parameters<typeof toAssetThumb>[0];
  const base = { id: 'x', title: 'Acme clip', status: 'draft', updatedAt: '2026-01-01T00:00:00Z' };

  it('uses the link for an image and always carries the file-type icon placeholder', () => {
    const thumb = toAssetThumb(t, 'en', { ...base, type: 'image' }, 'https://cdn.test/x.webp');
    expect(thumb.imageUrl).toBe('https://cdn.test/x.webp');
    const { container } = render(<>{thumb.placeholder}</>);
    expect(container.querySelector('svg')).not.toBeNull();
  });

  it('never uses a link for audio, even if one is passed', () => {
    expect(toAssetThumb(t, 'en', { ...base, type: 'audio' }, 'https://cdn.test/404.webp').imageUrl).toBeUndefined();
  });
});
