import {
  LEGACY_SETTINGS_PATHS,
  SETTINGS_GROUPS,
  SETTINGS_SECTIONS,
  canUseSettings,
  firstSettingsPath,
  sectionForPath,
  settingsNavigation,
  settingsSections,
} from './settingsModel';

const PERMISSIONS = SETTINGS_SECTIONS.map((s) => s.permission);

describe('the section table', () => {
  it('gives every section its own permission, its own URL under /settings and a known group', () => {
    expect(new Set(PERMISSIONS).size).toBe(SETTINGS_SECTIONS.length);
    expect(new Set(SETTINGS_SECTIONS.map((s) => s.to)).size).toBe(SETTINGS_SECTIONS.length);
    for (const s of SETTINGS_SECTIONS) {
      expect(s.to).toBe(`/settings/${s.id}`);
      expect(SETTINGS_GROUPS.map((g) => g.id)).toContain(s.group);
    }
  });

  it('uses the permissions the sidebar used for these pages before Settings existed', () => {
    const byId = Object.fromEntries(SETTINGS_SECTIONS.map((s) => [s.id, s.permission]));
    expect(byId).toEqual({
      integrations: 'integrations.manage',
      audit: 'audit.read',
      webhooks: 'webhooks.manage',
      workflows: 'workflow.manage',
      ai: 'ai.manage',
      glossary: 'glossary.manage',
      rights: 'rights.manage',
    });
  });

  it('groups the sections as Connections, Security and compliance, Automation and Content rules', () => {
    const groups = Object.fromEntries(
      SETTINGS_GROUPS.map((g) => [g.id, SETTINGS_SECTIONS.filter((s) => s.group === g.id).map((s) => s.id)]),
    );
    expect(groups).toEqual({
      connections: ['integrations'],
      security: ['audit'],
      automation: ['webhooks', 'workflows', 'ai'],
      content: ['glossary', 'rights'],
    });
  });
});

describe('settingsSections', () => {
  it.each(SETTINGS_SECTIONS.map((s) => [s.id, s.permission] as const))(
    'shows exactly the %s section for %s',
    (id, permission) => {
      expect(settingsSections([permission]).map((s) => s.id)).toEqual([id]);
    },
  );

  it('shows nothing without a section permission, however many others are held', () => {
    expect(settingsSections([])).toEqual([]);
    expect(settingsSections(undefined)).toEqual([]);
    expect(settingsSections(null)).toEqual([]);
    expect(settingsSections(['assets.read', 'assets.update', 'analytics.read', 'notifications.manage', 'users.manage'])).toEqual(
      [],
    );
  });

  it('shows every section to someone who holds every permission, in listing order', () => {
    expect(settingsSections([...PERMISSIONS].reverse()).map((s) => s.id)).toEqual(SETTINGS_SECTIONS.map((s) => s.id));
  });
});

describe('canUseSettings', () => {
  it('is true with any one section permission', () => {
    for (const p of PERMISSIONS) expect(canUseSettings(['assets.read', p])).toBe(true);
  });

  it('is false without one, and before permissions have loaded', () => {
    expect(canUseSettings(['assets.read', 'analytics.read'])).toBe(false);
    expect(canUseSettings([])).toBe(false);
    expect(canUseSettings(undefined)).toBe(false);
  });
});

describe('settingsNavigation', () => {
  it('keeps only the groups that hold a section for this person', () => {
    const nav = settingsNavigation(['webhooks.manage', 'rights.manage']);
    expect(nav.map((g) => [g.group.id, g.sections.map((s) => s.id)])).toEqual([
      ['automation', ['webhooks']],
      ['content', ['rights']],
    ]);
  });

  it('is empty without any section permission', () => {
    expect(settingsNavigation(['assets.read'])).toEqual([]);
  });
});

describe('firstSettingsPath', () => {
  it('is the first section the person may use, in listing order', () => {
    expect(firstSettingsPath(PERMISSIONS)).toBe('/settings/integrations');
    expect(firstSettingsPath(['rights.manage', 'ai.manage'])).toBe('/settings/ai');
    expect(firstSettingsPath(['rights.manage'])).toBe('/settings/rights');
  });

  it('is null when there is none', () => {
    expect(firstSettingsPath(['assets.read'])).toBeNull();
    expect(firstSettingsPath(undefined)).toBeNull();
  });
});

describe('sectionForPath', () => {
  it('finds the section that owns a path, including everything beneath it', () => {
    expect(sectionForPath('/settings/audit')?.id).toBe('audit');
    expect(sectionForPath('/settings/audit/')?.id).toBe('audit');
    expect(sectionForPath('/settings/workflows')?.id).toBe('workflows');
    expect(sectionForPath('/settings/workflows/w1')?.id).toBe('workflows');
    expect(sectionForPath('/settings/workflows/new')?.id).toBe('workflows');
  });

  it('finds none for the area root, an unknown path, or a path that merely starts the same way', () => {
    expect(sectionForPath('/settings')).toBeUndefined();
    expect(sectionForPath('/settings/unknown')).toBeUndefined();
    expect(sectionForPath('/settings/auditing')).toBeUndefined();
    expect(sectionForPath('/audit')).toBeUndefined();
  });

  it('drops the section list for the workflow designer only', () => {
    const wf = sectionForPath('/settings/workflows')!;
    expect(wf.focus?.('/settings/workflows')).toBe(false);
    expect(wf.focus?.('/settings/workflows/w1')).toBe(true);
    expect(sectionForPath('/settings/audit')!.focus).toBeUndefined();
  });
});

describe('the old URLs', () => {
  it('each map to a section URL (or the workflow designer) under /settings', () => {
    expect(LEGACY_SETTINGS_PATHS).toHaveLength(8);
    for (const { from, to } of LEGACY_SETTINGS_PATHS) {
      expect(from.startsWith('/settings')).toBe(false);
      expect(sectionForPath(to.replace(':id', 'x'))).toBeDefined();
    }
  });
});
