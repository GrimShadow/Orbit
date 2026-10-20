import { useTranslation } from 'react-i18next';
import { NavLink, useSearchParams } from 'react-router-dom';
import { parseDays } from './useInsightsPeriod';

export const INSIGHTS_TABS = [
  { to: '/insights/kpis', labelKey: 'insights.tabs.kpis', keepDays: true },
  { to: '/insights/unused', labelKey: 'insights.tabs.unused', keepDays: false },
  { to: '/insights/reports', labelKey: 'insights.tabs.reports', keepDays: true },
] as const;

/** The three Insights pages as a row of tabs. They are separate routes, so this is a navigation landmark of links (the
 * current one carries `aria-current="page"`) rather than an ARIA tablist; it looks like the ui-kit tabs. The reporting window
 * travels with the link between Key results and Reports. */
export function InsightsTabs() {
  const { t } = useTranslation();
  const [params] = useSearchParams();
  const days = parseDays(params.get('days'));
  return (
    <nav className="dam-instabs" aria-label={t('insights.tabs.label')}>
      {INSIGHTS_TABS.map((tab) => (
        <NavLink
          key={tab.to}
          to={{ pathname: tab.to, search: tab.keepDays && days ? `?days=${days}` : '' }}
          className="dam-instabs__tab"
        >
          {t(tab.labelKey)}
        </NavLink>
      ))}
    </nav>
  );
}
