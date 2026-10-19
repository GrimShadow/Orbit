export interface LogoProps {
  /** Glyph height in px; the wordmark scales with it. */
  size?: number;
  /** Show the "Orbit" wordmark next to the glyph. */
  wordmark?: boolean;
  /** Accessible name; the default names the product. Pass '' when a visible label sits beside it. */
  label?: string;
}

/** The product mark: a planet with a tilted ring and a small moon, drawn in the brand colour. The ring passes behind
 * the planet at the top and in front at the bottom. Mirrors `logo.svg` (the favicon source). */
export function Logo({ size = 24, wordmark = true, label = 'Orbit' }: LogoProps) {
  return (
    <span className="dam-logo">
      <svg
        className="dam-logo__glyph"
        width={size}
        height={size}
        viewBox="0 0 32 32"
        fill="none"
        role={label ? 'img' : undefined}
        aria-label={label || undefined}
        aria-hidden={label ? undefined : true}
      >
        {/* back half of the ring */}
        <path
          d="M4.74 22.5A13 6 -30 0 1 27.26 9.5"
          stroke="currentColor"
          strokeWidth="2.2"
          strokeLinecap="round"
          opacity="0.55"
        />
        <circle cx="16" cy="16" r="5.5" fill="currentColor" />
        {/* front half of the ring, drawn over the planet */}
        <path d="M27.26 9.5A13 6 -30 0 1 4.74 22.5" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" />
        <circle cx="27.6" cy="11.7" r="2.4" fill="currentColor" />
      </svg>
      {wordmark && (
        <span className="dam-logo__word" style={{ fontSize: size * 0.85 }} aria-hidden={label ? true : undefined}>
          Orbit
        </span>
      )}
    </span>
  );
}
