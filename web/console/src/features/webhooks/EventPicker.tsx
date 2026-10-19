import { useId, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Info, TagInput } from '../../ui';
import type { WebhookEventType } from './webhooksApi';

/** True when an event name is covered by a pattern: `*`, a `prefix.*` or the exact name (the server's rule). */
export function patternMatches(pattern: string, name: string): boolean {
  if (pattern === '*') return true;
  if (pattern.endsWith('.*')) return name.startsWith(pattern.slice(0, -1));
  return pattern === name;
}

/** `asset.*`-style prefix patterns the catalogue supports: one per first name segment shared by at least two events. */
export function prefixWildcards(events: WebhookEventType[]) {
  const byPrefix = new Map<string, string[]>();
  for (const e of events) {
    const prefix = e.name.split('.')[0]!;
    byPrefix.set(prefix, [...(byPrefix.get(prefix) ?? []), e.name]);
  }
  return [...byPrefix.entries()]
    .filter(([, names]) => names.length >= 2)
    .map(([prefix, names]) => ({ prefix, pattern: `${prefix}.*`, names }));
}

/** Events grouped by category, in catalogue order; each group carries the wildcards whose first event it holds. */
function groupByCategory(events: WebhookEventType[]) {
  const wildcards = prefixWildcards(events);
  const groups = new Map<string, WebhookEventType[]>();
  for (const e of events) groups.set(e.category, [...(groups.get(e.category) ?? []), e]);
  return [...groups.entries()].map(([category, items]) => ({
    category,
    items,
    wildcards: wildcards.filter((w) => items.some((i) => i.name === w.names[0])),
  }));
}

interface EventPickerProps {
  events: WebhookEventType[];
  value: string[];
  onChange: (next: string[]) => void;
  error?: string;
}

/**
 * Chooses which events a webhook or chat channel receives. The value is a list of server patterns: `*` (everything),
 * `category.*` (a whole group, including events added later), exact names, and any custom prefix such as
 * `workflow.task.*`. Checkboxes show what each pattern already covers, so nothing is ticked twice.
 */
export function EventPicker({ events, value, onChange, error }: EventPickerProps) {
  const { t } = useTranslation();
  const id = useId();
  const groups = useMemo(() => groupByCategory(events), [events]);
  const all = value.includes('*');
  const names = new Set(events.map((e) => e.name));
  const wildcards = new Set(groups.flatMap((g) => g.wildcards.map((w) => w.pattern)));
  const custom = value.filter((p) => p !== '*' && !names.has(p) && !wildcards.has(p));
  const covered = (name: string) => value.some((p) => p !== name && patternMatches(p, name));

  const setAll = (on: boolean) => onChange(on ? ['*'] : []);
  const toggleName = (name: string, on: boolean) => onChange(on ? [...value, name] : value.filter((p) => p !== name));
  const toggleWildcard = (w: { pattern: string; names: string[] }, on: boolean) => {
    if (!on) return onChange(value.filter((p) => p !== w.pattern));
    // The wildcard now covers those exact names, so they are dropped from the list.
    onChange([...value.filter((p) => !w.names.includes(p)), w.pattern]);
  };
  const categoryLabel = (c: string) => t(`webhooks.category.${c}`, { defaultValue: c.charAt(0).toUpperCase() + c.slice(1) });

  return (
    <div className="dam-whevents" aria-describedby={error ? `${id}-error` : undefined}>
      <div className="dam-whevents__group dam-whevents__group--all">
        <label className="dam-check">
          <input type="checkbox" className="dam-check__input" checked={all} onChange={(e) => setAll(e.target.checked)} />
          <span className="dam-check__label">
            {t('webhooks.events.all')}
            <span className="dam-hint dam-whevents__sub">{t('webhooks.events.allHint')}</span>
          </span>
        </label>
      </div>

      {groups.map((g) => (
        <fieldset key={g.category} className="dam-whevents__group" disabled={all}>
          <legend className="dam-whevents__legend">{categoryLabel(g.category)}</legend>
          <ul className="dam-whevents__list">
            {g.wildcards.map((w) => (
              <li key={w.pattern}>
                <label className="dam-check">
                  <input
                    type="checkbox"
                    className="dam-check__input"
                    checked={all || value.includes(w.pattern)}
                    onChange={(e) => toggleWildcard(w, e.target.checked)}
                  />
                  <span className="dam-check__label">
                    {t('webhooks.events.wholeCategory', { category: w.prefix })}
                    <code className="dam-whevents__pattern">{w.pattern}</code>
                  </span>
                </label>
              </li>
            ))}
            {g.items.map((e) => {
              const isCovered = all || covered(e.name);
              return (
                <li key={e.name}>
                  <label className="dam-check">
                    <input
                      type="checkbox"
                      className="dam-check__input"
                      checked={isCovered || value.includes(e.name)}
                      disabled={isCovered}
                      onChange={(ev) => toggleName(e.name, ev.target.checked)}
                    />
                    <span className="dam-check__label">
                      <code className="dam-whevents__name">{e.name}</code>
                      {e.derived && (
                        <span
                          className="dam-whevents__derived"
                          role="img"
                          title={t('webhooks.events.derivedHint')}
                          aria-label={t('webhooks.events.derivedHint')}
                        >
                          <Info size={14} aria-hidden="true" />
                        </span>
                      )}
                      <span className="dam-hint dam-whevents__sub">{e.description}</span>
                    </span>
                  </label>
                </li>
              );
            })}
          </ul>
        </fieldset>
      ))}

      <details className="dam-whevents__custom" open={custom.length > 0 || undefined}>
        <summary>{t('webhooks.events.custom')}</summary>
        <TagInput
          label={t('webhooks.events.customLabel')}
          hint={t('webhooks.events.customHint')}
          placeholder="workflow.task.*"
          value={custom}
          disabled={all}
          listLabel={t('webhooks.events.customList')}
          removeLabel={(p) => t('webhooks.events.removePattern', { pattern: p })}
          onChange={(next) => onChange([...value.filter((p) => !custom.includes(p)), ...next])}
        />
      </details>

      {error && (
        <span id={`${id}-error`} className="dam-error" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}
