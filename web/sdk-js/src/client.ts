import createFetchClient, { type Middleware } from 'openapi-fetch';
import type { paths } from './schema';

/** RFC 9457 problem+json, thrown so existing error handling (e.g. TanStack Query's `error`) keeps working unchanged. */
export class ApiError extends Error {
  constructor(
    public status: number,
    public title: string,
    public detail?: string,
    public correlationId?: string,
    /** The raw problem+json body, for the members a status and title cannot carry (`code`, `reasons`, per-field `errors`). */
    public problem?: Record<string, unknown>,
  ) {
    super(title);
  }
}

export interface ApiClientOptions {
  /** Origin the API is reachable at. Empty string (the default) means same-origin, relying on a dev-server proxy or
   * production same-origin deployment - paths already carry the full `/api/v1/...` prefix from the OpenAPI document. */
  baseUrl?: string;
  /** Read fresh on every request, so a refreshed token is picked up without recreating the client. */
  getAccessToken?: () => string | undefined;
}

export type ApiClient = ReturnType<typeof createFetchClient<paths>>;

/** The `Request` constructor needs an absolute URL to resolve a path against; a same-origin deployment (dev-server
 * proxy or production ingress alike) means "this page's own origin" is always the right one. */
function defaultBaseUrl(): string {
  return typeof window !== 'undefined' ? window.location.origin : '';
}

export function createApiClient({ baseUrl = defaultBaseUrl(), getAccessToken }: ApiClientOptions = {}): ApiClient {
  const client = createFetchClient<paths>({ baseUrl });
  const auth: Middleware = {
    onRequest({ request }) {
      const token = getAccessToken?.();
      if (token) request.headers.set('Authorization', `Bearer ${token}`);
      request.headers.set('Accept', 'application/json');
      return request;
    },
  };
  client.use(auth);
  return client;
}

/** A raw openapi-fetch result body shape close enough to RFC 9457 to read without pulling in a JSON schema for it. */
interface ProblemBody {
  title?: string;
  detail?: string;
  correlationId?: string;
}

/**
 * Turns an openapi-fetch `{ data, error, response }` result into a plain value, throwing {@link ApiError} for a
 * non-2xx response - the same contract `apiFetch` used before this client existed, so query hooks and their error
 * handling do not need to change shape just because the transport did.
 */
export function unwrap<T>({ data, error, response }: { data?: T; error?: unknown; response: Response }): T {
  if (response.ok) return data as T;
  const problem = (error ?? {}) as ProblemBody;
  throw new ApiError(
    response.status,
    problem.title ?? response.statusText,
    problem.detail,
    problem.correlationId ?? response.headers.get('X-Correlation-ID') ?? undefined,
    typeof error === 'object' && error !== null ? (error as Record<string, unknown>) : undefined,
  );
}

export type { paths } from './schema';
export type { components } from './schema';
