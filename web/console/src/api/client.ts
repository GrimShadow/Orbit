import { config } from '../config';

export class ApiError extends Error {
  constructor(
    public status: number,
    public title: string,
    public detail?: string,
    public correlationId?: string,
  ) {
    super(title);
  }
}

/** Calls the DAM API with the user's bearer token and turns RFC 9457 problem+json into an ApiError. */
export async function apiFetch<T>(path: string, accessToken: string | undefined, init: RequestInit = {}): Promise<T> {
  const res = await fetch(`${config.apiBase}${path}`, {
    ...init,
    headers: { Accept: 'application/json', ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}), ...init.headers },
  });
  if (!res.ok) {
    let p: { title?: string; detail?: string; correlationId?: string } = {};
    try {
      p = await res.json();
    } catch {
      /* non-JSON error body */
    }
    throw new ApiError(
      res.status,
      p.title ?? res.statusText,
      p.detail,
      p.correlationId ?? res.headers.get('X-Correlation-ID') ?? undefined,
    );
  }
  return res.json() as Promise<T>;
}
