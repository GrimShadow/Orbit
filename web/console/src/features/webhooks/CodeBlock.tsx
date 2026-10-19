/** A scrollable block of code or JSON. Focusable so keyboard users can scroll it, and named so a screen reader knows what it is. */
export function CodeBlock({ label, children }: { label: string; children: string }) {
  return (
    // A scrollable region must be reachable by keyboard (axe: scrollable-region-focusable), hence the tab stop.
    // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex
    <pre className="dam-whcode" role="region" aria-label={label} tabIndex={0}>
      <code>{children}</code>
    </pre>
  );
}
