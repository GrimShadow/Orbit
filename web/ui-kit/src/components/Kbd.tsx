import type { ReactNode } from 'react';

/** A keyboard key, e.g. <Kbd>⌘</Kbd> <Kbd>K</Kbd>. */
export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="dam-kbd">{children}</kbd>;
}
