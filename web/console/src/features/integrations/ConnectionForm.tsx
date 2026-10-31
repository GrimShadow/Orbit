import { Info } from '@phosphor-icons/react';
import { useEffect, useId, useMemo, useRef, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Drawer, useToast } from '../../ui';
import { Switch } from '../webhooks/Switch';
import { FieldControl, FieldFrame } from './FieldControl';
import { TestPanel } from './TestPanel';
import {
  GROUP_ORDER,
  canTestConnector,
  formFields,
  initialDraft,
  isDirty,
  isVisible,
  mapServerErrors,
  toInput,
  validate,
  type Draft,
  type FormErrors,
  type MappedServerErrors,
  type SecretDraft,
} from './formModel';
import { errorText, serverFieldErrors, useSaveConnection, useTestConnection } from './integrationsApi';
import type { ConnectionDto, ConnectorDto, FieldDto, FieldGroup } from './integrationsTypes';

interface FormProps {
  connector: ConnectorDto;
  /** The connection being edited, or `null` to create one. */
  connection: ConnectionDto | null;
  onClose: () => void;
}

const NO_SERVER_ERRORS: MappedServerErrors = { byKey: {}, other: [] };

function Form({ connector, connection, onClose }: FormProps) {
  const { t } = useTranslation();
  const { push } = useToast();
  const save = useSaveConnection();
  const test = useTestConnection();
  const nameId = useId();
  const summaryRef = useRef<HTMLDivElement>(null);
  const summaryId = useId();

  const fields = useMemo(
    () =>
      formFields(connector, {
        label: t('integrations.form.strategy'),
        optionLabel: (s) => t(`integrations.strategy.${s}`, { defaultValue: s }),
      }),
    [connector, t],
  );

  // `saved` is what the server holds: the connection being edited, or the one this form created and stayed open to test.
  const [saved, setSaved] = useState<ConnectionDto | null>(connection);
  // A connector that only stores settings has no Test: no "Save and test", no test result, a plain Save as the main action.
  const testable = canTestConnector(connector);
  // Such a connector is usually used once per system, so its own name is a sensible name to start from.
  const defaultName = testable ? '' : connector.name;
  const stored = useMemo(() => initialDraft(fields, saved, defaultName), [fields, saved, defaultName]);
  const [draft, setDraft] = useState<Draft>(() => initialDraft(fields, connection, defaultName));
  const [clientErrors, setClientErrors] = useState<FormErrors>({});
  const [serverErrors, setServerErrors] = useState<MappedServerErrors>(NO_SERVER_ERRORS);
  const [general, setGeneral] = useState<string | null>(null);
  const [focusTick, setFocusTick] = useState(0);

  const creating = saved === null;
  const dirty = creating || isDirty(connector, fields, draft, stored);

  useEffect(() => {
    if (focusTick > 0) summaryRef.current?.focus();
  }, [focusTick]);

  const problemText = (code: FormErrors[string]) => t(`integrations.form.problem.${code}`);
  const errorFor = (key: string): string | undefined => {
    const code = clientErrors[key];
    return code ? problemText(code) : serverErrors.byKey[key];
  };
  /** Editing a field retires the messages about it; the next save checks again. */
  const retire = (key: string) => {
    setClientErrors((prev) => {
      if (!(key in prev)) return prev;
      const { [key]: _gone, ...rest } = prev;
      void _gone;
      return rest;
    });
    setServerErrors((prev) => {
      if (!(key in prev.byKey)) return prev;
      const { [key]: _gone, ...rest } = prev.byKey;
      void _gone;
      return { ...prev, byKey: rest };
    });
  };

  const labelOf = (key: string) => {
    if (key === 'name') return t('integrations.form.name');
    const field = fields.find((f) => f.key === key.replace(/^(config|secrets)\./, ''));
    return field?.label ?? key;
  };
  const summary: string[] = [
    ...Object.entries(clientErrors).map(([k, code]) => `${labelOf(k)}: ${problemText(code)}`),
    ...Object.entries(serverErrors.byKey).map(([k, m]) => `${labelOf(k)}: ${m}`),
    ...serverErrors.other,
    ...(general ? [general] : []),
  ];

  const setValue = (key: string, value: string | boolean) => {
    setDraft((d) => ({ ...d, values: { ...d.values, [key]: value } }));
    retire(`config.${key}`);
  };
  const setSecret = (key: string, next: SecretDraft) => {
    setDraft((d) => ({ ...d, secrets: { ...d.secrets, [key]: next } }));
    retire(`secrets.${key}`);
  };

  const runTest = (id: string) => test.mutate(id);

  /** Saves, then either closes or (`thenTest`) stays open as the saved connection and runs its test. */
  const submit = (thenTest: boolean) => {
    const local = validate(fields, draft);
    setClientErrors(local);
    setServerErrors(NO_SERVER_ERRORS);
    setGeneral(null);
    if (Object.keys(local).length > 0) {
      setFocusTick((n) => n + 1);
      return;
    }
    if (thenTest && saved && !dirty) {
      runTest(saved.id);
      return;
    }
    test.reset();
    save.mutate(
      { id: saved?.id, body: toInput(connector, fields, draft, creating) },
      {
        onSuccess: (dto) => {
          if (thenTest) {
            // Stay open as the saved connection: typed secrets become "Set (…)" and the test shows below.
            setSaved(dto);
            setDraft(initialDraft(fields, dto));
            runTest(dto.id);
          } else {
            push({ title: t('integrations.saved', { name: dto.name }), tone: 'success' });
            onClose();
          }
        },
        onError: (err) => {
          const mapped = mapServerErrors(serverFieldErrors(err), fields);
          setServerErrors(mapped);
          const none = Object.keys(mapped.byKey).length === 0 && mapped.other.length === 0;
          setGeneral(none ? (errorText(err) ?? t('integrations.form.saveFailed')) : null);
          setFocusTick((n) => n + 1);
        },
      },
    );
  };

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    submit(false);
  };

  // A field with a group the page does not know is not lost: it lands in "Connection".
  const groupOf = (f: FieldDto): FieldGroup => (GROUP_ORDER.includes(f.group) ? f.group : 'connection');
  const groups = GROUP_ORDER.map((group) => ({
    group,
    fields: fields.filter((f) => groupOf(f) === group && isVisible(f, draft.values)),
  })).filter((g) => g.fields.length > 0);

  const busy = save.isPending;
  const testState = test.isPending ? 'pending' : test.isError ? 'error' : test.isSuccess ? 'done' : 'idle';

  return (
    <form className="dam-intform" onSubmit={onSubmit} noValidate aria-describedby={summary.length > 0 ? summaryId : undefined}>
      <p className="dam-hint">{t('integrations.form.intro', { name: connector.name })}</p>
      {!testable && (
        <p className="dam-intnote dam-intnote--info">
          <Info size={16} weight="bold" aria-hidden="true" />
          <span>{t('integrations.storedOnlyNote', { name: connector.name })}</span>
        </p>
      )}
      {summary.length > 0 && (
        <div
          ref={summaryRef}
          id={summaryId}
          className="dam-intnote dam-intnote--danger dam-intsummary"
          role="group"
          aria-label={t('integrations.form.summaryLabel')}
          tabIndex={-1}
        >
          <div>
            <strong>{t('integrations.form.summary', { count: summary.length })}</strong>
            <ul>
              {summary.map((m, i) => (
                <li key={`${i}-${m}`}>{m}</li>
              ))}
            </ul>
          </div>
        </div>
      )}

      <div className="dam-intform__section">
        <FieldFrame
          label={t('integrations.form.name')}
          required
          error={errorFor('name')}
          controlId={nameId}
          helpId={`${nameId}-help`}
          errorId={`${nameId}-error`}
        >
          <input
            id={nameId}
            className="dam-input"
            value={draft.name}
            onChange={(e) => {
              setDraft((d) => ({ ...d, name: e.target.value }));
              retire('name');
            }}
            maxLength={200}
            required
            autoComplete="off"
            aria-invalid={errorFor('name') ? true : undefined}
            aria-describedby={errorFor('name') ? `${nameId}-error` : undefined}
            placeholder={t('integrations.form.namePlaceholder')}
          />
        </FieldFrame>
        <Switch
          checked={draft.enabled}
          onChange={(enabled) => setDraft((d) => ({ ...d, enabled }))}
          label={t('integrations.form.enabled')}
          hint={t('integrations.form.enabledHint')}
        />
      </div>

      {groups.map(({ group, fields: list }) => (
        <section key={group} className="dam-intform__section" aria-labelledby={`${summaryId}-${group}`}>
          <h3 id={`${summaryId}-${group}`} className="dam-intform__heading">
            {t(`integrations.group.${group}`)}
          </h3>
          {list.map((f) => (
            <FieldControl
              key={f.key}
              field={f}
              value={draft.values[f.key] ?? (f.type === 'boolean' ? false : '')}
              onChange={(v) => setValue(f.key, v)}
              secretState={saved?.secrets[f.key]}
              secretDraft={draft.secrets[f.key]}
              onSecretChange={(next) => setSecret(f.key, next)}
              error={errorFor(f.type === 'secret' ? `secrets.${f.key}` : `config.${f.key}`)}
            />
          ))}
        </section>
      ))}

      {testable && (
        <TestPanel
          state={testState}
          result={test.data}
          error={test.error}
          onRetry={saved ? () => runTest(saved.id) : undefined}
        />
      )}

      <div className="dam-intform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('integrations.cancel')}
        </Button>
        <Button type="submit" variant={testable ? 'secondary' : 'primary'} loading={busy} disabled={test.isPending}>
          {creating ? t('integrations.form.create') : t('integrations.form.save')}
        </Button>
        {testable && (
          <Button variant="primary" loading={busy || test.isPending} onClick={() => submit(true)}>
            {dirty ? t('integrations.form.saveAndTest') : t('integrations.form.test')}
          </Button>
        )}
      </div>
    </form>
  );
}

export function ConnectionForm(props: FormProps & { open: boolean }) {
  const { t } = useTranslation();
  const { open, ...rest } = props;
  return (
    <Drawer
      open={open}
      title={
        rest.connection
          ? t('integrations.form.editTitle', { name: rest.connection.name })
          : t(canTestConnector(rest.connector) ? 'integrations.form.newTitle' : 'integrations.form.newTitleStored', {
              name: rest.connector.name,
            })
      }
      closeLabel={t('integrations.close')}
      onClose={rest.onClose}
    >
      <Form {...rest} />
    </Drawer>
  );
}
