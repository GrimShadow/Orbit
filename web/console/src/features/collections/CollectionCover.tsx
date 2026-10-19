import { BookmarkSimple, MagnifyingGlass } from '../../ui';

/** A collection's cover: up to three of its assets' thumbnails as a mosaic (one large, two stacked), a single image
 * when there is one, or a quiet placeholder glyph (smart collections have no fixed members to pick from). */
export function CollectionCover({ images, smart }: { images: string[]; smart: boolean }) {
  if (images.length === 0)
    return (
      <div className="dam-cover dam-cover--empty" aria-hidden="true">
        {smart ? <MagnifyingGlass size={32} /> : <BookmarkSimple size={32} />}
      </div>
    );
  const shown = images.slice(0, 3);
  return (
    <div className={`dam-cover dam-cover--${shown.length}`} aria-hidden="true">
      {shown.map((src) => (
        <img key={src} src={src} alt="" loading="lazy" />
      ))}
    </div>
  );
}
