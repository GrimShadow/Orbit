import { useCallback, useEffect, useId, useMemo, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Drawer, Input, Modal, Select, WarningCircle } from '../../../ui';
import { Switch } from '../../webhooks/Switch';
import '../../webhooks/webhooks.css';
import { RightsApiError, useSaveRights, type Rights, type RightsDocument, type RightsInputBody } from '../rightsApi';
import { DeleteDocumentDialog, DocumentsPanel } from './DocumentsPanel';
import { ScopePicker } from './ScopePicker';
import { licenceOptions, narrows, previewStatus, sameSet, statusLabel, STATUS_TONE } from './rightsLabels';
import './rights.css';

type Field = 'name' | 'licenceType' | 'licensor' | 'channels' | 'regions' | 'validFrom' | 'validTo' | 'notes';
type Errors = Partial<Record<Field, string>>;
const FIELDS: readonly Field[] = ['name', 'licenceType', 'licensor', 'channels', 'regions', 'validFrom', 'validTo', 'notes'];

interface Draft {
  name: string;
  licenceType: string;
  licensor: string;
  channels: string[];
  regions: string[];
  validFrom: string;
  validTo: string;
  watermarkRequired: boolean;
  notes: string;
}

const initialDraft = (r: Rights | null): Draft => ({
  name: r?.name ?? '',
  licenceType: r?.licenceType ?? 'licensed',
  licensor: r?.licensor ?? '',
  channels: r?.channels ?? [],
  regions: r?.regions ?? [],
  validFrom: r?.validFrom ?? '',
  validTo: r?.validTo ?? '',
  watermarkRequired: r?.watermarkRequired ?? false,
  notes: r?.notes ?? '',
});

const isDirty = (a: Draft, b: Draft) =>
  a.name !== b.name ||
  a.licenceType !== b.licenceType ||
  a.licensor !== b.licensor ||
  a.validFrom !== b.validFrom ||
  a.validTo !== b.validTo ||
  a.watermarkRequired !== b.watermarkRequired ||
  a.notes !== b.notes ||
  !sameSet(a.channels, b.channels) ||
  !sameSet(a.regions, b.regions);

/** The request body for a draft: text trimmed, empty dates sent as null. */
export function toBody(d: Draft): RightsInputBody {
  return {
    name: d.name.trim(),
    licenceType: d.licenceType,
    licensor: d.licensor.trim(),
    channels: d.channels,
    regions: d.regions,
    validFrom: d.validFrom || null,
    validTo: d.validTo || null,
    watermarkRequired: d.watermarkRequired,
    notes: d.notes.trim(),
  };
}

/** Splits a save failure into the form fields it names and a general message for the rest. */
function failure(e: unknown): { byField: Errors; general: string | null } {
  const byField: Errors = {};
  const rest: string[] = [];
  if (e instanceof RightsApiError) {
    for (const [key, messages] of Object.entries(e.fields)) {
      const field = FIELDS.find((f) => f.toLowerCase() === key.replace(/\[\d+\]$/, '').toLowerCase());
      const text = messages.join(' ');
      if (field) byField[field] = byField[field] ? `${byField[field]} ${text}` : text;
      else rest.push(text);
    }
    if (Object.keys(byField).length === 0 && rest.length === 0) return { byField, general: e.detail || e.title };
    return { byField, general: rest.join(' ') || null };
  }
  return { byField, general: null };
}

export interface SavedInfo {
  rights: Rights;
  created: boolean;
}

interface BodyProps {
  rights: Rights | null;
  onClose: () => void;
  onSaved: (info: SavedInfo) => void;
  onDirty: (dirty: boolean) => void;
  onDeleteDocument: (doc: RightsDocument) => void;
}

function FormBody({ rights, onClose, onSaved, onDirty, onDeleteDocument }: BodyProps) {
  const { t } = useTranslation();
  const save = useSaveRights();
  const baseline = useMemo(() => initialDraft(rights), [rights]);
  const [draft, setDraft] = useState<Draft>(baseline);
  const [errors, setErrors] = useState<Errors>({});
  const [general, setGeneral] = useState<string | null>(null);
  const notesId = useId();

  const dirty = isDirty(draft, baseline);
  useEffect(() => onDirty(dirty), [dirty, onDirty]);
  useEffect(() => () => onDirty(false), [onDirty]); // closing, however it happens, leaves nothing unsaved behind

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => {
    setDraft((d) => ({ ...d, [key]: value }));
    if (key in errors) setErrors((prev) => ({ ...prev, [key]: undefined }));
  };

  const datesBackwards = !!draft.validFrom && !!draft.validTo && draft.validTo < draft.validFrom;
  const dateError = datesBackwards ? t('rights.form.datesBackwards') : undefined;
  const preview = previewStatus(draft.validFrom, draft.validTo);
  const assigned = rights?.assetCount ?? 0;
  const narrowing =
    !!rights && assigned > 0 && (narrows(rights.channels, draft.channels) || narrows(rights.regions, draft.regions));

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const local: Errors = {};
    if (!draft.name.trim()) local.name = t('rights.form.nameRequired');
    if (datesBackwards) local.validTo = t('rights.form.datesBackwards');
    setErrors(local);
    setGeneral(null);
    if (Object.keys(local).length > 0) return;
    save.mutate(
      { id: rights?.id, body: toBody(draft) },
      {
        onSuccess: (saved) => {
          onDirty(false);
          onSaved({ rights: saved, created: !rights });
        },
        onError: (err) => {
          const { byField, general: other } = failure(err);
          setErrors(byField);
          setGeneral(other ?? (Object.keys(byField).length === 0 ? t('rights.form.saveFailed') : null));
        },
      },
    );
  };

  return (
    <form className="dam-stack dam-dialogform dam-rform" onSubmit={submit} noValidate>
      {general && (
        <p role="alert" className="dam-rwarn dam-rwarn--danger">
          <WarningCircle size={16} aria-hidden="true" />
          <span>{general}</span>
        </p>
      )}

      <Input
        label={t('rights.form.name')}
        value={draft.name}
        onChange={(e) => set('name', e.target.value)}
        error={errors.name}
        maxLength={200}
        autoComplete="off"
        placeholder={t('rights.form.namePlaceholder')}
      />
      <div className="dam-rform__row">
        <Select
          label={t('rights.form.licenceType')}
          value={draft.licenceType}
          onChange={(e) => set('licenceType', e.target.value)}
          options={licenceOptions(t)}
          error={errors.licenceType}
        />
        <Input
          label={t('rights.form.licensor')}
          value={draft.licensor}
          onChange={(e) => set('licensor', e.target.value)}
          error={errors.licensor}
          maxLength={200}
          autoComplete="off"
          placeholder={t('rights.form.licensorPlaceholder')}
        />
      </div>

      <fieldset className="dam-rform__group">
        <legend className="dam-rform__legend">{t('rights.form.whereTitle')}</legend>
        <p className="dam-hint dam-rform__lead">{t('rights.form.whereLead')}</p>
        <ScopePicker dimension="channel" value={draft.channels} onChange={(v) => set('channels', v)} error={errors.channels} />
        <ScopePicker dimension="region" value={draft.regions} onChange={(v) => set('regions', v)} error={errors.regions} />
        {narrowing && (
          <p role="alert" className="dam-rwarn">
            <WarningCircle size={16} aria-hidden="true" />
            <span>{t('rights.form.narrowing', { count: assigned })}</span>
          </p>
        )}
      </fieldset>

      <fieldset className="dam-rform__group">
        <legend className="dam-rform__legend">{t('rights.form.validityTitle')}</legend>
        <div className="dam-rform__row">
          <Input
            type="date"
            label={t('rights.form.validFrom')}
            value={draft.validFrom}
            onChange={(e) => set('validFrom', e.target.value)}
            error={errors.validFrom}
            max={draft.validTo || undefined}
          />
          <Input
            type="date"
            label={t('rights.form.validTo')}
            value={draft.validTo}
            onChange={(e) => set('validTo', e.target.value)}
            error={errors.validTo ?? dateError}
            min={draft.validFrom || undefined}
            hint={t('rights.form.validToHint')}
          />
        </div>
        <p className="dam-rform__preview" role="status">
          <span>{t('rights.form.statusPreview')}</span>
          <Badge tone={STATUS_TONE[preview.status]} size="sm">
            {statusLabel(t, preview.status, preview.daysRemaining)}
          </Badge>
        </p>
      </fieldset>

      <Switch
        checked={draft.watermarkRequired}
        onChange={(v) => set('watermarkRequired', v)}
        label={t('rights.form.watermark')}
        hint={t('rights.form.watermarkHint')}
      />

      <div className="dam-field">
        <label className="dam-label" htmlFor={notesId}>
          {t('rights.form.notes')}
        </label>
        <textarea
          id={notesId}
          className="dam-input dam-rform__notes"
          rows={3}
          value={draft.notes}
          onChange={(e) => set('notes', e.target.value)}
          aria-invalid={errors.notes ? true : undefined}
          placeholder={t('rights.form.notesPlaceholder')}
        />
        {errors.notes && (
          <span className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {errors.notes}
          </span>
        )}
      </div>

      {rights ? (
        <DocumentsPanel rights={rights} onDelete={onDeleteDocument} />
      ) : (
        <p className="dam-hint dam-rform__docsnote">{t('rights.documents.saveFirst')}</p>
      )}

      <div className="dam-dialogform__actions dam-rform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('rights.cancel')}
        </Button>
        <Button type="submit" variant="primary" loading={save.isPending}>
          {rights ? t('rights.form.save') : t('rights.form.create')}
        </Button>
      </div>
    </form>
  );
}

interface RightsFormProps {
  open: boolean;
  /** The record being edited, or `null` to create one. */
  rights: Rights | null;
  onClose: () => void;
  onSaved: (info: SavedInfo) => void;
}

/** The create/edit side panel. Closing it with unsaved edits (Esc, the backdrop, the close button or Cancel) asks first.
 * The form mounts fresh each time the panel opens, so it never shows a previous record's values. */
export function RightsForm({ open, rights, onClose, onSaved }: RightsFormProps) {
  const { t } = useTranslation();
  const dirty = useRef(false);
  const [confirming, setConfirming] = useState(false);
  const [docToDelete, setDocToDelete] = useState<RightsDocument | null>(null);

  const setDirty = useCallback((d: boolean) => {
    dirty.current = d;
  }, []);

  const requestClose = () => {
    if (dirty.current) setConfirming(true);
    else onClose();
  };

  // Esc is handled by the browser's own <dialog>: hold it back (capture phase, `cancel` does not bubble) while there are edits.
  useEffect(() => {
    if (!open) return;
    // Asked a moment later: a dialog opened while the browser is still handling Esc can be closed by that same key press.
    const askLater = () => window.setTimeout(() => setConfirming(true), 0);
    const ours = (e: Event) => {
      const target = e.target as HTMLDialogElement | null;
      return dirty.current && target?.classList?.contains('dam-dialog--drawer') && !!target.querySelector('.dam-rform')
        ? target
        : null;
    };
    const hold = (e: Event) => {
      if (ours(e)) {
        e.preventDefault();
        askLater();
      }
    };
    // A second Esc press closes a <dialog> whose `cancel` was held back; open it again behind the question.
    const reopen = (e: Event) => {
      const dialog = ours(e);
      if (dialog && !dialog.open && typeof dialog.showModal === 'function') {
        dialog.showModal();
        askLater();
      }
    };
    document.addEventListener('cancel', hold, true);
    document.addEventListener('close', reopen, true);
    return () => {
      document.removeEventListener('cancel', hold, true);
      document.removeEventListener('close', reopen, true);
    };
  }, [open]);

  return (
    <>
      <Drawer
        open={open}
        title={rights ? t('rights.form.editTitle') : t('rights.form.newTitle')}
        closeLabel={t('rights.close')}
        onClose={requestClose}
      >
        <FormBody rights={rights} onClose={requestClose} onSaved={onSaved} onDirty={setDirty} onDeleteDocument={setDocToDelete} />
      </Drawer>
      {rights && <DeleteDocumentDialog rightsId={rights.id} doc={docToDelete} onClose={() => setDocToDelete(null)} />}
      <Modal
        open={confirming}
        title={t('rights.discard.title')}
        closeLabel={t('rights.close')}
        onClose={() => setConfirming(false)}
      >
        <div className="dam-stack dam-dialogform dam-rdlg">
          <p>{t('rights.discard.body')}</p>
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={() => setConfirming(false)}>
              {t('rights.discard.keep')}
            </Button>
            <Button
              variant="danger"
              onClick={() => {
                dirty.current = false;
                setConfirming(false);
                onClose();
              }}
            >
              {t('rights.discard.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
}
