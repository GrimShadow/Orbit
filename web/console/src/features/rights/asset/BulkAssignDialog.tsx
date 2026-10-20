import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Modal, useToast } from '../../../ui';
import { useAssignRights, type AssignResult, type Rights } from '../rightsApi';
import { RightsPicker } from './RightsPicker';
import { problemText } from './rightsFormat';
import './rightsAsset.css';

/** The most assets one assign request takes (ADR-0033); a larger selection is sent in batches of this size. */
export const ASSIGN_BATCH = 500;
/** How many not-changed assets are listed with their reason before "and N more". */
const REASONS_SHOWN = 5;

interface Props {
  open: boolean;
  /** `finished` is true once rights were sent, so the caller can clear the selection it just changed. */
  onClose: (finished: boolean) => void;
  selectedIds: string[];
  /** Titles for the reason list (assets on screen). */
  titles: Map<string, string>;
}

interface Totals {
  assigned: number;
  skipped: number;
  failed: number;
  items: AssignResult['items'];
}

const chunks = <T,>(all: T[], size: number): T[][] =>
  Array.from({ length: Math.ceil(all.length / size) }, (_, i) => all.slice(i * size, (i + 1) * size));

function Body({
  close,
  markDone,
  selectedIds,
  titles,
}: Pick<Props, 'selectedIds' | 'titles'> & { close: () => void; markDone: () => void }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const assign = useAssignRights();
  const [choice, setChoice] = useState<Rights | null>(null);
  const [replace, setReplace] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [totals, setTotals] = useState<Totals | null>(null);
  const [running, setRunning] = useState(false);
  const replaceHint = useId();
  const doneRef = useRef<HTMLButtonElement>(null);
  // The form is replaced by the result: put focus on the way out rather than losing it with the button that was pressed.
  useEffect(() => {
    if (totals) doneRef.current?.focus();
  }, [totals]);
  const batches = chunks(selectedIds, ASSIGN_BATCH);

  const finish = (sum: Totals, stopped: string | null) => {
    markDone();
    setTotals(sum);
    setError(stopped);
    const clean = sum.skipped === 0 && sum.failed === 0 && !stopped;
    push(
      clean
        ? { title: t('rightsAsset.bulk.toastAll', { count: sum.assigned }), tone: 'success' }
        : {
            title: t('rightsAsset.bulk.toastSome', { assigned: sum.assigned, skipped: sum.skipped, failed: sum.failed }),
            message: stopped ?? sum.items.find((i) => i.outcome !== 'assigned')?.reason ?? undefined,
            tone: 'error',
          },
    );
  };

  const run = async () => {
    if (!choice) return;
    setRunning(true);
    setError(null);
    const sum: Totals = { assigned: 0, skipped: 0, failed: 0, items: [] };
    try {
      for (const ids of batches) {
        const r = await assign.mutateAsync({ rightsId: choice.id, assetIds: ids, replace });
        sum.assigned += r.assigned;
        sum.skipped += r.skipped;
        sum.failed += r.failed;
        sum.items.push(...r.items);
      }
      finish(sum, null);
    } catch (e) {
      // Nothing went through: stay on the form with the reason. Some batches did: say what happened so far.
      if (sum.assigned + sum.skipped + sum.failed === 0) setError(problemText(e));
      else finish(sum, problemText(e));
    } finally {
      setRunning(false);
    }
  };

  if (totals) {
    const notChanged = totals.items.filter((i) => i.outcome !== 'assigned');
    return (
      <div className="dam-rights-dialog" aria-live="polite">
        <h3 className="dam-rights__subtitle">{t('rightsAsset.bulk.resultTitle')}</h3>
        <dl className="dam-rights-bulk__totals">
          <div className="dam-rights-bulk__total">
            <dt>{t('rightsAsset.bulk.assigned')}</dt>
            <dd>{totals.assigned}</dd>
          </div>
          <div className="dam-rights-bulk__total">
            <dt>{t('rightsAsset.bulk.skipped')}</dt>
            <dd>{totals.skipped}</dd>
          </div>
          <div className={`dam-rights-bulk__total${totals.failed > 0 ? ' dam-rights-bulk__total--bad' : ''}`}>
            <dt>{t('rightsAsset.bulk.failed')}</dt>
            <dd>{totals.failed}</dd>
          </div>
        </dl>
        {error && (
          <p className="dam-error" role="alert">
            {error}
          </p>
        )}
        {notChanged.length > 0 && (
          <details>
            <summary className="dam-rights-bulk__summary">{t('rightsAsset.bulk.details')}</summary>
            <ul className="dam-rights-bulk__reasons" aria-label={t('rightsAsset.bulk.detailsTitle')}>
              {notChanged.slice(0, REASONS_SHOWN).map((i) => (
                <li key={i.assetId}>
                  <strong>{titles.get(i.assetId) ?? t('rightsAsset.bulk.unknownAsset')}</strong>
                  {': '}
                  {i.reason ?? t('rightsAsset.bulk.noReason')}
                </li>
              ))}
              {notChanged.length > REASONS_SHOWN && (
                <li>{t('rightsAsset.bulk.moreItems', { count: notChanged.length - REASONS_SHOWN })}</li>
              )}
            </ul>
          </details>
        )}
        <div className="dam-rights-dialog__actions">
          <Button ref={doneRef} variant="primary" onClick={close}>
            {t('rightsAsset.bulk.done')}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className="dam-rights-dialog">
      <p className="dam-rights-bulk__count">{t('rightsAsset.bulk.count', { count: selectedIds.length })}</p>
      <RightsPicker value={choice?.id ?? null} onChange={setChoice} />
      <label className="dam-rights-bulk__check">
        <input type="checkbox" checked={replace} aria-describedby={replaceHint} onChange={(e) => setReplace(e.target.checked)} />
        <span>
          {t('rightsAsset.bulk.replace')}
          <span id={replaceHint} className="dam-hint">
            {t('rightsAsset.bulk.replaceHint')}
          </span>
        </span>
      </label>
      {batches.length > 1 && (
        <p className="dam-rights-bulk__note" role="note">
          {t('rightsAsset.bulk.chunked', { count: selectedIds.length, batches: batches.length })}
        </p>
      )}
      {error && (
        <p className="dam-error" role="alert">
          {error}
        </p>
      )}
      <div className="dam-rights-dialog__actions">
        <Button variant="ghost" disabled={running} onClick={close}>
          {t('rightsAsset.assignDialog.cancel')}
        </Button>
        <Button variant="primary" disabled={!choice} loading={running} onClick={() => void run()}>
          {running ? t('rightsAsset.bulk.running') : t('rightsAsset.bulk.submit', { count: selectedIds.length })}
        </Button>
      </div>
    </div>
  );
}

/** Put the same rights record on every selected asset (needs `rights.manage`). A record is shown with its status so an
 * expired one is obvious; "Replace existing rights" moves assets that already have a different record, otherwise those are
 * skipped. The result says how many were assigned, skipped or failed and, expandable, why. */
export function BulkAssignDialog({ open, onClose, selectedIds, titles }: Props) {
  const { t } = useTranslation();
  const finished = useRef(false);
  const close = () => {
    const done = finished.current;
    finished.current = false;
    onClose(done);
  };
  return (
    <Modal open={open} onClose={close} title={t('rightsAsset.bulk.title')} closeLabel={t('rightsAsset.close')}>
      <Body
        close={close}
        markDone={() => {
          finished.current = true;
        }}
        selectedIds={selectedIds}
        titles={titles}
      />
    </Modal>
  );
}
