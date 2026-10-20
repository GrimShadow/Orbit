import { ApiError } from '@dam/sdk-js';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Modal, Select, WarningCircle } from '../../ui';
import { languageLabel, useEnabledLanguages } from '../translation/languages';
import { AssetPicker, type PickedAsset } from './AssetPicker';
import { docDraftToInput, docToDraft, emptyDocDraft, validateDocDraft, type DocDraft, type DocErrors } from './knowledgeDraft';
import { errorReason, useSaveKnowledgeDocument } from './knowledgeApi';
import { DOC_TYPES, type DocType, type KnowledgeDocumentDto } from './knowledgeTypes';

interface Props {
  open: boolean;
  onClose: () => void;
  /** Editing this mapping; omitted to map a new document. */
  document?: KnowledgeDocumentDto;
  /** The asset to map, already known (the asset page); omitted to pick one in the dialog. */
  asset?: PickedAsset;
  onSaved: (doc: KnowledgeDocumentDto, created: boolean) => void;
}

function Body({ document, asset, onClose, onSaved }: Omit<Props, 'open'>) {
  const { t, i18n } = useTranslation();
  const languages = useEnabledLanguages();
  const save = useSaveKnowledgeDocument();
  const [picked, setPicked] = useState<PickedAsset | null>(asset ?? null);
  const [draft, setDraft] = useState<DocDraft>(() =>
    document ? docToDraft(document) : emptyDocDraft(languages.includes('en') ? 'en' : (languages[0] ?? 'en')),
  );
  const [attempted, setAttempted] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);

  const editing = !!document;
  const errors: DocErrors = attempted ? validateDocDraft(draft, editing || picked !== null) : {};
  const msg = (code: string | undefined) => (code ? t(`knowledge.errors.${code}`) : undefined);
  const set = <K extends keyof DocDraft>(key: K, value: DocDraft[K]) => setDraft((d) => ({ ...d, [key]: value }));
  // A mapping may carry a language the tenant no longer lists; keep it selectable so editing does not silently change it.
  const languageOptions = [...new Set([...languages, ...(draft.language ? [draft.language] : [])])];

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setAttempted(true);
    setServerError(null);
    if (Object.keys(validateDocDraft(draft, editing || picked !== null)).length > 0) return;
    save.mutate(
      { id: document?.id, body: docDraftToInput(draft, editing ? undefined : picked!.id) },
      {
        onSuccess: (saved) => onSaved(saved, !editing),
        onError: (err) => {
          if (err instanceof ApiError && err.status === 409) setServerError(t('knowledge.map.alreadyMapped'));
          else if (err instanceof ApiError && err.status === 403) setServerError(t('knowledge.map.forbidden'));
          else setServerError(t('knowledge.map.saveFailed', { reason: errorReason(err) }));
        },
      },
    );
  };

  return (
    <form className="dam-stack dam-dialogform dam-knform" onSubmit={submit} noValidate>
      {editing ? (
        <p className="dam-knform__asset">
          <span className="dam-label">{t('knowledge.picker.label')}</span>
          <strong>{document.title}</strong>
        </p>
      ) : (
        <AssetPicker value={picked} onChange={setPicked} error={msg(errors.asset)} />
      )}
      <Select
        label={t('knowledge.field.docType')}
        value={draft.docType}
        options={DOC_TYPES.map((d) => ({ value: d, label: t(`knowledge.docType.${d}`) }))}
        onChange={(e) => set('docType', e.target.value as DocType)}
      />
      <div className="dam-knform__pair">
        <Input
          label={t('knowledge.field.model')}
          hint={t('knowledge.field.modelHint')}
          value={draft.model}
          autoComplete="off"
          spellCheck={false}
          aria-required="true"
          error={msg(errors.model)}
          onChange={(e) => set('model', e.target.value)}
        />
        <Input
          label={t('knowledge.field.variant')}
          hint={t('knowledge.field.variantHint')}
          value={draft.variant}
          autoComplete="off"
          spellCheck={false}
          error={msg(errors.variant)}
          onChange={(e) => set('variant', e.target.value)}
        />
      </div>
      <fieldset className="dam-knform__years" aria-describedby="dam-knform-years-hint">
        <legend className="dam-label">{t('knowledge.field.years')}</legend>
        <div className="dam-knform__pair">
          <Input
            label={t('knowledge.field.yearFrom')}
            value={draft.yearFrom}
            inputMode="numeric"
            maxLength={4}
            autoComplete="off"
            placeholder="2021"
            error={msg(errors.yearFrom)}
            onChange={(e) => set('yearFrom', e.target.value)}
          />
          <Input
            label={t('knowledge.field.yearTo')}
            value={draft.yearTo}
            inputMode="numeric"
            maxLength={4}
            autoComplete="off"
            placeholder="2024"
            error={msg(errors.yearTo)}
            onChange={(e) => set('yearTo', e.target.value)}
          />
        </div>
        <span id="dam-knform-years-hint" className="dam-hint">
          {t('knowledge.field.yearsHint')}
        </span>
        {errors.years && (
          <span className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {msg(errors.years)}
          </span>
        )}
      </fieldset>
      <div className="dam-knform__pair">
        <Select
          label={t('knowledge.field.language')}
          value={draft.language}
          options={languageOptions.map((l) => ({ value: l, label: languageLabel(l, i18n.language) }))}
          error={msg(errors.language)}
          onChange={(e) => set('language', e.target.value)}
        />
        <Input
          label={t('knowledge.field.theme')}
          hint={t('knowledge.field.themeHint')}
          value={draft.theme}
          autoComplete="off"
          spellCheck={false}
          error={msg(errors.theme)}
          onChange={(e) => set('theme', e.target.value)}
        />
      </div>
      {serverError && (
        <p className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {serverError}
        </p>
      )}
      <div className="dam-dialogform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('knowledge.cancel')}
        </Button>
        <Button type="submit" variant="primary" loading={save.isPending}>
          {t(editing ? 'knowledge.map.save' : 'knowledge.map.add')}
        </Button>
      </div>
    </form>
  );
}

/** Map an existing asset as a knowledge document, or change an existing mapping. The form restarts each time it opens. */
export function DocumentMapDialog({ open, onClose, document, asset, onSaved }: Props) {
  const { t } = useTranslation();
  return (
    <Modal
      open={open}
      title={t(document ? 'knowledge.map.editTitle' : 'knowledge.map.addTitle')}
      closeLabel={t('knowledge.close')}
      onClose={onClose}
    >
      <Body document={document} asset={asset} onClose={onClose} onSaved={onSaved} />
    </Modal>
  );
}
