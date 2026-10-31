import {
  canTestConnector,
  formFields,
  initialDraft,
  isDirty,
  isVisible,
  mapServerErrors,
  strategyKey,
  toInput,
  validate,
  type Draft,
} from './formModel';
import { CONNECTORS, HTTP_FIELDS, connection, connector } from './testKit';

const http = connector();
const fields = formFields(http, { label: 'Authentication method', optionLabel: (s) => s });
const create = () => initialDraft(fields, null);

function withValues(d: Draft, values: Record<string, string | boolean>, secrets: Draft['secrets'] = {}): Draft {
  return { ...d, values: { ...d.values, ...values }, secrets: { ...d.secrets, ...secrets } };
}

describe('showWhen', () => {
  it('shows an authentication field only for the strategies it names', () => {
    const key = fields.find((f) => f.key === 'apiKey')!;
    const pass = fields.find((f) => f.key === 'password')!;
    const always = fields.find((f) => f.key === 'baseUrl')!;
    expect(isVisible(key, { authStrategy: 'api_key' })).toBe(true);
    expect(isVisible(key, { authStrategy: 'basic' })).toBe(false);
    expect(isVisible(pass, { authStrategy: 'basic' })).toBe(true);
    expect(isVisible(pass, { authStrategy: '' })).toBe(false);
    expect(isVisible(always, {})).toBe(true);
  });
});

describe('strategy field', () => {
  it('uses the descriptor select when there is one, defaulting to the connector default', () => {
    const noDefault = connector({
      fields: HTTP_FIELDS.map((f) => (f.key === 'authStrategy' ? { ...f, default: undefined } : f)),
    });
    const out = formFields(noDefault, { label: 'x', optionLabel: (s) => s });
    expect(out).toHaveLength(HTTP_FIELDS.length);
    expect(out.find((f) => f.key === 'authStrategy')!.default).toBe('api_key');
  });

  it('adds a select when the descriptor lists strategies and dependent fields but not the choice itself', () => {
    const bare = connector({ fields: HTTP_FIELDS.filter((f) => f.key !== 'authStrategy') });
    expect(strategyKey(bare)).toBe('authStrategy');
    const out = formFields(bare, { label: 'Authentication method', optionLabel: (s) => `<${s}>` });
    const added = out.find((f) => f.key === 'authStrategy')!;
    expect(added.type).toBe('select');
    expect(added.options).toEqual([
      { value: 'none', label: '<none>' },
      { value: 'api_key', label: '<api_key>' },
      { value: 'basic', label: '<basic>' },
    ]);
    expect(added.default).toBe('api_key');
    // It sits first among the authentication fields.
    const auth = out.filter((f) => f.group === 'authentication').map((f) => f.key);
    expect(auth[0]).toBe('authStrategy');
  });

  it('adds nothing for a connector with a single strategy and no dependent fields', () => {
    const one = connector({ authStrategies: ['none'], fields: [HTTP_FIELDS[0]!] });
    expect(strategyKey(one)).toBeNull();
    expect(formFields(one, { label: 'x', optionLabel: (s) => s })).toEqual([HTTP_FIELDS[0]]);
  });
});

describe('initialDraft', () => {
  it('starts a new connection from the defaults, enabled, with every secret awaiting a value', () => {
    const d = create();
    expect(d).toMatchObject({ name: '', enabled: true });
    expect(d.values).toMatchObject({
      authStrategy: 'api_key',
      apiKeyHeader: 'X-Api-Key',
      timeoutSeconds: '30',
      sendJson: true,
      baseUrl: '',
    });
    expect(d.secrets.apiKey).toEqual({ mode: 'replace', value: '' });
  });

  it('starts an edit from the stored config and never prefills a secret', () => {
    const d = initialDraft(fields, connection({ name: 'Acme CMS', enabled: false }));
    expect(d.name).toBe('Acme CMS');
    expect(d.enabled).toBe(false);
    expect(d.values.baseUrl).toBe('https://api.example.test');
    expect(d.secrets.apiKey).toEqual({ mode: 'keep', value: '' });
    expect(d.secrets.password).toEqual({ mode: 'replace', value: '' });
  });
});

describe('toInput', () => {
  it('sends the visible fields, leaves out hidden ones and empty optional ones, and names the connector on create', () => {
    const d = withValues(
      create(),
      { baseUrl: ' https://api.example.test ', username: 'ignored', testPath: '' },
      {
        apiKey: { mode: 'replace', value: 'k-123456' },
        password: { mode: 'replace', value: 'hidden-secret' },
      },
    );
    d.name = ' Acme CMS ';
    expect(toInput(http, fields, d, true)).toEqual({
      connector: 'http',
      name: 'Acme CMS',
      enabled: true,
      config: {
        baseUrl: 'https://api.example.test',
        authStrategy: 'api_key',
        apiKeyHeader: 'X-Api-Key',
        apiKeyIn: 'header',
        tokenPrefix: 'Bearer ',
        timeoutSeconds: 30,
        sendJson: true,
      },
      secrets: { apiKey: 'k-123456' },
      clearSecrets: [],
    });
  });

  it('switching the strategy swaps which fields and secrets are sent', () => {
    const d = withValues(
      create(),
      { baseUrl: 'https://api.example.test', authStrategy: 'basic', username: 'svc' },
      {
        apiKey: { mode: 'replace', value: 'stale' },
        password: { mode: 'replace', value: 'pw-9999' },
      },
    );
    const out = toInput(http, fields, d, true);
    expect(out.config).toMatchObject({ authStrategy: 'basic', username: 'svc' });
    expect(out.config).not.toHaveProperty('apiKeyHeader');
    expect(out.secrets).toEqual({ password: 'pw-9999' });
  });

  it('omits a stored secret that was not touched, sends a replacement and lists a removal in clearSecrets', () => {
    const stored = connection({ secrets: { apiKey: { set: true, hint: 'wxyz' } } });
    const kept = initialDraft(fields, stored);
    expect(toInput(http, fields, kept, false)).toMatchObject({ secrets: {}, clearSecrets: [] });
    const replaced = withValues(kept, {}, { apiKey: { mode: 'replace', value: 'new-key' } });
    expect(toInput(http, fields, replaced, false)).toMatchObject({ secrets: { apiKey: 'new-key' }, clearSecrets: [] });
    const emptied = withValues(kept, {}, { apiKey: { mode: 'replace', value: '' } });
    expect(toInput(http, fields, emptied, false)).toMatchObject({ secrets: {}, clearSecrets: [] });
    const cleared = withValues(kept, {}, { apiKey: { mode: 'clear', value: '' } });
    expect(toInput(http, fields, cleared, false)).toMatchObject({ secrets: {}, clearSecrets: ['apiKey'] });
  });

  it('does not clear or send the secret of a field that is hidden', () => {
    const d = withValues(initialDraft(fields, connection()), { authStrategy: 'none' }, { apiKey: { mode: 'clear', value: '' } });
    expect(toInput(http, fields, d, false)).toMatchObject({ secrets: {}, clearSecrets: [] });
  });

  it('sends text as typed, so a verbatim setting such as a token prefix keeps its trailing space', () => {
    const d = withValues(create(), { baseUrl: 'https://x.example.test' });
    expect(d.values.tokenPrefix).toBe('Bearer ');
    expect(toInput(http, fields, d, true).config.tokenPrefix).toBe('Bearer ');
    expect(toInput(http, fields, withValues(d, { tokenPrefix: '  ' }), true).config).not.toHaveProperty('tokenPrefix');
  });

  it('sends numbers as numbers and a false switch as false', () => {
    const d = withValues(create(), { baseUrl: 'https://x.example.test', timeoutSeconds: '12.5', sendJson: false });
    expect(toInput(http, fields, d, true).config).toMatchObject({ timeoutSeconds: 12.5, sendJson: false });
  });
});

describe('validate', () => {
  it('asks for the name and every visible required field, secret or not', () => {
    expect(validate(fields, create())).toEqual({
      name: 'required',
      'config.baseUrl': 'required',
      'secrets.apiKey': 'required',
    });
  });

  it('counts a stored secret that stays, and typed text, as a value; a removal is not', () => {
    const d = initialDraft(fields, connection());
    expect(validate(fields, d)).toEqual({});
    expect(validate(fields, withValues(d, {}, { apiKey: { mode: 'clear', value: '' } }))).toEqual({
      'secrets.apiKey': 'required',
    });
    expect(validate(fields, withValues(d, {}, { apiKey: { mode: 'replace', value: 'typed' } }))).toEqual({});
  });

  it('does not ask for hidden fields', () => {
    const d = withValues(create(), { baseUrl: 'https://x.example.test', authStrategy: 'none' });
    d.name = 'Acme';
    expect(validate(fields, d)).toEqual({});
  });

  it('checks that an address is http or https and a number is a number', () => {
    const d = withValues(
      create(),
      { baseUrl: 'ftp://files.example.test', timeoutSeconds: 'soon' },
      { apiKey: { mode: 'replace', value: 'k' } },
    );
    d.name = 'Acme';
    expect(validate(fields, d)).toEqual({ 'config.baseUrl': 'url', 'config.timeoutSeconds': 'number' });
  });
});

describe('isDirty', () => {
  it('is false for the stored values and true for any change, including a typed secret', () => {
    const base = initialDraft(fields, connection());
    expect(isDirty(http, fields, base, base)).toBe(false);
    expect(isDirty(http, fields, { ...base, name: 'Other' }, base)).toBe(true);
    expect(isDirty(http, fields, withValues(base, { testPath: '/x' }), base)).toBe(true);
    expect(isDirty(http, fields, withValues(base, {}, { apiKey: { mode: 'replace', value: 'k' } }), base)).toBe(true);
    expect(isDirty(http, fields, withValues(base, {}, { apiKey: { mode: 'clear', value: '' } }), base)).toBe(true);
    // Opening Replace and typing nothing changes nothing.
    expect(isDirty(http, fields, withValues(base, {}, { apiKey: { mode: 'replace', value: '' } }), base)).toBe(false);
  });
});

describe('mapServerErrors', () => {
  it('maps config.* and secrets.* keys (any case, any [n] suffix) onto fields and keeps the rest as general', () => {
    const out = mapServerErrors(
      {
        'config.baseUrl': ['Address is not allowed.'],
        'Secrets.ApiKey': ['Too short.', 'Not valid.'],
        Name: ['Name is taken.'],
        'config.nothing': ['Unknown field.'],
        '': ['Something else.'],
      },
      fields,
    );
    expect(out.byKey).toEqual({
      'config.baseUrl': 'Address is not allowed.',
      'secrets.apiKey': 'Too short. Not valid.',
      name: 'Name is taken.',
    });
    expect(out.other).toEqual(['Unknown field.', 'Something else.']);
  });
});

describe('a connector that only stores its settings', () => {
  const pads4 = CONNECTORS.find((c) => c.key === 'pads4')!;
  const pads4Fields = formFields(pads4, { label: 'Authentication method', optionLabel: (s) => s });

  it('can be tested only with the test capability', () => {
    expect(canTestConnector(http)).toBe(true);
    expect(canTestConnector(pads4)).toBe(false);
  });

  it('adds no authentication select and starts a new connection from the given name', () => {
    expect(pads4Fields.map((f) => f.key)).toEqual(['baseUrl', 'domain', 'username', 'password']);
    const draft = initialDraft(pads4Fields, null, 'PADS4');
    expect(draft.name).toBe('PADS4');
    expect(draft.secrets.password).toEqual({ mode: 'replace', value: '' });
    expect(initialDraft(pads4Fields, null).name).toBe('');
  });

  it('sends the address, domain and user name as settings and the password as a secret, and asks for all four', () => {
    const draft = initialDraft(pads4Fields, null, 'PADS4');
    expect(validate(pads4Fields, draft)).toEqual({
      'config.baseUrl': 'required',
      'config.domain': 'required',
      'config.username': 'required',
      'secrets.password': 'required',
    });
    const full = withValues(
      draft,
      { baseUrl: 'http://192.168.1.20:8080', domain: 'acme', username: 'ann' },
      { password: { mode: 'replace', value: 'pw-0042' } },
    );
    expect(validate(pads4Fields, full)).toEqual({});
    expect(toInput(pads4, pads4Fields, full, true)).toEqual({
      connector: 'pads4',
      name: 'PADS4',
      enabled: true,
      config: { baseUrl: 'http://192.168.1.20:8080', domain: 'acme', username: 'ann' },
      secrets: { password: 'pw-0042' },
      clearSecrets: [],
    });
  });
});
