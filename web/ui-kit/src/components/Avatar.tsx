export interface AvatarProps {
  /** The person's display name; the initials are derived from it. */
  name: string;
  size?: 'sm' | 'md' | 'lg';
  className?: string;
  /** Hide it from assistive tech - for when the person's name is already announced beside it (a menu button, a list row). */
  decorative?: boolean;
}

/** First letters of the first two words, upper-cased - works for "Priya Natarajan", "priya" and an e-mail address. */
export function initialsOf(name: string): string {
  const words = name
    .trim()
    .split(/[\s._@-]+/)
    .filter(Boolean);
  return words
    .slice(0, 2)
    .map((w) => w[0]!.toUpperCase())
    .join('');
}

/** Initials on a brand-tinted rounded square (a squircle, not a circle). */
export function Avatar({ name, size = 'md', className, decorative }: AvatarProps) {
  const a11y = decorative ? ({ 'aria-hidden': true } as const) : ({ role: 'img', 'aria-label': name } as const);
  return (
    <span className={['dam-avatar', className].filter(Boolean).join(' ')} data-size={size} {...a11y}>
      <span aria-hidden="true">{initialsOf(name)}</span>
    </span>
  );
}
