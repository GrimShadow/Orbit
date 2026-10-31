import {
  BookOpenText,
  FlowArrow,
  ListMagnifyingGlass,
  PlugsConnected,
  SealCheck,
  Sparkle,
  WebhooksLogo,
  type Icon,
} from '@phosphor-icons/react';

/** The Settings area (ADR-0042): the administrative pages, re-homed under `/settings`. This file is the one place that
 * says which sections exist, where they live and which permission each needs; the sidebar, the account menu, the
 * router and the layout all read it. */

export const SETTINGS_ROOT = '/settings';

export type SettingsGroupId = 'connections' | 'security' | 'automation' | 'content';

export interface SettingsGroup {
  id: SettingsGroupId;
  labelKey: string;
}

export interface SettingsSection {
  id: string;
  /** The section's URL; a section also owns everything beneath it (the workflow designer is `/settings/workflows/:id`). */
  to: string;
  labelKey: string;
  Icon: Icon;
  /** The permission that shows the section and lets its page be used. */
  permission: string;
  group: SettingsGroupId;
  /** A full-width working surface: the layout drops the section list so the page has the room. */
  focus?: (pathname: string) => boolean;
}

/** In the order they are listed. */
export const SETTINGS_GROUPS: readonly SettingsGroup[] = [
  { id: 'connections', labelKey: 'settings.group.connections' },
  { id: 'security', labelKey: 'settings.group.security' },
  { id: 'automation', labelKey: 'settings.group.automation' },
  { id: 'content', labelKey: 'settings.group.content' },
];

/** In the order they are listed; the first one a person may use is where `/settings` takes them. */
export const SETTINGS_SECTIONS: readonly SettingsSection[] = [
  {
    id: 'integrations',
    to: '/settings/integrations',
    labelKey: 'nav.integrations',
    Icon: PlugsConnected,
    permission: 'integrations.manage',
    group: 'connections',
  },
  {
    id: 'audit',
    to: '/settings/audit',
    labelKey: 'nav.audit',
    Icon: ListMagnifyingGlass,
    permission: 'audit.read',
    group: 'security',
  },
  {
    id: 'webhooks',
    to: '/settings/webhooks',
    labelKey: 'nav.webhooks',
    Icon: WebhooksLogo,
    permission: 'webhooks.manage',
    group: 'automation',
  },
  {
    id: 'workflows',
    to: '/settings/workflows',
    labelKey: 'nav.workflows',
    Icon: FlowArrow,
    permission: 'workflow.manage',
    group: 'automation',
    // The designer is a canvas editor with its own back link.
    focus: (pathname) => pathname.startsWith('/settings/workflows/'),
  },
  { id: 'ai', to: '/settings/ai', labelKey: 'nav.ai', Icon: Sparkle, permission: 'ai.manage', group: 'automation' },
  {
    id: 'glossary',
    to: '/settings/glossary',
    labelKey: 'nav.glossary',
    Icon: BookOpenText,
    permission: 'glossary.manage',
    group: 'content',
  },
  {
    id: 'rights',
    to: '/settings/rights',
    labelKey: 'nav.rights',
    Icon: SealCheck,
    permission: 'rights.manage',
    group: 'content',
  },
];

/** The URLs these pages had before Settings existed. Each redirects (keeping the query string and hash) to its new home. */
export const LEGACY_SETTINGS_PATHS: readonly { from: string; to: string }[] = [
  { from: '/integrations', to: '/settings/integrations' },
  { from: '/admin/audit', to: '/settings/audit' },
  { from: '/admin/webhooks', to: '/settings/webhooks' },
  { from: '/admin/workflows', to: '/settings/workflows' },
  { from: '/admin/workflows/:id', to: '/settings/workflows/:id' },
  { from: '/admin/ai', to: '/settings/ai' },
  { from: '/admin/glossary', to: '/settings/glossary' },
  { from: '/admin/rights', to: '/settings/rights' },
];

type Permissions = readonly string[] | undefined | null;

/** The sections a person with these permissions may use, in listing order. */
export function settingsSections(permissions: Permissions): SettingsSection[] {
  if (!permissions) return [];
  return SETTINGS_SECTIONS.filter((s) => permissions.includes(s.permission));
}

/** True when at least one section is usable: this is what shows the Settings entry and the area. */
export function canUseSettings(permissions: Permissions): boolean {
  return settingsSections(permissions).length > 0;
}

/** The sections a person may use, grouped for display; groups with nothing for this person are left out. */
export function settingsNavigation(permissions: Permissions): { group: SettingsGroup; sections: SettingsSection[] }[] {
  const allowed = settingsSections(permissions);
  return SETTINGS_GROUPS.map((group) => ({ group, sections: allowed.filter((s) => s.group === group.id) })).filter(
    (g) => g.sections.length > 0,
  );
}

/** Where `/settings` takes a person: the first section they may use, or null when there is none. */
export function firstSettingsPath(permissions: Permissions): string | null {
  return settingsSections(permissions)[0]?.to ?? null;
}

/** The section that owns a URL path (the section's own path or anything beneath it), if any. */
export function sectionForPath(pathname: string): SettingsSection | undefined {
  const path = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
  return SETTINGS_SECTIONS.find((s) => path === s.to || path.startsWith(`${s.to}/`));
}
