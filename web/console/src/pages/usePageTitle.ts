import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';

/** Sets the document title to "<page> - Orbit" (or just the product name when no page title is given), so browser
 * tabs, history and screen-reader page announcements tell pages apart. Pass the already-translated page name. */
export function usePageTitle(title: string | undefined) {
  const { t } = useTranslation();
  const product = t('app.name');
  useEffect(() => {
    document.title = title ? `${title} - ${product}` : product;
  }, [title, product]);
}
