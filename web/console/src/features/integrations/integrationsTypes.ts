// Shapes of the Integrations API (`/api/v1/integrations`), written by hand from docs/adr/0040-integrations-framework.md ("Contract")
// because the generated schema (web/sdk-js/src/schema.ts) did not yet contain these routes. Once it does, replace each type here
// with `components['schemas'][...]` and delete the one cast in integrationsApi.ts (`useLooseClient`).
//
// Field labels, help text and connector names/descriptions are the server's own strings; everything else the page says is i18n.

export type FieldType = 'text' | 'url' | 'secret' | 'select' | 'boolean' | 'number' | 'textarea';
export type FieldGroup = 'connection' | 'authentication' | 'advanced';
export type ConnectorStatus = 'available' | 'preview' | 'planned';
export type ConnectionStatus = 'new' | 'ok' | 'error';
export type ActivityKind = 'auth' | 'test' | 'action';

export interface FieldOption {
  value: string;
  label: string;
}

/** Shows an authentication field only while the field named `field` holds one of `equals`. */
export interface ShowWhen {
  field: string;
  equals: string[];
}

export interface FieldDto {
  key: string;
  label: string;
  type: FieldType;
  required: boolean;
  help?: string | null;
  placeholder?: string | null;
  group: FieldGroup;
  options?: FieldOption[] | null;
  /** A string, number or boolean matching the field's type. */
  default?: unknown;
  showWhen?: ShowWhen | null;
}

export interface ConnectorDto {
  key: string;
  name: string;
  description: string;
  category: string;
  status: ConnectorStatus;
  authStrategies: string[];
  defaultAuthStrategy?: string | null;
  capabilities: string[];
  fields: FieldDto[];
}

export interface SecretState {
  set: boolean;
  /** The last four characters; never the value. */
  hint?: string | null;
}

export type ConfigValue = string | number | boolean;

export interface ConnectionDto {
  id: string;
  connector: string;
  name: string;
  enabled: boolean;
  config: Record<string, ConfigValue | null>;
  secrets: Record<string, SecretState>;
  status: ConnectionStatus;
  statusDetail?: string | null;
  lastTestedAt?: string | null;
  lastOkAt?: string | null;
  tokenExpiresAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ConnectionInput {
  /** Create only. */
  connector?: string;
  name: string;
  enabled: boolean;
  config: Record<string, ConfigValue>;
  /** A secret you send is replaced; one you leave out is kept. */
  secrets: Record<string, string>;
  /** Secrets to remove. */
  clearSecrets: string[];
}

export interface TestStepDto {
  name: string;
  ok: boolean;
  detail?: string | null;
  durationMs: number;
}

export interface TestResultDto {
  ok: boolean;
  durationMs: number;
  testedAt: string;
  steps: TestStepDto[];
}

export interface ActivityDto {
  id: string;
  kind: ActivityKind | string;
  method: string;
  /** Host and path only: no query string, no credentials. */
  target: string;
  statusCode?: number | null;
  ok: boolean;
  durationMs: number;
  error?: string | null;
  createdAt: string;
}

export interface ActivityPage {
  items: ActivityDto[];
  nextCursor?: string | null;
}
