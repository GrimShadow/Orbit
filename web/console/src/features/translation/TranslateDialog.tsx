import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, CheckboxGroup, Modal, Select, Skeleton } from '../../ui';
import { errorMessage, useSchema, type FieldDto } from '../asset/assetApi';
import { languageLabel, MAX_TARGET_LANGUAGES, useEnabledLanguages } from './languages';
import { JobProgress } from './JobProgress';
import { useTranslateAsset } from './translationApi';

/** The fields of an asset type that hold one text per language. */
export const mlFields = (fields: FieldDto[]) => fields.filter((f) => f.type === 'ml_text');

interface Props {
  assetId: string;
  assetType: string;
  open: boolean;
  onClose: () => void;
}

/**
 * Translate several fields into several languages in one request: pick the fields, the target languages (at most ten),
 * optionally the source language, and whether languages that already have text are redone. The request is a background
 * job whose results (translated, skipped with a reason, failed with a reason) are listed here as it finishes; the
 * translations themselves arrive as suggestions to review, and nothing on the asset changes until they are accepted.
 */
export function TranslateDialog({ assetId, assetType, open, onClose }: Props) {
  const { t, i18n } = useTranslation();
  const schema = useSchema(assetType);
  const fields = mlFields(schema.data?.fields ?? []);
  const languages = useEnabledLanguages();
  const translate = useTranslateAsset(assetId);
  const overwriteId = useId();

  // `null` = nothing chosen yet: the dialog then offers every field.
  const [chosenFields, setChosenFields] = useState<string[] | null>(null);
  const [targets, setTargets] = useState<string[]>([]);
  const [source, setSource] = useState('');
  const [overwrite, setOverwrite] = useState(false);
  const [attempted, setAttempted] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [jobId, setJobId] = useState<string | null>(null);

  const fieldNames = fields.map((f) => f.name);
  const selectedFields = chosenFields ?? fieldNames;
  const targetOptions = languages.filter((l) => l !== source);
  const picked = targets.filter((l) => targetOptions.includes(l));
  const atLimit = picked.length >= MAX_TARGET_LANGUAGES;

  const fieldError = attempted && selectedFields.length === 0 ? t('translation.dialog.needField') : undefined;
  const targetError = attempted && picked.length === 0 ? t('translation.dialog.needTarget') : undefined;

  const close = () => {
    onClose();
    // A finished run starts over next time; one in progress keeps its view so reopening shows where it got to.
  };

  const submit = () => {
    setAttempted(true);
    setError(null);
    if (selectedFields.length === 0 || picked.length === 0) return;
    translate.mutate(
      { fields: selectedFields, targetLanguages: picked, sourceLanguage: source || null, overwrite },
      { onSuccess: (job) => setJobId(job.id), onError: (e) => setError(errorMessage(e)) },
    );
  };

  const labels = Object.fromEntries(fields.map((f) => [f.name, f.label]));

  let body;
  if (jobId) {
    body = (
      <div className="dam-stack dam-tdlg">
        <JobProgress assetId={assetId} assetType={assetType} jobId={jobId} fieldLabels={labels} onReview={close} />
        <div className="dam-dialogform__actions">
          <Button variant="secondary" onClick={() => setJobId(null)}>
            {t('translation.dialog.another')}
          </Button>
          <Button variant="primary" onClick={close}>
            {t('translation.dialog.done')}
          </Button>
        </div>
      </div>
    );
  } else if (schema.isPending) {
    body = (
      <div role="status" aria-label={t('translation.dialog.loading')} className="dam-stack dam-tdlg">
        <Skeleton height={20} width="60%" />
        <Skeleton height={80} />
      </div>
    );
  } else if (schema.error || fields.length === 0) {
    body = (
      <div className="dam-stack dam-tdlg">
        <p role={schema.error ? 'alert' : undefined}>
          {t(schema.error ? 'translation.dialog.schemaFailed' : 'translation.dialog.noFields')}
        </p>
        <div className="dam-dialogform__actions">
          <Button variant="secondary" onClick={close}>
            {t('translation.dialog.cancel')}
          </Button>
        </div>
      </div>
    );
  } else {
    body = (
      <form
        className="dam-stack dam-dialogform dam-tdlg"
        noValidate
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
      >
        <p className="dam-hint">{t('translation.dialog.intro')}</p>

        <div>
          <CheckboxGroup
            label={t('translation.dialog.fields')}
            options={fields.map((f) => ({ value: f.name, label: f.label }))}
            value={selectedFields}
            onChange={setChosenFields}
          />
          {fieldError && (
            <span className="dam-error" role="alert">
              {fieldError}
            </span>
          )}
        </div>

        <Select
          label={t('translation.dialog.source')}
          hint={t('translation.dialog.sourceHint')}
          value={source}
          onChange={(e) => setSource(e.target.value)}
          options={[
            { value: '', label: t('translation.dialog.sourceAuto') },
            ...languages.map((l) => ({ value: l, label: languageLabel(l, i18n.language) })),
          ]}
        />

        <div>
          <CheckboxGroup
            label={t('translation.dialog.targets')}
            hint={t('translation.dialog.targetsHint', { count: picked.length, max: MAX_TARGET_LANGUAGES })}
            options={targetOptions.map((l) => ({
              value: l,
              label: languageLabel(l, i18n.language),
              disabled: atLimit && !picked.includes(l),
            }))}
            value={picked}
            onChange={setTargets}
          />
          {targetError && (
            <span className="dam-error" role="alert">
              {targetError}
            </span>
          )}
        </div>

        <label className="dam-check" htmlFor={overwriteId}>
          <input
            id={overwriteId}
            type="checkbox"
            className="dam-check__input"
            checked={overwrite}
            onChange={(e) => setOverwrite(e.target.checked)}
          />
          <span className="dam-check__label">
            {t('translation.dialog.overwrite')}
            <span className="dam-hint dam-tdlg__sub">{t('translation.dialog.overwriteHint')}</span>
          </span>
        </label>

        {error && (
          <p className="dam-error" role="alert">
            {t('translation.dialog.failed', { reason: error })}
          </p>
        )}
        <div className="dam-dialogform__actions">
          <Button variant="ghost" onClick={close}>
            {t('translation.dialog.cancel')}
          </Button>
          <Button type="submit" variant="primary" loading={translate.isPending}>
            {t('translation.dialog.submit')}
          </Button>
        </div>
      </form>
    );
  }

  return (
    <Modal open={open} title={t('translation.dialog.title')} closeLabel={t('translation.dialog.close')} onClose={close}>
      {body}
    </Modal>
  );
}
