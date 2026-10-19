import { useEffect } from 'react';

/** Sets `document.title` to "<page> - Orbit" while the page is mounted and restores the previous title on the way out. */
export function useDocumentTitle(page: string | undefined) {
  useEffect(() => {
    if (!page) return;
    const previous = document.title;
    document.title = `${page} - Orbit`;
    return () => {
      document.title = previous;
    };
  }, [page]);
}
