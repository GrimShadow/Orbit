# ADR-0042: Admin Settings area

Status: accepted. Changes none of D1-D15. Console only: no backend, API or SDK change.

## Context

The console's administrative pages were seven separate items in the sidebar (Workflows, AI, Glossary, Rights, Integrations, Webhooks, Audit log), each
shown to people holding that page's permission, and spread over two URL schemes (`/admin/...` and `/integrations`). New administrative pages kept
adding sidebar items (Integrations, with PADS4 and other connections, was the latest), and below 1024px the sidebar is hidden and the phone tab bar lists
only six primary sections, so none of the administrative pages could be reached on a phone or a narrow window. The request: one place for
administrators, where connections and integrations (PADS4 first), the audit log and the like live.

## Decisions

1. **One Settings area at `/settings`** with its own layout (react-router nested routes and `<Outlet/>`): a section list and, beside it, the section's
   own page. Each section is the existing page, unchanged, re-homed. Insights (analytics) is not administration and stays in the main navigation;
   Tasks and the product's primary sections are unchanged.
2. **Sections, groups and permissions** (one table, `web/console/src/features/settings/settingsModel.ts`, which the sidebar, the account menu, the router and
   the layout all read):

   | Group | Section | URL | Permission |
   |---|---|---|---|
   | Connections | Integrations | `/settings/integrations` | `integrations.manage` |
   | Security and compliance | Audit log | `/settings/audit` | `audit.read` |
   | Automation | Webhooks | `/settings/webhooks` | `webhooks.manage` |
   | Automation | Workflows (and the designer, `/settings/workflows/:id`) | `/settings/workflows` | `workflow.manage` |
   | Automation | AI | `/settings/ai` | `ai.manage` |
   | Content rules | Glossary | `/settings/glossary` | `glossary.manage` |
   | Content rules | Rights | `/settings/rights` | `rights.manage` |

   These are the permissions the sidebar already used for each page. New administrative pages are added by adding a row.
3. **Who sees what.** The Settings entry (one gear item in the sidebar where the administrative items were, and a link in the account menu so the area
   is reachable on narrow screens) and the `/settings` area appear only to a signed-in person holding at least one section permission
   (`canUseSettings`). Inside, each section link appears only when its permission is held. `/settings` redirects to the first section the person may
   use (`firstSettingsPath`), and shows a calm "nothing to manage" state if there is none. A section URL opened without its permission shows a
   forbidden state inside the area (the person's own sections stay listed, and the section's page is never mounted, so it makes no requests); it does
   not redirect, so a pasted link says what is wrong instead of silently landing elsewhere. The server still enforces every permission: the console
   check is about what to show.
4. **URLs.** `/admin/ai`, `/admin/glossary`, `/admin/rights`, `/admin/webhooks`, `/admin/audit`, `/admin/workflows`, `/admin/workflows/:id` and
   `/integrations` redirect (replacing the history entry, keeping the query string and hash) to the matching `/settings/...` URL. Links written by the
   backend (emails and notifications) point to `/tasks`, `/insights/reports/...` and `/library/assets/...`, not to these pages, so none needed changing;
   bookmarks and links in earlier ADRs keep working through the redirects. Console links to the old URLs were updated to the new ones.
5. **Layout.** From 1024px a vertical, grouped list (icons and labels) on the left; below it the same list becomes a horizontally scrolling row above
   the content, with the group names kept for screen readers. The workflow designer, a canvas editor with its own back link, takes the full width and
   drops the section list. The section pages keep their own headings; they gain an eyebrow ("Settings · Automation") so a re-homed page reads as part
   of the area, without a second page title.
6. **Translations.** English and Hindi (`settings.en.json`, `settings.hi.json`); the Hindi coverage test includes the `settings` namespace.

## Alternatives considered

- **Keep flat admin items in the sidebar.** No change, but the sidebar grows with every administrative page, the pages stay unreachable on narrow
  screens, and nothing tells a person that these pages belong together.
- **Gate the area by role name ("Admin").** Simple, but it contradicts how the product authorises everything else (permissions, custom roles,
  access rules), and a role rename or a custom administrator role would break it. The permission-based rule is also what the server enforces.
- **A single landing page with cards.** Pretty, but a click more than a redirect for the common case, and the section list is needed on every page
  anyway to move between sections.
- **Redirect to the first allowed section on a forbidden URL.** Rejected: silently showing a different page than the one asked for is confusing.
- **A "General" tenant-settings page.** Out of scope; nothing needs it yet.

## Consequences

- Built-in roles: Admin holds all seven section permissions and sees every section. Of the other built-in roles only **Librarian** holds one
  (`rights.manage`), so a Librarian sees the Settings entry with a single section, Rights. Editor, Approver, Contributor, Viewer, Dealer and ApiClient hold
  none and see no Settings. The permission-based result is what ships, so a custom role or a granted permission shows the matching section too.
- Before this change a Librarian saw Rights in the sidebar; now it sits under Settings, one click deeper.
- A person with `notifications.manage` but not `webhooks.manage` could open the Webhooks page by URL to use its "Chat channels" tab; the sidebar never
  listed it for them. Under Settings the Webhooks section needs `webhooks.manage`, so that tab is no longer reachable for them. Only Admin holds
  `notifications.manage` among the built-in roles. If this matters for a custom role, the section can accept either permission (a one-row change).
- The administrative pages load after the person's permissions have loaded (one request, already cached for the sidebar), so each opens a moment
  later than a bare page did.
- Later administrative pages (users and roles, access rules, taxonomy) have an obvious home.
