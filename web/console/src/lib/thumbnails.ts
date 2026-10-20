/** Asset types for which the server makes no "thumb" rendition (audio has nothing to draw), so there is no thumbnail link
 * to ask for and a card shows the file-type icon straight away. Every other type is asked for one; if its rendition is
 * missing or fails to load, the image falls back to the same icon (see `ImageWithFallback`). */
const NO_THUMBNAIL = new Set(['audio']);

export const hasThumbnail = (type: string): boolean => !NO_THUMBNAIL.has(type);

/** The ids of the items worth asking `POST /assets/links/batch` about: skips types without a thumbnail rendition. */
export function thumbnailIds(items: { id: string; type: string }[]): string[] {
  return items.filter((i) => hasThumbnail(i.type)).map((i) => i.id);
}
