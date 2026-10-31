import { useTranslation } from 'react-i18next';
import { useLocation } from 'react-router-dom';
import { SETTINGS_GROUPS, sectionForPath } from './settingsModel';

/** The eyebrow a Settings page shows above its own title ("Settings · Automation"), so a re-homed page reads as part of
 * the area. Undefined outside `/settings`, which leaves a page rendered on its own unchanged. */
export function useSettingsEyebrow(): string | undefined {
  const { t } = useTranslation();
  const { pathname } = useLocation();
  const section = sectionForPath(pathname);
  if (!section) return undefined;
  const group = SETTINGS_GROUPS.find((g) => g.id === section.group);
  return group ? `${t('settings.title')} · ${t(group.labelKey)}` : t('settings.title');
}
