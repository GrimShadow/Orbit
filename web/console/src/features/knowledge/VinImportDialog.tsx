import { useId, useState, type ChangeEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Modal, WarningCircle } from '../../ui';
import { isLimitError } from '../glossary/glossaryApi';
import { errorReason, useImportVinRules } from './knowledgeApi';
import { sampleCsvHref } from './knowledgeModel';
import { VIN_RULE_LIMIT, type VinImportResultDto } from './knowledgeTypes';

const COLUMNS = ['name', 'pattern', 'model', 'variant', 'year', 'theme', 'priority', 'enabled'] as const;
const REQUIRED = new Set(['name', 'pattern', 'model']);
const SHOWN_ERRORS = 50;

function Errors({ errors }: { errors: VinImportResultDto['errors'] }) {
  const { t } = useTranslation();
  if (errors.length === 0) return null;
  return (
    <div role="alert">
      <p className="dam-label dam-knimport__errtitle">
        <WarningCircle size={14} weight="bold" aria-hidden="true" />
        {t('knowledge.import.errors', { count: errors.length })}
      </p>
      <ul className="dam-knimport__errors">
        {errors.slice(0, SHOWN_ERRORS).map((e, i) => (
          <li key={`${e.line}-${i}`}>
            <span className="dam-knimport__line">{t('knowledge.import.line', { line: e.line })}</span> {e.message}
          </li>
        ))}
        {errors.length > SHOWN_ERRORS && (
          <li className="dam-hint">{t('knowledge.import.moreErrors', { count: errors.length - SHOWN_ERRORS })}</li>
        )}
      </ul>
    </div>
  );
}

function Counts({ result }: { result: VinImportResultDto }) {
  const { t } = useTranslation();
  return (
    <ul className="dam-knimport__counts" aria-label={t('knowledge.import.countsLabel')}>
      <li>
        <Badge tone="success">{t('knowledge.import.created', { count: result.created })}</Badge>
      </li>
      <li>
        <Badge tone="info">{t('knowledge.import.updated', { count: result.updated })}</Badge>
      </li>
      <li>
        <Badge tone={result.skipped > 0 ? 'warning' : 'neutral'}>
          {t('knowledge.import.skipped', { count: result.skipped })}
        </Badge>
      </li>
    </ul>
  );
}

function Body({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const run = useImportVinRules();
  const areaId = useId();
  const fileId = useId();
  const [text, setText] = useState('');
  const [fileName, setFileName] = useState<string | null>(null);
  const [fileError, setFileError] = useState(false);
  const [partial, setPartial] = useState(false);
  /** The dry run for exactly the text and option now on screen; any edit clears it. */
  const [preview, setPreview] = useState<VinImportResultDto | null>(null);
  const [done, setDone] = useState<VinImportResultDto | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  const reset = () => {
    setPreview(null);
    setFailure(null);
  };
  const fail = (err: unknown) =>
    setFailure(
      isLimitError(err)
        ? t('knowledge.rules.limitReached', { max: VIN_RULE_LIMIT.toLocaleString() })
        : t('knowledge.import.failed', { reason: errorReason(err) }),
    );

  const onFile = async (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = ''; // the same file can be chosen again
    if (!file) return;
    try {
      setText(await file.text());
      setFileName(file.name);
      setFileError(false);
      reset();
    } catch {
      setFileError(true);
    }
  };

  const check = () => {
    setFailure(null);
    run.mutate({ csv: text, dryRun: true, partial }, { onSuccess: setPreview, onError: fail });
  };
  const doImport = () => {
    setFailure(null);
    run.mutate({ csv: text, dryRun: false, partial }, { onSuccess: setDone, onError: fail });
  };

  if (done) {
    return (
      <div className="dam-stack dam-dialogform dam-knimport">
        <p role="status" aria-live="polite" className="dam-knimport__result">
          {t('knowledge.import.finished')}
        </p>
        <Counts result={done} />
        <Errors errors={done.errors} />
        <div className="dam-dialogform__actions">
          <Button variant="primary" onClick={onClose}>
            {t('knowledge.import.done')}
          </Button>
        </div>
      </div>
    );
  }

  const writes = preview ? preview.created + preview.updated : 0;
  const blocked = preview !== null && preview.errors.length > 0 && !partial;
  const canImport = preview !== null && !blocked && writes > 0;

  return (
    <div className="dam-stack dam-dialogform dam-knimport">
      <p className="dam-hint">{t('knowledge.import.intro')}</p>
      <div>
        <p className="dam-label">{t('knowledge.import.columns')}</p>
        <ul className="dam-knimport__cols" aria-label={t('knowledge.import.columns')}>
          {COLUMNS.map((c) => (
            <li key={c}>
              <code className="dam-chip-code">{c}</code>
              <span className="dam-hint">
                {REQUIRED.has(c) ? `${t('knowledge.import.required')}. ` : ''}
                {t(`knowledge.import.col.${c}`)}
              </span>
            </li>
          ))}
        </ul>
        <a className="dam-knimport__sample" href={sampleCsvHref()} download="vin-rules-sample.csv">
          {t('knowledge.import.sample')}
        </a>
      </div>

      <div className="dam-field">
        <label className="dam-label" htmlFor={fileId}>
          {t('knowledge.import.file')}
        </label>
        <input id={fileId} className="dam-input" type="file" accept=".csv,text/csv,text/plain" onChange={(e) => void onFile(e)} />
        {fileName && <span className="dam-hint">{t('knowledge.import.loaded', { name: fileName })}</span>}
        {fileError && (
          <span className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {t('knowledge.import.fileFailed')}
          </span>
        )}
      </div>
      <div className="dam-field">
        <label className="dam-label" htmlFor={areaId}>
          {t('knowledge.import.paste')}
        </label>
        <textarea
          id={areaId}
          className="dam-input dam-knimport__area"
          rows={7}
          spellCheck={false}
          value={text}
          disabled={run.isPending}
          onChange={(e) => {
            setText(e.target.value);
            setFileName(null);
            reset();
          }}
        />
      </div>
      <label className="dam-knimport__partial">
        <input
          type="checkbox"
          className="dam-check__input"
          checked={partial}
          onChange={(e) => {
            setPartial(e.target.checked);
            reset();
          }}
        />
        <span>
          {t('knowledge.import.partial')}
          <span className="dam-hint dam-knimport__partialhint">{t('knowledge.import.partialHint')}</span>
        </span>
      </label>

      <div aria-live="polite" className="dam-knimport__preview">
        {preview && (
          <>
            <p className="dam-knimport__result">{t('knowledge.import.previewTitle')}</p>
            <Counts result={preview} />
            <Errors errors={preview.errors} />
            {blocked && <p className="dam-hint">{t('knowledge.import.blocked')}</p>}
            {!blocked && writes === 0 && <p className="dam-hint">{t('knowledge.import.nothing')}</p>}
          </>
        )}
      </div>
      {failure && (
        <p className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {failure}
        </p>
      )}

      <div className="dam-dialogform__actions">
        <Button variant="ghost" onClick={onClose} disabled={run.isPending}>
          {t('knowledge.cancel')}
        </Button>
        <Button
          variant="secondary"
          disabled={text.trim() === ''}
          loading={run.isPending && run.variables?.dryRun}
          onClick={check}
        >
          {t('knowledge.import.check')}
        </Button>
        <Button
          variant="primary"
          disabled={!canImport}
          loading={run.isPending && run.variables?.dryRun === false}
          onClick={doImport}
        >
          {t('knowledge.import.submit', { count: writes })}
        </Button>
      </div>
    </div>
  );
}

/** Import VIN rules from a CSV (a pasted text or a chosen file). A dry run comes first and shows what would be created, updated
 * and skipped, and every line that cannot be imported, before "Import" writes anything. */
export function VinImportDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Modal open={open} title={t('knowledge.import.title')} closeLabel={t('knowledge.close')} onClose={onClose}>
      <Body onClose={onClose} />
    </Modal>
  );
}
