import { ApiError, unwrap, type components } from '@dam/sdk-js';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../../api/apiClient';
import { useAuth } from '../../../auth/AuthProvider';
import type { DefinitionIssue, WorkflowDefinition } from '../definition';
import type { WorkflowDto } from '../workflowApi';

export type RoleDto = components['schemas']['RoleDto'];

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

/** A person as the user directory returns them: enough to show a name and to pick them, nothing else. */
export interface DirectoryUser {
  id: string;
  displayName: string;
  email: string;
}
interface DirectoryPage {
  items: DirectoryUser[];
  nextCursor?: string | null;
}

/** `GET /api/v1/users/directory?q=&limit=&cursor=`: active people of the tenant, open to anyone who needs to pick people (unlike
 * `GET /users`, which needs `users.manage`). */
async function fetchDirectory(
  api: ReturnType<typeof useApiClient>,
  query: { q?: string; limit: number; cursor?: string },
): Promise<DirectoryPage> {
  return unwrap(await api.GET('/api/v1/users/directory', { params: { query } }));
}

const DIRECTORY_KEY = ['wf-users', 'directory'];
const PAGE = 50; // the most the directory returns in one page
const PAGES = 4; // so chips can name up to 200 people up front; anyone else is found through search

/** The first people of the directory (up to 200, by name), so chips can show names. Anyone beyond that is found through search
 * and remembered here once picked, or looked up by id. */
export function useUserDirectory() {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: DIRECTORY_KEY,
    queryFn: async () => {
      const all: DirectoryUser[] = [];
      let cursor: string | undefined;
      for (let i = 0; i < PAGES; i++) {
        const page = await fetchDirectory(api, { limit: PAGE, cursor });
        all.push(...page.items);
        if (!page.nextCursor) break;
        cursor = page.nextCursor;
      }
      return all;
    },
    enabled: status === 'authenticated',
    staleTime: 60_000,
    retry: false,
  });
}

/** Names of user ids, or a short id when nobody can say who they are. The directory knows active people; an id it does not
 * list (someone deactivated, or beyond the first 200) is looked up one by one, which only works for people allowed to read
 * users, so for everyone else it stays a short id. Pass the ids on screen so those lookups can happen. */
export function useUserNames(ids: string[] = []) {
  const directory = useUserDirectory();
  const api = useApiClient();
  const qc = useQueryClient();
  const names = new Map((directory.data ?? []).map((u) => [u.id, u.displayName || u.email]));
  const lookups = useQueries({
    queries: [...new Set(ids)]
      .filter((id) => directory.isSuccess && !names.has(id))
      .map((id) => ({
        queryKey: ['wf-users', 'by-id', id],
        queryFn: async () => unwrap(await api.GET('/api/v1/users/{id}', { params: { path: { id } } })),
        staleTime: 300_000,
        retry: false,
      })),
  });
  for (const l of lookups) if (l.data) names.set(l.data.id, l.data.displayName || l.data.email);
  return {
    nameOf: (id: string) => names.get(id) ?? `${id.slice(0, 8)}…`,
    known: (id: string) => names.has(id),
    remember: (u: DirectoryUser) =>
      qc.setQueryData<DirectoryUser[]>(DIRECTORY_KEY, (old) => (old?.some((x) => x.id === u.id) ? old : [...(old ?? []), u])),
  };
}

export function useUserSearch(q: string) {
  const api = useApiClient();
  const { status } = useAuth();
  const term = q.trim();
  return useQuery({
    queryKey: ['wf-users', 'search', term],
    queryFn: async () => (await fetchDirectory(api, { q: term, limit: 8 })).items,
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
