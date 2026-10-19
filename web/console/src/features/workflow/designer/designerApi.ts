import { ApiError, unwrap, type components } from '@dam/sdk-js';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../../api/apiClient';
import { useAuth } from '../../../auth/AuthProvider';
import type { DefinitionIssue, WorkflowDefinition } from '../definition';
import type { WorkflowDto } from '../workflowApi';

export type RoleDto = components['schemas']['RoleDto'];
export type UserDto = components['schemas']['UserDto'];

export const isForbidden = (e: unknown) => e instanceof ApiError && e.status === 403;

/** Every role in the tenant (names are matched exactly, case included, by the server). */
export function useRoles() {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['wf-roles'],
    queryFn: async () => unwrap(await api.GET('/api/v1/roles')),
    enabled: status === 'authenticated',
    staleTime: 60_000,
    retry: false,
  });
}

const DIRECTORY_KEY = ['wf-users', 'directory'];

/** The first 200 active users, so assignee chips can show names. Anyone beyond that is found through search and
 * remembered here once picked. */
export function useUserDirectory() {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: DIRECTORY_KEY,
    queryFn: async () =>
      unwrap(await api.GET('/api/v1/users', { params: { query: { limit: 200, 'filter[status]': 'active' } } })).items,
    enabled: status === 'authenticated',
    staleTime: 60_000,
    retry: false,
  });
}

/** Name of a user id, or a short id when the directory does not know them. */
export function useUserNames() {
  const directory = useUserDirectory();
  const qc = useQueryClient();
  const names = new Map((directory.data ?? []).map((u) => [u.id, u.displayName || u.email]));
  return {
    nameOf: (id: string) => names.get(id) ?? `${id.slice(0, 8)}…`,
    known: (id: string) => names.has(id),
    remember: (u: UserDto) =>
      qc.setQueryData<UserDto[]>(DIRECTORY_KEY, (old) => (old?.some((x) => x.id === u.id) ? old : [...(old ?? []), u])),
  };
}

export function useUserSearch(q: string) {
  const api = useApiClient();
  const { status } = useAuth();
  const term = q.trim();
  return useQuery({
    queryKey: ['wf-users', 'search', term],
    queryFn: async () =>
      unwrap(await api.GET('/api/v1/users', { params: { query: { limit: 8, 'filter[status]': 'active', 'filter[q]': term } } }))
        .items,
    enabled: status === 'authenticated' && term.length > 0,
    staleTime: 30_000,
    retry: false,
  });
}

/** The server reports a bad definition as 422 with `errors: { "definition.steps[1].id": ["..."] }`; this turns that
 * into the same path-addressed issues the validate endpoint returns. */
export class DefinitionRejected extends Error {
  constructor(
    public issues: DefinitionIssue[],
    title: string,
  ) {
    super(title);
  }
}

interface Problem {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}

export function issuesFromProblem(errors: Record<string, string[]> | undefined): DefinitionIssue[] {
  return Object.entries(errors ?? {})
    .filter(([key]) => key === 'definition' || key.startsWith('definition.'))
    .flatMap(([key, messages]) =>
      messages.map((message) => ({ path: key === 'definition' ? '' : key.slice('definition.'.length), message })),
    );
}

/** Create or replace a workflow. Unlike the shared `useSaveWorkflow` it keeps the 422 field errors, so the designer can
 * point at the step that is wrong. */
export function useSaveDesign() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { id?: string; name: string; description: string; definition: WorkflowDefinition }) => {
      const body = { name: v.name.trim(), description: v.description.trim() || null, definition: v.definition };
      const res = v.id
        ? await api.PUT('/api/v1/workflows/{id}', { params: { path: { id: v.id } }, body })
        : await api.POST('/api/v1/workflows', { body });
      if (res.response.ok) return res.data as WorkflowDto;
      const problem = (res.error ?? {}) as Problem;
      const issues = issuesFromProblem(problem.errors);
      if (res.response.status === 422 && issues.length > 0) throw new DefinitionRejected(issues, problem.title ?? 'Invalid');
      throw new ApiError(res.response.status, problem.title ?? res.response.statusText, problem.detail);
    },
    onSuccess: (saved) => {
      qc.setQueryData<WorkflowDto[]>(['workflows', 'list'], (old) =>
        old ? (old.some((w) => w.id === saved.id) ? old.map((w) => (w.id === saved.id ? saved : w)) : [...old, saved]) : old,
      );
      void qc.invalidateQueries({ queryKey: ['workflows'] });
    },
  });
}

/** Sets or clears a folder's default workflow. */
export function useSetFolderDefault() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ folderId, workflowId }: { folderId: string; workflowId: string | null }) =>
      unwrap(
        await api.PATCH('/api/v1/folders/{id}', {
          params: { path: { id: folderId } },
          body: workflowId
            ? { name: null, inheritedMetadata: null, defaultWorkflowId: workflowId, clearDefaultWorkflow: false }
            : { name: null, inheritedMetadata: null, defaultWorkflowId: null, clearDefaultWorkflow: true },
        }),
      ),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['folders'] });
      void qc.invalidateQueries({ queryKey: ['workflows'] });
    },
  });
}

/** A 409 from delete says why in its title ("2 folder(s) use this workflow..."). */
export function conflictMessage(e: unknown): string | null {
  return e instanceof ApiError && e.status === 409 ? (e.detail ?? e.title) : null;
}
