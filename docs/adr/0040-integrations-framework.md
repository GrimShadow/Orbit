# ADR-0040: Integrations framework

Status: accepted. Changes none of D1-D15. Applies the multi-client rule (ADR-0003): which systems a tenant connects, and their addresses and
credentials, are tenant data; nothing about a customer lives in code.

## Context

Step 6 of the spec has a PADS4 plugin that pulls from the DAM (the plugin signs in as an `ApiClient` and fetches renditions, A9). The DAM also needs
to reach out: connect to PADS4 by its own authentication API and then add content to PADS4's content library from the DAM, and later do the same with
other systems such as Canva. Each system has its own addresses, credentials and authentication call. This ADR builds the common framework so that a
specific connector is mostly configuration plus a small adapter; the specifics for each connector (what is pushed, in what shape) are separate steps.
It is DAM-initiated and complements, not replaces, the plugin's pull model.

## Decisions

1. **Connector** (code) is a kind of external system. It is registered in the application, has a descriptor (key, name, description, category, status,
   the authentication strategies it allows, its configuration fields, its capabilities) and an implementation of `IConnector` (`TestAsync`, and later
   the actions). Status is `available` (works now), `preview` (can be configured and tested, its actions are still to come) or `planned` (listed, cannot
   be created). Shipped now: **Generic HTTP API** (`http`, available), **PADS4** (`pads4`, preview: for now a *stored-credentials* connector that keeps
   a PADS4 server's address, sign-in domain, user name and password and makes no call; the real sign-in and the content-library actions come with its
   connector step) and **Canva** (`canva`, planned: it needs an OAuth authorisation-code flow, which is a later authentication strategy). A connector's
   `capabilities` say what it can do: `test` for Test connection, and later its actions. One with no capabilities only stores its settings.
2. **Connection** (tenant data, `integration_connections`, RLS, at most 50 per tenant) is one configured use of a connector: `name` (unique per
   tenant), `enabled`, `config` (jsonb, non-secret values by field key), the encrypted secrets, and status (`new`, `ok`, `error`) with detail,
   `last_tested_at`, `last_ok_at`, `token_expires_at`. Config is validated against the connector's field descriptors: required fields, types, and URL
   fields must be `http` or `https` and, for a connector that makes calls, pass the outbound URL guard when saved and on every call (ADR-0031: public
   addresses only unless the deployment allows private targets with `DAM_WEBHOOK_ALLOW_PRIVATE`, which an on-premise PADS4 will need). A connector that
   makes no calls is not guarded at save (see the implementation notes).
3. **Secrets** are encrypted with `ISecretProtector` (key `DAM_SECRETS_KEY`) per field. The API never returns a secret: a connection shows, per secret
   field, whether it is set and a four-character hint. On update a secret you send is replaced, one you leave out is kept, and `clearSecrets` removes
   named ones. Secrets, tokens and `Authorization` values never appear in logs, the activity log, audit events or error messages.
4. **Authentication strategies** are pure, unit-tested domain code that turns a connection's configuration and secrets into authenticated requests:
   - `none`.
   - `api_key`: a secret sent as a named header or query parameter.
   - `bearer`: a static secret token as `Authorization: Bearer`.
   - `basic`: username and secret password.
   - `oauth2_client_credentials`: token URL, client id, secret and optional scope; the token is fetched and cached.
   - `login_call`: the shape PADS4's authentication API needs. A request (method, URL or path, JSON or form body with placeholders `{{username}}`,
     `{{password}}`, `{{apiKey}}`, `{{clientId}}`, `{{clientSecret}}` filled from the connection's secrets and config) whose response yields a token (a JSON
     pointer such as `/access_token`) and optionally a lifetime (a JSON pointer in seconds, or a fixed number of seconds); the token is then sent as a
     configurable header (default `Authorization`, prefix `Bearer `).
   **Token lifecycle:** a fetched token is cached encrypted on the connection with its expiry and reused until 60 seconds before it ends; a single-flight
   *lease* on the connection's row stops two workers logging in at once (see the implementation notes: a lease, not a held lock); a `401` on a call triggers one
   new login and one retry, never a loop. A token's lifetime is the value the response gives (a JSON pointer, in seconds), else the connection's fixed lifetime
   (60 to 86,400 seconds), else 300 seconds; never more than 86,400. A wrong guess costs one `401` and one new login.
5. **Outbound calls** go through the existing guarded outbound HTTP client (timeouts, size caps, no redirects to private addresses, the URL guard).
   Every call a connector makes is recorded in `integration_calls`: connection, kind (`auth`, `test`, `action`), method, host and path (no query string, no
   credentials in URLs), status code, ok, duration, a sanitised error message, time. No request or response bodies or headers are stored. The latest 500
   rows per connection are kept (a daily cleanup in the scheduler).
6. **Test connection** (`POST /integrations/connections/{id}/test`) runs the connector's test: authenticate, then, when the connection has a `testPath`,
   one `GET` of it. It returns the steps (`Authenticate`, `Test request`) with their result and duration, and updates the connection's status. A test always
   signs in afresh, so it proves the credentials as they are now (and saves the new token). A test may run at most once every 5 seconds per connection; a
   sooner one is a `429` with a `Retry-After` header and a clear message.
7. **Actions come next, on this framework.** The extension point is declared now (`capabilities`, an `IConnectorAction` shape and an
   `integration_deliveries` log with retries, modelled on webhook deliveries) but no action is implemented: pushing an asset into PADS4's content library
   is the PADS4 connector step.
8. **Permissions and audit.** One new permission `integrations.manage` (Admin) covers the catalog, connections, tests and the activity log. Events
   `integration.connection.created|updated|deleted|tested` go through the normal domain-event path with no secret content.

## Contract (`/api/v1/integrations`, all `integrations.manage`)

| Route | Result |
|---|---|
| `GET /connectors` | `ConnectorDto[]` |
| `GET /connections`, `POST /connections` | `ConnectionDto[]`, `ConnectionDto` (201) |
| `GET /connections/{id}`, `PUT /connections/{id}`, `DELETE /connections/{id}` | `ConnectionDto`, `ConnectionDto`, 204 |
| `POST /connections/{id}/test` | `TestResultDto` |
| `GET /connections/{id}/activity?limit&cursor` | `{items: ActivityDto[], nextCursor}` |

`ConnectorDto {key, name, description, category, status, authStrategies[], defaultAuthStrategy, capabilities[], fields[]}`;
`ConnectorFieldDto {key, label, type (text|url|secret|select|boolean|number|textarea), required, help, placeholder, group (connection|authentication|advanced),
options[{value,label}], default, showWhen{field, equals[]}}` (`showWhen` shows an authentication field only for some strategies; the type is
`ConnectorFieldDto`, with `ConnectorFieldOptionDto` and `ConnectorShowWhenDto`, because the metadata schema already owns the name `FieldDto`, and `config` is a dictionary of unknowns in the OpenAPI document);
`ConnectionDto {id, connector, name, enabled, config{}, secrets{key: {set, hint}}, status, statusDetail, lastTestedAt, lastOkAt, tokenExpiresAt,
createdAt, updatedAt}`; `ConnectionInput {connector (create only), name, enabled, config{}, secrets{}, clearSecrets[]}`; `TestResultDto {ok, durationMs,
testedAt, steps[{name, ok, detail, durationMs}]}`; `ActivityDto {id, kind, method, target, statusCode, ok, durationMs, error, createdAt}`. Field errors
are 422 problem answers keyed `config.<field>` or `secrets.<field>`.

## Implementation notes (the backend step)

What the build settled, and where it refines the text above.

- **Single-flight is a lease, not an advisory lock.** The first design said "a Postgres advisory lock or equivalent". A session advisory lock would pin a database
  connection for the length of a third party's sign-in (up to the call timeout), and a transaction-scoped one would be a transaction around an HTTP call, which
  ADR-0038 rules out (and the audit-chain lock must never be held across one either). So the sign-in claims a *lease* with one conditional `UPDATE`
  (`login_lease_until`, on the database's clock, valid for the call timeout plus 15 seconds): the caller that wins signs in and stores the token and clears the
  lease in one statement; the others poll (150 ms) and use the token once it is there, or take the lease when it is released or expires (a crashed worker never
  blocks anyone for more than that). Nothing is held open across the HTTP request. A caller that has just been refused with a `401` does not reuse a saved token
  equal to the one refused, but does use a newer one another caller saved meanwhile, so a burst of `401`s makes one new sign-in, not many.
- **Calls never run inside a transaction.** Connector calls write with single statements that commit by themselves (the token, the lease, the activity row), and
  `ConnectionRuntime` refuses to run when a transaction is open. Test connection is therefore a query handler that does the calls and then dispatches
  `RecordConnectionTestCommand` (status update, `integration.connection.tested` event), so the usual command, validator, handler, event, outbox path still applies
  to the state change.
- **Fields.** The HTTP connectors share one set of field keys: `baseUrl`, `testPath`, `authStrategy`, `apiKeyIn`, `apiKeyName`, `apiKey` (secret), `bearerToken`
  (secret), `username`, `password` (secret), `tokenUrl`, `clientId`, `clientSecret` (secret), `scope`, `loginMethod` (`POST` or `PUT`), `loginUrl` (a path on the base
  URL, or a full address), `loginBodyType` (`json`, `form`, `none`), `loginBody`, `tokenPointer` (default `/access_token`), `expiryPointer`, `expirySeconds`,
  `tokenHeader` (default `Authorization`), `tokenPrefix` (default `Bearer `). The same key serves several strategies (`username` and `password` for basic
  authentication and for the `{{username}}`/`{{password}}` placeholders; `apiKey`, `clientId`, `clientSecret` likewise), and `showWhen` lists them all. A default
  fills only a field that applies to the chosen strategy. OAuth 2 client credentials reads `/access_token` and `/expires_in`. Generic HTTP offers all six, defaulting
  to `api_key`. (The first build also gave PADS4 a login-call/bearer/api-key form on these shared fields; PADS4 is now a stored-credentials connector, see below.)
- **Login body templates.** Placeholders are `{{username}}`, `{{password}}`, `{{apiKey}}`, `{{clientId}}`, `{{clientSecret}}` (spaces inside the braces allowed, names
  case-sensitive). Substitution is one pass: a value that itself contains `{{...}}` is inserted as text. A JSON body needs each placeholder inside a string
  (otherwise 422) and the filled-in text must be valid JSON; values are JSON-escaped. A form body percent-encodes values. A placeholder whose value is not set
  is a failure that names the placeholder, never a value.
- **Secrets.** Each secret is encrypted with `ISecretProtector` under a purpose bound to the connection id and field key (`integration-secret/<id>/<key>`), the cached
  token under `integration-token/<id>`: a ciphertext cannot be moved between connections, fields or uses. A secret is trimmed and may not contain control characters or
  exceed 4,096 characters. The hint is the last four characters, and `null` for a secret shorter than 8. `secrets` in a response has one entry per secret field of the
  connector. An empty or missing secret on update means "keep". Changing any setting or secret drops the saved token and returns the status to `new` (`lastOkAt` keeps
  the history); renaming or switching `enabled` does not.
- **Errors never carry content.** A call's error text is built from fixed wording and the status code. No response body, header or request value is ever put into
  a result, the status detail, the activity log, an event or a log line; on top of that every message is passed through a sanitiser that replaces known secrets
  and tokens (also in their URL-encoded, JSON-escaped and base64 forms) and removes control characters. A query-string API key is not in the activity log (host and
  path only) and, with the default OpenTelemetry HTTP instrumentation, not in traces either.
- **Limits.** 50 connections per tenant (`422` on `name`), names unique ignoring case (`409`), text 500 characters, text areas 4,000, URLs 2,000, settings 16,000
  in all, a header name 64, a prefix 32, secrets 4,096. Header names that the HTTP stack controls (`Host`, `Content-Length`, `Cookie`, `Sec-*`, `Proxy-*` ...) cannot
  carry a credential. Request paths must stay on the connection's address (no full URL, `..`, spaces or fragment). Redirects are never followed.
- **Rate limit.** The test claim is one conditional `UPDATE` on `test_claimed_at`, so two requests (or two API instances) at once let exactly one through.
- **Errors.** `Error` gains `TooManyRequests` (answered `429` with `Retry-After` and `retryAfterSeconds`).
- **Activity** is paged by the call's id (a version 7 id, so newest first is id order) with the usual `{items, nextCursor}` page; limit 50, up to 200. The cleanup
  finds connections with more than 500 calls through the read-only system connection and deletes the surplus through a tenant-scoped scope (RLS applies to the delete).
- **Tables and roles.** `integration_connections` (the app role may select, insert, update, delete) and `integration_calls` (select, insert, delete; never update),
  both with forced row-level security; the system role may only select `integration_calls`. A unique index on `(tenant_id, lower(name))` is created in the migration.

### PADS4 as a stored-credentials preview connector (the form-only step)

- **What it is.** `pads4` has exactly four fields, all required and all in the `connection` group: `baseUrl` (url, "PADS4 server URL"), `domain` (text, the PADS4
  sign-in domain), `username` (text) and `password` (secret). `baseUrl`, `domain` and `username` are `config`; the password is an encrypted, write-only secret like
  any other (never returned, hint only, kept when left out of an update). It has no authentication strategy (`authStrategies` empty, `defaultAuthStrategy`
  null, so no `authStrategy` setting and no login-call or token fields) and no capabilities, and its status stays `preview`. It is implemented by
  `StoredCredentialsConnector`, which makes no outbound call. The Generic HTTP connector and the whole strategy machinery are unchanged (and are what the
  framework tests now exercise).
- **Why the URL guard is skipped at save for it.** The guard exists to stop Orbit from being made to request an internal address. A connector that never
  requests anything cannot be abused that way, and a PADS4 server is usually on the customer's private network, so refusing a private address would only
  block the one thing the form is for. The shape rules still apply (absolute `http`/`https`, no credentials in the URL, at most 2,000 characters; other text at
  most 500). The test is "makes no calls" (`ConnectorDescriptor.MakesCalls`: no capabilities); when PADS4 gains `test` and its actions it is guarded at save and on
  every call again, with no other change.
- **Status.** A connection of such a connector is created and updated with status `new` and the detail "Saved. Orbit does not connect to PADS4 yet."
  (English in the API; the console words it itself). `POST /connections/{id}/test` answers `409` ("Testing is not available for this connector yet.") before
  claiming the rate limit, calling anything or recording anything; no event, activity row or `test_claimed_at` is written. Activity is simply empty.
- **Console.** The card says "Saved settings only" and offers "Add PADS4 connection"; the dialog shows the four fields and a calm note, with no Test and no
  "Save and test"; the list shows a "Saved" badge with the note instead of the last-test and token facts. The test for hiding Test is the missing `test`
  capability, not the connector's key. A required secret (the password) offers Replace only, never Remove.
- **next step.** The real PADS4 sign-in and the content-library calls: give the descriptor the `test` capability (and the strategy or sign-in call PADS4 needs),
  replace `StoredCredentialsConnector` by a connector built on `ConnectionRuntime`, and migrate any saved connection's settings if the fields change.

### Configuration added

| Variable | Default | Meaning |
|---|---|---|
| `DAM_INTEGRATIONS_TIMEOUT_SECONDS` | `15` (1 to 60) | The longest one call to another system may take, sign-in and API calls alike. Connect timeouts follow `DAM_WEBHOOK_TIMEOUT_SECONDS`. |
| `DAM_INTEGRATIONS_CLEANUP_CRON` | `0 30 3 * * ?` | When the scheduler trims each connection's activity log to its latest 500 calls. |

Address rules are the webhook ones (`DAM_WEBHOOK_ALLOW_PRIVATE`), and so is the secrets key (`DAM_SECRETS_KEY`; without it secrets cannot be saved or read and a test says so).

## Console

An **Integrations** page (admin sidebar, `integrations.manage`): the catalog as cards (PADS4 and Generic HTTP API available or preview, Canva marked
planned), the connections as a list with status, last test, an enabled switch and Test, Edit and Delete; a connect/edit dialog drawn from the
connector's field descriptors (fields appear by `showWhen` as the authentication strategy changes; secrets show "set" with a hint and a Replace
action); a "Recent activity" panel per connection.

## Consequences

- One migration adds `integration_connections` and `integration_calls` (RLS, grants), the `integrations.manage` permission is synced at API start, and the
  scheduler gets a daily cleanup job.
- A new connector is a descriptor plus an adapter registered in DI; no schema change per connector.
- The PADS4 and Canva connector steps will define their actions, and a Canva authorisation-code strategy, on top of this.
