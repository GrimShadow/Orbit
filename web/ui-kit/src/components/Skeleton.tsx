export function Skeleton({ width = '100%', height = 16 }: { width?: number | string; height?: number | string }) {
  return <span className="dam-skeleton" aria-hidden="true" style={{ width, height }} />;
}

/** Announce loading once to assistive tech; render skeleton blocks visually. */
export function LoadingRegion({ label = 'Loading', children }: { label?: string; children: React.ReactNode }) {
  return (
    <div role="status" aria-live="polite">
      <span className="dam-sr-only">{label}</span>
      {children}
    </div>
  );
}
