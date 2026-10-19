import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Button, Drawer } from '../../ui';
import type { AuditEntry } from './auditApi';
import { entityRoute, relatedAssetId } from './auditFormat';
import { CopyButton } from './CopyButton';
import { DiffView } from './DiffView';

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="dam-auditfield">
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  );
}

function Mono({ value, copyLabel }: { value: string; copyLabel: string }) {
  return (
    <span className="dam-auditmono">
      <code className="dam-auditmono__text">{value}</code>
      <CopyButton value={value} label={copyLabel} />
    </span>
  );
}

/** Everything recorded about one audit entry, with the before/after diff. A native dialog underneath, so Escape closes
 * it, focus stays inside while it is open and the page behind is inert. */
export function AuditDrawer({
  entry,
  onClose,
  onFilterActor,
}: {
  entry: AuditEntry | null;
  onClose: () => void;
  onFilterActor: (entry: AuditEntry) => void;
}) {
  const { t, i18n } = useTranslation();
  const lang = i18n.language;
  const e = entry;
  const route = e ? entityRoute(e.entityType, e.entityId) : null;
  const asset = e && e.entityType !== 'asset' ? (relatedAssetId(e.after) ?? relatedAssetId(e.before)) : null;
  const when = e ? new Date(e.occurredAt) : null;

  return (
    <Drawer open={e !== null} onClose={onClose} title={e?.action ?? t('audit.detail.title')} closeLabel={t('audit.detail.close')}>
      {e && when && (
        <div className="dam-auditdrawer">
          <dl className="dam-auditfields">
            <Field label={t('audit.col.time')}>
              <time dateTime={e.occurredAt}>{when.toLocaleString(lang, { dateStyle: 'full', timeStyle: 'medium' })}</time>
              <span className="dam-auditfield__sub">{e.occurredAt}</span>
            </Field>
            <Field label={t('audit.col.actor')}>
              <span>{e.actorName || e.actorId}</span>
              <span className="dam-auditfield__sub">{e.actorUserId ?? e.actorId}</span>
              <Button variant="ghost" size="sm" onClick={() => onFilterActor(e)}>
                {t('audit.detail.filterActor')}
              </Button>
            </Field>
            <Field label={t('audit.col.entity')}>
              {e.entityType ? (
                <>
                  <span>{e.entityType}</span>
                  {e.entityId && (
                    <Mono value={e.entityId} copyLabel={t('audit.detail.copy', { what: t('audit.detail.entityId') })} />
                  )}
                  {route && (
                    <Link to={route} className="dam-auditlink">
                      {t('audit.detail.open', { type: e.entityType })}
                    </Link>
                  )}
                </>
              ) : (
                <span className="dam-auditfield__sub">{t('audit.none')}</span>
              )}
            </Field>
            {asset && (
              <Field label={t('audit.detail.relatedAsset')}>
                <Link to={`/library/assets/${asset}`} className="dam-auditlink">
                  {t('audit.detail.open', { type: 'asset' })}
                </Link>
              </Field>
            )}
            <Field label={t('audit.col.ip')}>{e.ip ?? <span className="dam-auditfield__sub">{t('audit.none')}</span>}</Field>
          </dl>

          <h3 className="dam-auditdrawer__h">{t('audit.detail.changes')}</h3>
          <DiffView before={e.before} after={e.after} />

          <h3 className="dam-auditdrawer__h">{t('audit.detail.integrity')}</h3>
          <dl className="dam-auditfields">
            <Field label={t('audit.detail.chainNo')}>
              <span className="dam-auditfield__num">#{e.chainNo}</span>
            </Field>
            <Field label={t('audit.detail.entryId')}>
              <Mono value={e.id} copyLabel={t('audit.detail.copy', { what: t('audit.detail.entryId') })} />
            </Field>
            <Field label={t('audit.detail.hash')}>
              <Mono value={e.hash} copyLabel={t('audit.detail.copy', { what: t('audit.detail.hash') })} />
            </Field>
            <Field label={t('audit.detail.prevHash')}>
              <Mono value={e.prevHash} copyLabel={t('audit.detail.copy', { what: t('audit.detail.prevHash') })} />
            </Field>
          </dl>
          <p className="dam-auditdrawer__note">{t('audit.detail.integrityNote')}</p>
        </div>
      )}
    </Drawer>
  );
}
