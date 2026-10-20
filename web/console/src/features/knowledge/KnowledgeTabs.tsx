import { useTranslation } from 'react-i18next';
import { NavLink } from 'react-router-dom';
import { useMe } from '../../api/me';

/** The knowledge pages as a row of tabs. They are separate routes, so this is a navigation landmark of links (the current one
 * carries `aria-current="page"`) rather than an ARIA tablist - the same pattern as the Insights tabs. The VIN rules tab is only for
 * people who may see the rules (`knowledge.manage` or `assets.update`). */
export function KnowledgeTabs() {
  const { t } = useTranslation();
  const me = useMe();
  const permissions = me.data?.permissions ?? [];
  const canSeeRules = permissions.includes('knowledge.manage') || permissions.includes('assets.update');
  const tabs = [
    { to: '/knowledge', end: true, labelKey: 'knowledge.tabs.documents', show: true },
    { to: '/knowledge/vin-rules', end: false, labelKey: 'knowledge.tabs.rules', show: canSeeRules },
    { to: '/knowledge/resolver', end: false, labelKey: 'knowledge.tabs.resolver', show: true },
  ].filter((tab) => tab.show);
  return (
    <nav className="dam-kntabs" aria-label={t('knowledge.tabs.label')}>
      {tabs.map((tab) => (
        <NavLink key={tab.to} to={tab.to} end={tab.end} className="dam-kntabs__tab">
          {t(tab.labelKey)}
        </NavLink>
      ))}
    </nav>
  );
}
