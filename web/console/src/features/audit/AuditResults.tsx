import { CaretRight } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { IconButton, Table, type Column } from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import type { AuditEntry } from './auditApi';
import { entityRoute, shortId } from './auditFormat';

/** The audit entries as a table. A row opens the detail drawer on click; the explicit "View details" button at the end
 * of each row is the keyboard and screen-reader route to the same thing. */
export function AuditResults({
  entries,
  loading,
  onOpen,
  onFilterActor,
}: {
  entries: AuditEntry[];
  loading: boolean;
  onOpen: (entry: AuditEntry) => void;
  onFilterActor: (entry: AuditEntry) => void;
}) {
  const { t, i18n } = useTranslation();
  const lang = i18n.language;

  const columns: Column<AuditEntry>[] = [
    {
      key: 'time',
      header: t('audit.col.time'),
      render: (e) => (
        <time
          dateTime={e.occurredAt}
          className="dam-audittime"
          title={new Date(e.occurredAt).toLocaleString(lang, { dateStyle: 'full', timeStyle: 'long' })}
        >
          <span className="dam-audittime__rel">{relativeTime(e.occurredAt, lang)}</span>
          <span className="dam-audittime__abs">
            {new Date(e.occurredAt).toLocaleString(lang, { dateStyle: 'medium', timeStyle: 'medium' })}
          </span>
        </time>
      ),
    },
    {
      key: 'actor',
      header: t('audit.col.actor'),
      render: (e) => (
        <button
          type="button"
          className="dam-auditactor"
          title={t('audit.actorTitle', { id: e.actorUserId ?? e.actorId })}
          aria-label={t('audit.filterByActor', { name: e.actorName || e.actorId })}
          onClick={() => onFilterActor(e)}
        >
          {e.actorName || e.actorId}
        </button>
      ),
    },
    {
      key: 'action',
      header: t('audit.col.action'),
      render: (e) => <code className="dam-auditaction">{e.action}</code>,
    },
    {
      key: 'entity',
      header: t('audit.col.entity'),
      render: (e) => {
        if (!e.entityType) return <span className="dam-auditfield__sub">{t('audit.none')}</span>;
        const route = entityRoute(e.entityType, e.entityId);
        const id = e.entityId ? (
          <code className="dam-auditentity__id" title={e.entityId}>
            {shortId(e.entityId)}
          </code>
        ) : null;
        return (
          <span className="dam-auditentity">
            <span className="dam-auditentity__type">{e.entityType}</span>
            {route ? (
              <Link to={route} className="dam-auditlink" title={t('audit.detail.open', { type: e.entityType })}>
                {id}
              </Link>
            ) : (
              id
            )}
          </span>
        );
      },
    },
    {
      key: 'ip',
      header: t('audit.col.ip'),
      className: 'dam-col--hide-sm',
      render: (e) =>
        e.ip ? <code className="dam-auditentity__id">{e.ip}</code> : <span className="dam-auditfield__sub">–</span>,
    },
    {
      key: 'view',
      header: <span className="dam-sr-only">{t('audit.col.view')}</span>,
      className: 'dam-auditview',
      render: (e) => (
        <IconButton
          label={t('audit.viewDetails', { action: e.action, time: new Date(e.occurredAt).toLocaleString(lang) })}
          size="sm"
          data-entry-id={e.id}
          onClick={() => onOpen(e)}
        >
          <CaretRight size={16} weight="bold" aria-hidden="true" />
        </IconButton>
      ),
    },
  ];

  const openFromRow = (target: EventTarget) => {
    const el = target as HTMLElement;
    if (el.closest('a, button, input, summary')) return; // those do their own thing
    const id = el.closest('tr')?.querySelector<HTMLElement>('[data-entry-id]')?.dataset.entryId;
    const entry = entries.find((x) => x.id === id);
    if (entry) onOpen(entry);
  };

  return (
    // Whole-row click is a pointer convenience; the "View details" button in every row is the accessible control.
    // eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-static-element-interactions
    <div className="dam-auditresults" onClick={(ev) => openFromRow(ev.target)}>
      <Table columns={columns} rows={entries} getRowId={(e) => e.id} caption={t('audit.caption')} loading={loading} />
    </div>
  );
}
