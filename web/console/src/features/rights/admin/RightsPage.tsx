import { SealCheck } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { ApiError } from '../../../api/client';
import { Button, EmptyState, Modal, PageHeader, Plus, Skeleton, WarningCircle, useToast } from '../../../ui';
import { useDocumentTitle } from '../../library/useDocumentTitle';
import { RightsApiError, isForbidden, useDeleteRights, useRightsList, useSaveRights, type Rights } from '../rightsApi';
import { RightsForm, toBody, type SavedInfo } from './RightsForm';
import { RightsList, Summary, Toolbar } from './RightsList';
import { copyName, countByStatus, filterRights, isStatus } from './rightsLabels';
import './rights.css';
import { useSettingsEyebrow } from '../../settings/useSettingsEyebrow';

/** `/settings/rights`: licences and release terms that narrow, time-limit or watermark the assets they are assigned to. */
export function RightsPage() {
  const { t } = useTranslation();
  const { push } = useToast();
  useDocumentTitle(t('rights.title'));
  const eyebrow = useSettingsEyebrow();
  const [params, setParams] = useSearchParams();
  const query = useRightsList();
  const save = useSaveRights();
  const remove = useDeleteRights();
  const [form, setForm] = useState<{ rights: Rights | null; key: number } | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const [toDelete, setToDelete] = useState<Rights | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);

  const rawStatus = params.get('status');
  const status = isStatus(rawStatus) ? rawStatus : null;
  // The search box keeps its own text so typing never waits on the router; the URL follows it (and is where it starts).
  const [q, setQ] = useState(params.get('q') ?? '');
  const setFilter = (next: { status?: string | null; q?: string }) =>
    setParams(
      (prev) => {
        const p = new URLSearchParams(prev);
        const apply = (key: string, value: string | null | undefined) => (value ? p.set(key, value) : p.delete(key));
        if ('status' in next) apply('status', next.status);
        if ('q' in next) apply('q', next.q);
        return p;
      },
      { replace: true },
    );
  const clearFilters = () => {
    setQ('');
    setFilter({ status: null, q: '' });
  };

  const all = query.data ?? [];
  const shown = filterRights(all, { status, q });
  const forbidden = isForbidden(query.error);
  const filtered = status !== null || q.trim() !== '';

  // A new key each time the panel opens, so its form starts from the record it is opened for.
  const edit = (rights: Rights | null) => {
    setForm((f) => ({ rights, key: (f?.key ?? 0) + 1 }));
    setFormOpen(true);
  };

  const saved = ({ rights, created }: SavedInfo) => {
    setFormOpen(false);
    push({
      title: t(created ? 'rights.created' : 'rights.saved', { name: rights.name }),
      // More than 500 assigned assets are not all re-derived in the request; the hourly job finishes the rest.
      message: rights.pendingAssets > 0 ? t('rights.pending', { count: rights.pendingAssets }) : undefined,
      tone: 'success',
    });
  };

  const duplicate = (r: Rights) =>
    save.mutate(
      {
        body: {
          ...toBody({
            name: copyName(
              r.name,
              all.map((x) => x.name),
              t('rights.duplicateSuffix'),
            ),
            licenceType: r.licenceType,
            licensor: r.licensor,
            channels: r.channels,
            regions: r.regions,
            validFrom: r.validFrom ?? '',
            validTo: r.validTo ?? '',
            watermarkRequired: r.watermarkRequired,
            notes: r.notes,
          }),
        },
      },
      {
        onSuccess: (created) => push({ title: t('rights.duplicated', { name: created.name }), tone: 'success' }),
        onError: (e) =>
          push({
            title: t('rights.duplicateFailed'),
            message: e instanceof ApiError ? e.detail || e.title : undefined,
            tone: 'error',
          }),
      },
    );

  const closeDelete = () => {
    setToDelete(null);
    setRefusal(null);
  };

  const newButton = (
    <Button variant="primary" onClick={() => edit(null)}>
      <Plus size={16} weight="bold" aria-hidden="true" />
      {t('rights.new')}
    </Button>
  );

  let body;
  if (query.isPending) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('rights.loading')}</span>
        <div className="dam-stack dam-stack--fill" aria-hidden="true">
          <Skeleton height={76} />
          <Skeleton height={150} />
          <Skeleton height={150} />
        </div>
      </div>
    );
  } else if (forbidden) {
    body = (
      <EmptyState
        icon={<WarningCircle size={28} />}
        title={t('rights.forbidden.title')}
        description={t('rights.forbidden.description')}
      />
    );
  } else if (query.error) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('rights.loadFailed')}
          description={t('rights.loadFailedHint')}
          action={<Button onClick={() => void query.refetch()}>{t('rights.retry')}</Button>}
        />
      </div>
    );
  } else if (all.length === 0) {
    body = (
      <EmptyState
        icon={<SealCheck size={28} />}
        title={t('rights.empty.title')}
        description={t('rights.empty.description')}
        action={newButton}
      />
    );
  } else {
    body = (
      <>
        <Summary counts={countByStatus(all)} />
        <Toolbar
          q={q}
          status={status}
          onQ={(v) => {
            setQ(v);
            setFilter({ q: v });
          }}
          onStatus={(v) => setFilter({ status: v })}
        />
        <p className="dam-hint dam-rcount" role="status">
          {filtered ? t('rights.showing', { shown: shown.length, total: all.length }) : t('rights.count', { count: all.length })}
        </p>
        {shown.length === 0 ? (
          <EmptyState
            icon={<SealCheck size={28} />}
            title={t('rights.noMatch.title')}
            description={t('rights.noMatch.description')}
            action={<Button onClick={clearFilters}>{t('rights.noMatch.clear')}</Button>}
            titleAs="h2"
          />
        ) : (
          <RightsList rights={shown} busy={save.isPending} onEdit={edit} onDuplicate={duplicate} onDelete={setToDelete} />
        )}
      </>
    );
  }

  return (
    <div className="dam-page dam-rpage">
      <PageHeader
        title={t('rights.title')}
        eyebrow={eyebrow}
        description={t('rights.description')}
        actions={forbidden || query.isPending || all.length === 0 ? undefined : newButton}
      />
      {body}

      <RightsForm
        key={form?.key ?? 0}
        open={formOpen}
        rights={form?.rights ?? null}
        onClose={() => setFormOpen(false)}
        onSaved={saved}
      />

      <Modal open={toDelete !== null} title={t('rights.delete.title')} closeLabel={t('rights.close')} onClose={closeDelete}>
        <div className="dam-stack dam-dialogform dam-rdlg">
          <p>{t('rights.delete.body', { name: toDelete?.name })}</p>
          {toDelete && toDelete.documentCount > 0 && (
            <p className="dam-hint">{t('rights.delete.documents', { count: toDelete.documentCount })}</p>
          )}
          {refusal && (
            <p role="alert" className="dam-rrefusal">
              <WarningCircle size={16} aria-hidden="true" />
              <span>{refusal}</span>
            </p>
          )}
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={closeDelete}>
              {t('rights.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={remove.isPending}
              onClick={() => {
                setRefusal(null);
                remove.mutate(toDelete!.id, {
                  onSuccess: () => {
                    const name = toDelete!.name;
                    closeDelete();
                    push({ title: t('rights.deleted', { name }), tone: 'success' });
                  },
                  onError: (e) => {
                    if (e instanceof RightsApiError && e.status === 409) {
                      setRefusal(
                        e.assetCount !== undefined ? t('rights.delete.inUse', { count: e.assetCount }) : e.detail || e.title,
                      );
                    } else {
                      closeDelete();
                      push({
                        title: t('rights.deleteFailed'),
                        message: e instanceof ApiError ? e.detail || e.title : undefined,
                        tone: 'error',
                      });
                    }
                  },
                });
              }}
            >
              {t('rights.menu.delete')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
