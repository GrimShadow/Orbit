import {
  CaretDoubleLeft,
  CaretDoubleRight,
  BookOpenText,
  ChartLineUp,
  CheckSquareOffset,
  FlowArrow,
  House,
  Images,
  ListMagnifyingGlass,
  PlugsConnected,
  SealCheck,
  Sparkle,
  MagnifyingGlass,
  Stack,
  UploadSimple,
  WebhooksLogo,
  type Icon,
} from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { NavLink } from 'react-router-dom';
import { useMe } from '../api/me';
import { Brand } from './Brand';
import { NavCount, useTasksBadge } from './NavBadge';

export interface NavItem {
  to: string;
  end?: boolean;
  labelKey: string;
  Icon: Icon;
}

/** The product's primary sections, in the order they appear in the sidebar and the phone tab bar. */
export const NAV_ITEMS: NavItem[] = [
  { to: '/', end: true, labelKey: 'nav.home', Icon: House },
  { to: '/library', labelKey: 'nav.library', Icon: Images },
  { to: '/search', labelKey: 'nav.search', Icon: MagnifyingGlass },
  { to: '/collections', labelKey: 'nav.collections', Icon: Stack },
  { to: '/tasks', labelKey: 'nav.tasks', Icon: CheckSquareOffset },
  { to: '/upload', labelKey: 'nav.upload', Icon: UploadSimple },
];

/** Sections shown in the sidebar only to people holding the named permission. Insights links to `/insights`, which opens Key
 * results; the link stays current on all three of its pages because a non-`end` link matches by prefix. */
export const ADMIN_ITEMS: (NavItem & { permission: string })[] = [
  { to: '/insights', labelKey: 'nav.insights', Icon: ChartLineUp, permission: 'analytics.read' },
  { to: '/admin/workflows', labelKey: 'nav.workflows', Icon: FlowArrow, permission: 'workflow.manage' },
  { to: '/admin/ai', labelKey: 'nav.ai', Icon: Sparkle, permission: 'ai.manage' },
  { to: '/admin/glossary', labelKey: 'nav.glossary', Icon: BookOpenText, permission: 'glossary.manage' },
  { to: '/admin/rights', labelKey: 'nav.rights', Icon: SealCheck, permission: 'rights.manage' },
  { to: '/integrations', labelKey: 'nav.integrations', Icon: PlugsConnected, permission: 'integrations.manage' },
  { to: '/admin/webhooks', labelKey: 'nav.webhooks', Icon: WebhooksLogo, permission: 'webhooks.manage' },
  { to: '/admin/audit', labelKey: 'nav.audit', Icon: ListMagnifyingGlass, permission: 'audit.read' },
];

interface SidebarProps {
  collapsed: boolean;
  onToggle: () => void;
}

/** Desktop navigation rail (Canva / AEM style): brand, the primary sections with icons, the tenant, and a collapse
 * toggle. Collapsed it keeps the icons and exposes each label through the link's title/aria-label. */
export function Sidebar({ collapsed, onToggle }: SidebarProps) {
  const { t } = useTranslation();
  const { data: me } = useMe();
  const tasksBadge = useTasksBadge();
  return (
    <aside className={`dam-sidebar${collapsed ? ' dam-sidebar--collapsed' : ''}`} aria-label={t('shell.sidebar')}>
      <div className="dam-sidebar__brand">
        <Brand wordmark={!collapsed} />
      </div>
      <nav aria-label={t('nav.primary')} className="dam-sidenav">
        {NAV_ITEMS.map(({ to, end, labelKey, Icon }) => {
          const badge = to === '/tasks' ? tasksBadge : null;
          return (
            <NavLink
              key={to}
              to={to}
              end={end}
              className="dam-sidenav__item"
              title={collapsed ? t(labelKey) : undefined}
              aria-label={badge ? badge.name : collapsed ? t(labelKey) : undefined}
            >
              <Icon size={20} weight="regular" aria-hidden="true" className="dam-sidenav__icon" />
              <span className="dam-sidenav__label">{t(labelKey)}</span>
              {badge && <NavCount badge={badge} />}
            </NavLink>
          );
        })}
        {ADMIN_ITEMS.filter((i) => me?.permissions.includes(i.permission)).map(({ to, end, labelKey, Icon }) => (
          <NavLink
            key={to}
            to={to}
            end={end}
            className="dam-sidenav__item"
            title={collapsed ? t(labelKey) : undefined}
            aria-label={collapsed ? t(labelKey) : undefined}
          >
            <Icon size={20} weight="regular" aria-hidden="true" className="dam-sidenav__icon" />
            <span className="dam-sidenav__label">{t(labelKey)}</span>
          </NavLink>
        ))}
      </nav>
      <div className="dam-sidebar__foot">
        {me && (
          <div className="dam-sidebar__tenant" title={t('shell.defaultTenant', { id: me.tenantId.slice(0, 8) })}>
            <span className="dam-sidebar__tenant-dot" aria-hidden="true" />
            <span className="dam-sidenav__label">{t('shell.defaultTenant', { id: me.tenantId.slice(0, 8) })}</span>
          </div>
        )}
        <button
          type="button"
          className="dam-sidebar__toggle"
          onClick={onToggle}
          aria-label={collapsed ? t('shell.expand') : t('shell.collapse')}
          title={collapsed ? t('shell.expand') : t('shell.collapse')}
          aria-pressed={collapsed}
        >
          {collapsed ? (
            <CaretDoubleRight size={16} weight="bold" aria-hidden="true" />
          ) : (
            <CaretDoubleLeft size={16} weight="bold" aria-hidden="true" />
          )}
          <span className="dam-sidenav__label">{t('shell.collapse')}</span>
        </button>
      </div>
    </aside>
  );
}

/** Phone navigation: the same sections as a fixed bottom tab bar. */
export function MobileTabBar() {
  const { t } = useTranslation();
  const tasksBadge = useTasksBadge();
  return (
    <nav aria-label={t('shell.mobileNav')} className="dam-tabbar">
      {NAV_ITEMS.map(({ to, end, labelKey, Icon }) => {
        const badge = to === '/tasks' ? tasksBadge : null;
        return (
          <NavLink key={to} to={to} end={end} className="dam-tabbar__item" aria-label={badge?.name}>
            <span className="dam-tabbar__iconwrap">
              <Icon size={22} aria-hidden="true" className="dam-tabbar__icon" />
              {badge && <NavCount badge={badge} />}
            </span>
            <span>{t(labelKey)}</span>
          </NavLink>
        );
      })}
    </nav>
  );
}
