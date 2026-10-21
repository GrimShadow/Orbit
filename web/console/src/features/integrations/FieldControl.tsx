import { CaretDown, Eye, EyeSlash, LockKey, WarningCircle } from '@phosphor-icons/react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton } from '../../ui';
import { Switch } from '../webhooks/Switch';
import type { SecretDraft } from './formModel';
import type { FieldDto, SecretState } from './integrationsTypes';

interface FrameProps {
  label: string;
  required?: boolean;
  help?: string | null;
  error?: string;
  /** The id of the control the label names; without one the label is plain text (the control is a group of buttons). */
  controlId?: string;
  labelId?: string;
  helpId: string;
  errorId: string;
  children: ReactNode;
}

/** A labelled field row: the label with a required marker, the control, its help text and its error, as the kit's fields
 * lay them out. */
export function FieldFrame({ label, required, help, error, controlId, labelId, helpId, errorId, children }: FrameProps) {
  const text = (
    <>
      {label}
      {required && (
        <span className="dam-intreq" aria-hidden="true">
          {' '}
          *
        </span>
      )}
    </>
  );
  return (
    <div className="dam-field">
      {controlId ? (
        <label className="dam-label" htmlFor={controlId}>
          {text}
        </label>
      ) : (
        <span className="dam-label" id={labelId}>
          {text}
        </span>
      )}
      {children}
      {help && (
        <span id={helpId} className="dam-hint">
          {help}
        </span>
      )}
      {error && (
        <span id={errorId} className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </span>
      )}
    </div>
  );
}

const describedBy = (field: FieldDto, hasError: boolean, helpId: string, errorId: string) =>
  [field.help && helpId, hasError && errorId].filter(Boolean).join(' ') || undefined;

interface SecretFieldProps {
  field: FieldDto;
  /** What the connection says about the stored secret; absent when creating. */
  stored: SecretState | undefined;
  draft: SecretDraft;
  onChange: (next: SecretDraft) => void;
  error?: string;
}

/** A write-only secret. A stored one shows "Set (…hint)" with Replace (and Remove, unless the secret is required); a typed one never leaves its password input
 * (a toggle shows it while you type). The stored value is never shown or prefilled. */
function SecretField({ field, stored, draft, onChange, error }: SecretFieldProps) {
  const { t } = useTranslation();
  const id = useId();
  const [helpId, errorId, labelId] = [`${id}-help`, `${id}-error`, `${id}-label`];
  const isSet = stored?.set === true;
  const label = field.label;

  const set = (next: Partial<SecretDraft>) => onChange({ ...draft, ...next });

  let control: ReactNode;
  if (isSet && draft.mode === 'keep') {
    control = (
      <div className="dam-intsecret" role="group" aria-labelledby={labelId}>
        <span className="dam-intsecret__state">
          <LockKey size={16} aria-hidden="true" />
          <span>{stored?.hint ? t('integrations.secret.set', { hint: stored.hint }) : t('integrations.secret.setNoHint')}</span>
        </span>
        <Button
          size="sm"
          aria-label={t('integrations.secret.replaceNamed', { label })}
          onClick={() => {
            set({ mode: 'replace', value: '' });
          }}
        >
          {t('integrations.secret.replace')}
        </Button>
        {/* A required secret can only be replaced: removing it would leave the connection incomplete. */}
        {!field.required && (
          <Button
            size="sm"
            variant="ghost"
            aria-label={t('integrations.secret.removeNamed', { label })}
            onClick={() => set({ mode: 'clear', value: '' })}
          >
            {t('integrations.secret.remove')}
          </Button>
        )}
      </div>
    );
  } else if (draft.mode === 'clear') {
    control = (
      <div className="dam-intsecret" role="group" aria-labelledby={labelId}>
        <span className="dam-intsecret__state dam-intsecret__state--removed">
          <LockKey size={16} aria-hidden="true" />
          <span>{t('integrations.secret.removed')}</span>
        </span>
        <Button size="sm" aria-label={t('integrations.secret.undoNamed', { label })} onClick={() => set({ mode: 'keep' })}>
          {t('integrations.secret.undo')}
        </Button>
      </div>
    );
  } else {
    control = (
      <div className="dam-intsecret dam-intsecret--edit">
        <SecretInput
          id={id}
          value={draft.value}
          onChange={(value) => set({ value })}
          placeholder={field.placeholder ?? undefined}
          required={field.required && !isSet}
          invalid={!!error}
          describedBy={describedBy(field, !!error, helpId, errorId)}
          label={label}
          focusOnMount={isSet}
        />
        {isSet && (
          <Button
            size="sm"
            variant="ghost"
            aria-label={t('integrations.secret.cancelNamed', { label })}
            onClick={() => set({ mode: 'keep', value: '' })}
          >
            {t('integrations.secret.cancel')}
          </Button>
        )}
      </div>
    );
  }

  const editing = draft.mode === 'replace';
  return (
    <FieldFrame
      label={label}
      required={field.required}
      help={field.help}
      error={error}
      controlId={editing ? id : undefined}
      labelId={labelId}
      helpId={helpId}
      errorId={errorId}
    >
      {control}
    </FieldFrame>
  );
}

/** The password input with a show/hide toggle. The text is shown only while the toggle is on. `focusOnMount` moves focus into
 * the input when it appears because Replace was chosen. */
function SecretInput({
  id,
  value,
  onChange,
  placeholder,
  required,
  invalid,
  describedBy,
  label,
  focusOnMount,
}: {
  id: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  required: boolean;
  invalid: boolean;
  describedBy?: string;
  label: string;
  focusOnMount: boolean;
}) {
  const { t } = useTranslation();
  const [visible, setVisible] = useState(false);
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (focusOnMount) ref.current?.focus();
  }, [focusOnMount]);
  return (
    <span className="dam-intsecret__input">
      <input
        ref={ref}
        id={id}
        className="dam-input"
        type={visible ? 'text' : 'password'}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        required={required}
        aria-invalid={invalid ? true : undefined}
        aria-describedby={describedBy}
        autoComplete="new-password"
        autoCapitalize="off"
        autoCorrect="off"
        spellCheck={false}
        data-1p-ignore
        data-lpignore="true"
      />
      <IconButton
        label={t('integrations.secret.toggleNamed', { label })}
        size="sm"
        aria-pressed={visible}
        onClick={() => setVisible(!visible)}
      >
        {visible ? <EyeSlash size={16} aria-hidden="true" /> : <Eye size={16} aria-hidden="true" />}
      </IconButton>
    </span>
  );
}

/** A choice with a default is never blank: leaving it out means the default. */
const hasDefault = (f: FieldDto) => f.default !== undefined && f.default !== null && f.default !== '';

interface FieldControlProps {
  field: FieldDto;
  /** Text and select fields hold a string, a boolean field a boolean. */
  value: string | boolean;
  onChange: (value: string | boolean) => void;
  secretState?: SecretState;
  secretDraft?: SecretDraft;
  onSecretChange?: (next: SecretDraft) => void;
  error?: string;
}

/** One input drawn from a field descriptor: text, url, secret, select, boolean, number or textarea. */
export function FieldControl({ field, value, onChange, secretState, secretDraft, onSecretChange, error }: FieldControlProps) {
  const { t } = useTranslation();
  const id = useId();
  const [helpId, errorId] = [`${id}-help`, `${id}-error`];

  if (field.type === 'secret') {
    return (
      <SecretField
        field={field}
        stored={secretState}
        draft={secretDraft ?? { mode: 'replace', value: '' }}
        onChange={onSecretChange ?? (() => {})}
        error={error}
      />
    );
  }

  if (field.type === 'boolean') {
    return (
      <div className="dam-field">
        <Switch checked={value === true} onChange={onChange} label={field.label} hint={field.help ?? undefined} />
        {error && (
          <span className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {error}
          </span>
        )}
      </div>
    );
  }

  const text = typeof value === 'string' ? value : '';
  const common = {
    id,
    required: field.required,
    'aria-invalid': error ? (true as const) : undefined,
    'aria-describedby': describedBy(field, !!error, helpId, errorId),
  };
  let control: ReactNode;
  if (field.type === 'select') {
    const options = field.options ?? [];
    control = (
      <span className="dam-select-wrap">
        <select className="dam-select" value={text} onChange={(e) => onChange(e.target.value)} {...common}>
          {(text === '' || (!field.required && !hasDefault(field))) && <option value="">{t('integrations.form.choose')}</option>}
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
        <CaretDown size={14} weight="bold" aria-hidden="true" className="dam-select__chevron" />
      </span>
    );
  } else if (field.type === 'textarea') {
    control = (
      <textarea
        className="dam-input dam-intform__textarea"
        rows={4}
        value={text}
        placeholder={field.placeholder ?? undefined}
        onChange={(e) => onChange(e.target.value)}
        spellCheck={false}
        {...common}
      />
    );
  } else {
    control = (
      <input
        className="dam-input"
        type={field.type === 'number' ? 'number' : field.type === 'url' ? 'url' : 'text'}
        inputMode={field.type === 'url' ? 'url' : field.type === 'number' ? 'decimal' : undefined}
        step={field.type === 'number' ? 'any' : undefined}
        value={text}
        placeholder={field.placeholder ?? undefined}
        onChange={(e) => onChange(e.target.value)}
        autoComplete="off"
        spellCheck={false}
        {...common}
      />
    );
  }

  return (
    <FieldFrame
      label={field.label}
      required={field.required}
      help={field.help}
      error={error}
      controlId={id}
      helpId={helpId}
      errorId={errorId}
    >
      {control}
    </FieldFrame>
  );
}
