import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

/** The Orbit mark: a planet with an elliptical orbit ring, in the brand colour, plus the wordmark. Drawn inline so it
 * follows the theme (currentColor) and needs no asset request. */
export function OrbitGlyph({ size = 28 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" fill="none" aria-hidden="true" focusable="false" className="dam-orbit">
      <circle cx="16" cy="16" r="6.5" fill="currentColor" />
      <ellipse cx="16" cy="16" rx="13.5" ry="5.5" stroke="currentColor" strokeWidth="2" transform="rotate(-28 16 16)" />
      <circle cx="27.2" cy="9.4" r="2.2" fill="currentColor" />
    </svg>
  );
}

export function Brand({ wordmark = true, to = '/' }: { wordmark?: boolean; to?: string }) {
  const { t } = useTranslation();
  return (
    <Link to={to} className="dam-brand" aria-label={t('app.name')}>
      <OrbitGlyph />
      {wordmark && <span className="dam-brand__word">{t('app.name')}</span>}
    </Link>
  );
}
