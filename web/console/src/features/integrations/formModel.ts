// The pure rules of the connect/edit form: which fields show, the draft a form edits, what is sent to the server and the
// checks made before sending. Kept out of the components so they can be tested without rendering.
import type { ConfigValue, ConnectionDto, ConnectionInput, ConnectorDto, FieldDto, FieldGroup } from './integrationsTypes';

export const GROUP_ORDER: FieldGroup[] = ['connection', 'authentication', 'advanced'];

/** `keep`: the stored secret stays (omitted from the request). `replace`: a typed value is sent. `clear`: the key goes into
 * `clearSecrets`. A secret that is not stored yet is always `replace`. */
export type SecretMode = 'keep' | 'replace' | 'clear';
export interface SecretDraft {
  mode: SecretMode;
  value: string;
}

export interface Draft {
  name: string;
  enabled: boolean;
  /** Non-secret fields by key: text as typed, booleans as booleans. */
  values: Record<string, string | boolean>;
  secrets: Record<string, SecretDraft>;
}

/** The key of the authentication-strategy field. The descriptor names it in the `showWhen.field` of the fields it controls;
 * `authStrategy` is the fallback when a connector lists several strategies without saying. */
export function strategyKey(connector: ConnectorDto): string | null {
  const referenced = connector.fields.find((f) => f.showWhen)?.showWhen?.field;
  if (referenced) return referenced;
  return connector.authStrategies.length > 1 ? 'authStrategy' : null;
}

/** The connector's fields, plus an authentication-strategy select when the descriptor leaves one out (it lists the allowed
 * `authStrategies` and the fields that depend on the choice, so the choice itself must be offered). */
export function formFields(
  connector: ConnectorDto,
  strategy: { label: string; optionLabel: (strategy: string) => string },
): FieldDto[] {
  const key = strategyKey(connector);
  if (!key || connector.authStrategies.length === 0) return connector.fields;
  if (connector.fields.some((f) => f.key === key)) {
    // The descriptor has the select; the connector's default strategy fills in when the field names none.
    return connector.fields.map((f) =>
      f.key === key && (f.default === undefined || f.default === null) && connector.defaultAuthStrategy
        ? { ...f, default: connector.defaultAuthStrategy }
        : f,
    );
  }
  const field: FieldDto = {
    key,
    label: strategy.label,
    type: 'select',
    required: true,
    group: 'authentication',
    options: connector.authStrategies.map((s) => ({ value: s, label: strategy.optionLabel(s) })),
    default: connector.defaultAuthStrategy ?? connector.authStrategies[0],
  };
  // The strategy comes first in its group, as the other authentication fields depend on it.
  const firstAuth = connector.fields.findIndex((f) => f.group === 'authentication');
  const out = [...connector.fields];
  out.splice(firstAuth < 0 ? out.length : firstAuth, 0, field);
  return out;
}

/** Whether `field` shows for the current values. Only `showWhen` hides a field. */
export function isVisible(field: FieldDto, values: Draft['values']): boolean {
  if (!field.showWhen) return true;
  const current = values[field.showWhen.field];
  return field.showWhen.equals.includes(typeof current === 'string' ? current : String(current ?? ''));
}

const asText = (v: unknown) => (v === null || v === undefined ? '' : String(v));

function initialValue(field: FieldDto, stored: ConfigValue | null | undefined): string | boolean {
  const from = stored ?? field.default;
  if (field.type === 'boolean') return from === true || from === 'true';
  return asText(from);
}

/** The draft a form starts from: stored values when editing, the descriptor's defaults when creating. Secrets are never
 * prefilled. */
export function initialDraft(fields: FieldDto[], connection: ConnectionDto | null): Draft {
  const values: Draft['values'] = {};
  const secrets: Draft['secrets'] = {};
  for (const f of fields) {
    if (f.type === 'secret') {
      const stored = connection?.secrets[f.key]?.set === true;
      secrets[f.key] = { mode: stored ? 'keep' : 'replace', value: '' };
    } else values[f.key] = initialValue(f, connection?.config[f.key]);
  }
  return { name: connection?.name ?? '', enabled: connection?.enabled ?? true, values, secrets };
}

/** What the request carries. Fields hidden by `showWhen` are left out entirely (values and secrets alike), empty optional
 * values are omitted, and a stored secret that was not touched is omitted so the server keeps it. */
export function toInput(connector: ConnectorDto, fields: FieldDto[], draft: Draft, creating: boolean): ConnectionInput {
  const config: Record<string, ConfigValue> = {};
  const secrets: Record<string, string> = {};
  const clearSecrets: string[] = [];
  for (const f of fields) {
    if (!isVisible(f, draft.values)) continue;
    if (f.type === 'secret') {
      const s = draft.secrets[f.key];
      if (!s) continue;
      if (s.mode === 'replace' && s.value !== '') secrets[f.key] = s.value;
      else if (s.mode === 'clear') clearSecrets.push(f.key);
      continue;
    }
    const raw = draft.values[f.key];
    if (f.type === 'boolean') {
      config[f.key] = raw === true;
      continue;
    }
    const text = typeof raw === 'string' ? raw : '';
    if (text.trim() === '') continue;
    // Text is sent as typed: some settings are verbatim (a token prefix such as "Bearer " ends in a space, which the
    // descriptor does not flag), and the server trims the rest itself. Addresses, numbers and choices are trimmed.
    const value = f.type === 'text' || f.type === 'textarea' ? text : text.trim();
    if (f.type === 'number') {
      const n = Number(value);
      config[f.key] = Number.isFinite(n) ? n : value;
    } else config[f.key] = value;
  }
  const input: ConnectionInput = { name: draft.name.trim(), enabled: draft.enabled, config, secrets, clearSecrets };
  if (creating) input.connector = connector.key;
  return input;
}

export type FieldProblem = 'required' | 'url' | 'number';

/** Errors keyed `name`, `config.<field>` or `secrets.<field>`: the same keys the server uses for its 422 answers. */
export type FormErrors = Record<string, FieldProblem>;

const isHttpUrl = (s: string) => {
  if (!/^https?:\/\/\S+$/i.test(s)) return false;
  try {
    return !!new URL(s).hostname;
  } catch {
    return false;
  }
};

/** The checks a person can be told about before anything is sent; the server still validates everything. */
export function validate(fields: FieldDto[], draft: Draft): FormErrors {
  const errors: FormErrors = {};
  if (draft.name.trim() === '') errors.name = 'required';
  for (const f of fields) {
    if (!isVisible(f, draft.values)) continue;
    if (f.type === 'secret') {
      const s = draft.secrets[f.key];
      // A stored secret that stays is a value; typed text is a value; a removed or empty one is not.
      const has = s?.mode === 'keep' || (s?.mode === 'replace' && s.value !== '');
      if (f.required && !has) errors[`secrets.${f.key}`] = 'required';
      continue;
    }
    if (f.type === 'boolean') continue;
    const raw = draft.values[f.key];
    const text = (typeof raw === 'string' ? raw : '').trim();
    if (text === '') {
      if (f.required) errors[`config.${f.key}`] = 'required';
    } else if (f.type === 'url' && !isHttpUrl(text)) errors[`config.${f.key}`] = 'url';
    else if (f.type === 'number' && !Number.isFinite(Number(text))) errors[`config.${f.key}`] = 'number';
  }
  return errors;
}

/** Whether the form differs from what is stored (secrets typed, replaced or removed count). */
export function isDirty(connector: ConnectorDto, fields: FieldDto[], draft: Draft, initial: Draft): boolean {
  return JSON.stringify(toInput(connector, fields, draft, false)) !== JSON.stringify(toInput(connector, fields, initial, false));
}

export interface MappedServerErrors {
  /** Messages by form key (`name`, `config.<field>`, `secrets.<field>`). */
  byKey: Record<string, string>;
  /** Messages the form has no field for. */
  other: string[];
}

/** Maps the server's 422 keys to the form's fields, ignoring case (ASP.NET may capitalise) and any `[0]` suffix. */
export function mapServerErrors(errors: Record<string, string[]>, fields: FieldDto[]): MappedServerErrors {
  const byKey: Record<string, string> = {};
  const other: string[] = [];
  for (const [rawKey, messages] of Object.entries(errors)) {
    const text = messages.join(' ');
    const key = rawKey.replace(/\[\d+\]$/, '');
    const m = /^(config|secrets)\.(.+)$/i.exec(key);
    let target: string | null = null;
    if (m) {
      const prefix = m[1]!.toLowerCase();
      const field = fields.find((f) => f.key.toLowerCase() === m[2]!.toLowerCase());
      if (field) target = `${prefix === 'secrets' || field.type === 'secret' ? 'secrets' : 'config'}.${field.key}`;
    } else if (key.toLowerCase() === 'name') target = 'name';
    if (target) byKey[target] = byKey[target] ? `${byKey[target]} ${text}` : text;
    else other.push(text);
  }
  return { byKey, other };
}
