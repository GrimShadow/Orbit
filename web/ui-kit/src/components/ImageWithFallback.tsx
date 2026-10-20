import { useState, type ImgHTMLAttributes, type ReactNode } from 'react';

/** An `<img>` that is never shown broken: with no `src`, or once the browser fails to load it (a rendition that does not
 * exist, an expired link), it renders `fallback` instead - typically a file-type icon. A new `src` is tried afresh, so a
 * refreshed link recovers. Decorative by default (`alt=""`). */
export function ImageWithFallback({
  src,
  fallback,
  alt = '',
  onError,
  ...rest
}: Omit<ImgHTMLAttributes<HTMLImageElement>, 'src'> & { src?: string | null; fallback: ReactNode }) {
  const [failedSrc, setFailedSrc] = useState<string | null>(null);
  if (!src || failedSrc === src) return <>{fallback}</>;
  return (
    <img
      {...rest}
      src={src}
      alt={alt}
      onError={(e) => {
        setFailedSrc(src);
        onError?.(e);
      }}
    />
  );
}
