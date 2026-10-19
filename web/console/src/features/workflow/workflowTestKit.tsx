// Shared fixtures and a fetch mock for the workflow-run tests (inbox, asset panel, nav badge, dashboard).
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { App } from '../../App';
import { AuthContext, type AuthState } from '../../auth/AuthProvider';
import type { TaskSummary, WorkflowInstanceView, WorkflowStepView, WorkflowTask } from './workflowApi';

export const signedIn: AuthState = {
  status: 'authenticated',
  name: 'Dev Admin',
  email: 'admin@dam.local',
  accessToken: 'tok',
  signIn: vi.fn(async () => {}),
  signOut: vi.fn(async () => {}),
};

export function renderAt(route: string) {
  return render(
    <AuthContext.Provider value={signedIn}>
      <MemoryRouter initialEntries={[route]}>
        <App />
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

export const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });

export interface Call {
  method: string;
  pathname: string;
  search: URLSearchParams;
  json: () => Promise<unknown>;
}

/** Answers `handler` first; falls back to the endpoints every page asks for (me, task summary, thumbnails) and 404s the rest. */
export function mockApi(
  handler: (call: Call) => Response | Promise<Response> | undefined,
  opts: { permissions?: string[]; summary?: Partial<TaskSummary> | null; thumbs?: Record<string, string | null> } = {},
) {
  const calls: Call[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (!(input instanceof Request)) throw new Error(`unexpected raw fetch: ${String(input)}`);
      const url = new URL(input.url);
      const call: Call = {
        method: input.method,
        pathname: url.pathname,
        search: url.searchParams,
        json: () => input.clone().json(),
      };
      calls.push(call);
      const custom = handler(call);
      if (custom) return custom;
      if (call.pathname === '/api/v1/me')
        return json({
          userId: 'u1',
          email: 'admin@dam.local',
          displayName: 'Dev Admin',
          tenantId: '0197a000-0000-7000-8000-000000000001',
          roles: ['Approver'],
          groups: [],
          permissions: opts.permissions ?? ['assets.read'],
          attributes: {},
          accessRuleCount: 1,
          inactiveRoles: [],
          status: 'active',
        });
      if (call.pathname === '/api/v1/tasks/summary')
        return opts.summary === null ? json({ title: 'Nope', status: 500 }, 500) : json(summary(opts.summary));
      if (call.pathname === '/api/v1/assets/links/batch') {
        const { assetIds } = (await call.json()) as { assetIds: string[] };
        return json(Object.fromEntries(assetIds.map((id) => [id, opts.thumbs?.[id] ?? null])));
      }
      return json({ title: 'Not found', status: 404 }, 404);
    }),
  );
  return calls;
}

export const hoursFromNow = (h: number) => new Date(Date.now() + h * 3_600_000 + 30_000).toISOString();

export function summary(over: Partial<TaskSummary> = {}): TaskSummary {
  return {
    pendingForMe: 0,
    overdueForMe: 0,
    pendingTotal: null,
    overdueTotal: null,
    completed30d: null,
    completedOnTime30d: null,
    slaAttainmentPercent: null,
    ...over,
  };
}

export function task(over: Partial<WorkflowTask> = {}): WorkflowTask {
  return {
    id: 't1',
    assetId: 'a1',
    assetTitle: 'Acme spring hero',
    assetType: 'image',
    workflowName: 'Two-level approval',
    stepId: 'review',
    stepName: 'Review',
    status: 'pending',
    assigneeRoles: ['Approver'],
    assigneeUserIds: [],
    dueAt: null,
    slaBreached: false,
    slaBreachedAt: null,
    createdAt: '2026-01-01T00:00:00Z',
    decision: null,
    decidedBy: null,
    decidedAt: null,
    comment: null,
    escalated: false,
    decidedByName: null,
    instanceId: 'i1',
    ...over,
  };
}

export const page = (items: WorkflowTask[], nextCursor: string | null = null) => ({ items, nextCursor });

export function step(over: Partial<WorkflowStepView> = {}): WorkflowStepView {
  return { id: 'review', name: 'Review', state: 'waiting', mode: 'any', slaHours: null, tasks: [], ...over };
}

export function instance(over: Partial<WorkflowInstanceView> = {}): NonNullable<WorkflowInstanceView> {
  return {
    id: 'i1',
    workflowName: 'Two-level approval',
    status: 'running',
    startedBy: 'u9',
    startedByName: 'Sam Author',
    startedAt: new Date(Date.now() - 3 * 3_600_000).toISOString(),
    completedAt: null,
    outcomeReason: null,
    outcomeStatus: null,
    autoPublish: false,
    steps: [],
    ...over,
  } as NonNullable<WorkflowInstanceView>;
}
