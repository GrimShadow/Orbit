import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { WarningCircle } from '@phosphor-icons/react';
import { Button, EmptyState, Modal, PageHeader, Skeleton, useToast } from '../../../ui';
import { relativeTime } from '../../../lib/relativeTime';
import { useDocumentTitle } from '../../library/useDocumentTitle';
import { useSettingsEyebrow } from '../../settings/useSettingsEyebrow';
import { useLeaveGuard } from '../../workflow/designer/useLeaveGuard';
import { AiApiError, isForbidden, useAiReindex, useAiSettings, useAiStatus, useSaveAiSettings, type AiSettings } from '../aiApi';
import {
  fieldErrors,
  languageInvalid,
  needsAcknowledgement,
  providerProblem,
  sameDraft,
  toDraft,
  toRequest,
  type Draft,
} from './aiAdminModel';
import { BackfillCard } from './BackfillCard';
import { FeaturesCard } from './FeaturesCard';
import { ProviderCard } from './ProviderCard';
import { StatusCard } from './StatusCard';
import { TestCard } from './TestCard';
import './aiAdmin.css';

/** While a rebuild or a backfill is going, look at the status this often (it otherwise refreshes every 15 s). */
const BUSY_POLL_MS = 4000;

/** `/settings/ai`: whether AI is on for the organisation, which provider does the work, what it does, and how far it has got. */
export function AiSettingsPage() {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const qc = useQueryClient();
  useDocumentTitle(t('aiAdmin.title'));
  const eyebrow = useSettingsEyebrow();

  const settings = useAiSettings();
  const status = useAiStatus();
  const save = useSaveAiSettings();
  const reindex = useAiReindex();

  // `draft` is null while nothing differs from what the server holds, so a background refetch never overwrites an edit
  // and a saved page simply shows the server's answer.
  const [draft, setDraft] = useState<Draft | null>(null);
  const [ack, setAck] = useState(false);
  const [forceAck, setForceAck] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [rebuilding, setRebuilding] = useState(false);

  const saved: AiSettings | undefined = settings.data;
  const base = saved ? toDraft(saved) : null;
  const current = draft ?? base;
  const dirty = draft !== null;
  const guard = useLeaveGuard(dirty);

  const queueDepth = status.data?.queueDepth ?? 0;
  const needsReindex = status.data?.vectorIndex.needsReindex ?? false;
  const refetchStatus = status.refetch;

  // Faster refreshes while something is happening, so a rebuild or a backfill shows its progress; a rebuild is over when
  // the status stops asking for one.
  useEffect(() => {
    if (!rebuilding && queueDepth === 0) return;
    const id = window.setInterval(() => {
      void refetchStatus().then((r) => {
        if (rebuilding && r.data && !r.data.vectorIndex.needsReindex) {
          setRebuilding(false);
          push({ title: t('aiAdmin.status.reindexDone'), tone: 'success' });
        }
      });
    }, BUSY_POLL_MS);
    return () => window.clearInterval(id);
  }, [rebuilding, queueDepth, refetchStatus, push, t]);

  const forbidden = isForbidden(settings.error) || isForbidden(status.error);

  const rebuild = () =>
    reindex.mutate(undefined, {
      onSuccess: () => {
        setRebuilding(true);
        push({ title: t('aiAdmin.status.reindexStarted'), message: t('aiAdmin.status.reindexStartedBody'), tone: 'success' });
      },
      onError: () =>
        push({ title: t('aiAdmin.status.reindexFailed'), message: t('aiAdmin.status.reindexFailedHint'), tone: 'error' }),
    });

  let body;
  if (settings.isPending || !saved || !current) {
    body = forbidden ? (
      <Forbidden />
    ) : settings.error ? (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('aiAdmin.loadFailed')}
          description={t('aiAdmin.loadFailedHint')}
          action={<Button onClick={() => void settings.refetch()}>{t('aiAdmin.retry')}</Button>}
        />
      </div>
    ) : (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('aiAdmin.loading')}</span>
        <div className="dam-ai__skeleton" aria-hidden="true">
          <Skeleton height={260} />
          <Skeleton height={220} />
          <Skeleton height={220} />
        </div>
      </div>
    );
  } else {
    const patch = (p: Partial<Draft>) => {
      const next = { ...current, ...p };
      setDraft(sameDraft(next, toDraft(saved)) ? null : next);
      // The acknowledgement belongs to one provider, switched on at one moment: any change to either asks again.
      if ('provider' in p || 'enabled' in p) {
        setAck(false);
        setForceAck(false);
      }
      setErrors((e) => {
        const rest = { ...e };
        if ('provider' in p || 'enabled' in p) {
          delete rest.provider;
          delete rest.acknowledgeOffSite;
        }
        if ('ocrLanguage' in p) delete rest.ocrLanguage;
        return rest;
      });
    };

    const needsAck = needsAcknowledgement(current, saved) || (forceAck && current.enabled);
    const blocked = languageInvalid(current.ocrLanguage) || providerProblem(current, saved) !== null || (needsAck && !ack);

    const submit = (e: FormEvent) => {
      e.preventDefault();
      if (!dirty || blocked || save.isPending) return;
      save.mutate(toRequest(current, ack), {
        onSuccess: (result) => {
          qc.setQueryData(['ai', 'settings'], result);
          setDraft(null);
          setAck(false);
          setForceAck(false);
          setErrors({});
          push({ title: t('aiAdmin.saved'), tone: 'success' });
        },
        onError: (err) => {
          if (err instanceof AiApiError && err.status === 422) {
            const found = fieldErrors(err.fields);
            setErrors(found);
            if (found.acknowledgeOffSite) setForceAck(true);
          } else {
            push({ title: t('aiAdmin.saveFailed'), message: t('aiAdmin.saveFailedHint'), tone: 'error' });
          }
        },
      });
    };

    const hasRefusals = Object.keys(errors).length > 0;
    body = (
      <>
        <StatusCard
          status={status.data}
          loading={status.isPending && !forbidden}
          failed={status.isError}
          fetching={status.isFetching}
          onRefresh={() => void status.refetch()}
          rebuilding={rebuilding && needsReindex}
          rebuildBusy={reindex.isPending}
          onRebuild={rebuild}
        />

        <div className="dam-ai__grid">
          <form className="dam-ai__col" onSubmit={submit} aria-label={t('aiAdmin.bar.label')} noValidate>
            <ProviderCard
              settings={saved}
              draft={current}
              onChange={patch}
              needsAck={needsAck}
              ack={ack}
              onAck={setAck}
              errors={errors}
            />
            <FeaturesCard draft={current} onChange={patch} status={status.data} languageError={errors.ocrLanguage} />

            <div className="dam-aibar" data-dirty={dirty || undefined}>
              <div className="dam-aibar__text">
                {dirty ? (
                  <p role="status">
                    <strong>{t('aiAdmin.bar.unsaved')}</strong>
                    {blocked && <span className="dam-hint"> {t('aiAdmin.bar.fixFirst')}</span>}
                  </p>
                ) : (
                  <p className="dam-hint" role="status">
                    {t('aiAdmin.bar.clean')}
                    {saved.updatedAt && (
                      <>
                        {' '}
                        <time dateTime={saved.updatedAt} title={new Date(saved.updatedAt).toLocaleString(i18n.language)}>
                          {t('aiAdmin.bar.lastSaved', { when: relativeTime(saved.updatedAt, i18n.language) })}
                        </time>
                      </>
                    )}
                  </p>
                )}
                {hasRefusals && (
                  <p className="dam-error dam-aibar__errors" role="alert">
                    <WarningCircle size={14} weight="bold" aria-hidden="true" />
                    {t('aiAdmin.bar.refused')}
                  </p>
                )}
              </div>
              <div className="dam-aibar__actions">
                <Button
                  variant="ghost"
                  disabled={!dirty || save.isPending}
                  onClick={() => {
                    setDraft(null);
                    setAck(false);
                    setForceAck(false);
                    setErrors({});
                  }}
                >
                  {t('aiAdmin.bar.discard')}
                </Button>
                <Button variant="primary" type="submit" loading={save.isPending} disabled={!dirty || blocked}>
                  {save.isPending ? t('aiAdmin.bar.saving') : t('aiAdmin.bar.save')}
                </Button>
              </div>
            </div>
          </form>

          <div className="dam-ai__col">
            <TestCard />
            <BackfillCard settings={saved} status={status.data} />
          </div>
        </div>
      </>
    );
  }

  return (
    <div className="dam-page dam-aipage">
      <PageHeader title={t('aiAdmin.title')} eyebrow={eyebrow} description={t('aiAdmin.description')} />
      {body}

      <Modal open={guard.pending !== null} title={t('aiAdmin.leave.title')} onClose={guard.stay}>
        <div className="dam-aidialog">
          <p>{t('aiAdmin.leave.body')}</p>
          <div className="dam-aidialog__actions">
            <Button variant="ghost" onClick={guard.stay}>
              {t('aiAdmin.leave.stay')}
            </Button>
            <Button variant="danger" onClick={guard.leave}>
              {t('aiAdmin.leave.leave')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}

function Forbidden() {
  const { t } = useTranslation();
  return (
    <EmptyState
      icon={<WarningCircle size={28} />}
      title={t('aiAdmin.forbidden.title')}
      description={t('aiAdmin.forbidden.description')}
    />
  );
}
