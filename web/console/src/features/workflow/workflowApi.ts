import { unwrap, type components } from '@dam/sdk-js';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';
import { useAuth } from '../../auth/AuthProvider';
import type { WorkflowDefinition } from './definition';

export type WorkflowDto = components['schemas']['WorkflowDto'];
export type WorkflowTask = components['schemas']['WorkflowTaskDto'];
export type WorkflowBinding = components['schemas']['WorkflowBindingDto'];
export type WorkflowInstanceView = NonNullable<components['schemas']['WorkflowInstanceViewDto']>;
export type WorkflowStepView = components['schemas']['WorkflowStepViewDto'];
export type TaskSummary = components['schemas']['TaskSummaryDto'];
export type WorkflowIssue = components['schemas']['WorkflowErrorDto'];

export type TaskScope = 'me' | 'all';
export type TaskStatus = 'pending' | 'decided' | 'all';

/** The definition the server stores is opaque JSON; the designer owns its typed shape. */
export const asDefinition = (w: Pick<WorkflowDto, 'definition'>) => w.definition as unknown as WorkflowDefinition;

const invalidateWorkflow = (qc: ReturnType<typeof useQueryClient>) => {
  void qc.invalidateQueries({ queryKey: ['workflows'] });
  void qc.invalidateQueries({ queryKey: ['tasks'] });
  void qc.invalidateQueries({ queryKey: ['assetWorkflow'] });
};

/** Every workflow definition (the list carries the full definition, so the designer needs no second fetch). */
export function useWorkflows(enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['workflows', 'list'],
    queryFn: async () => unwrap(await api.GET('/api/v1/workflows')),
    enabled: enabled && status === 'authenticated',
  });
}

export function useSaveWorkflow() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { id?: string; name: string; description: string; definition: WorkflowDefinition }) => {
      const body = { name: v.name, description: v.description || null, definition: v.definition };
      return v.id
        ? unwrap(await api.PUT('/api/v1/workflows/{id}', { params: { path: { id: v.id } }, body }))
        : unwrap(await api.POST('/api/v1/workflows', { body }));
    },
    onSuccess: () => invalidateWorkflow(qc),
  });
}

export function useDeleteWorkflow() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE('/api/v1/workflows/{id}', { params: { path: { id } } })),
    onSuccess: () => invalidateWorkflow(qc),
  });
}

/** Server-side validation, the authority behind the designer's instant local checks. Never fails for a bad definition. */
export function useValidateWorkflow() {
  const api = useApiClient();
  return useMutation({
    mutationFn: async (definition: WorkflowDefinition) =>
      unwrap(await api.POST('/api/v1/workflows/validate', { body: { definition } })),
  });
}

export function useWorkflowBindings(enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['workflows', 'bindings'],
    queryFn: async () => unwrap(await api.GET('/api/v1/workflows/bindings')),
    enabled: enabled && status === 'authenticated',
  });
}

export function useReplaceBindings() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (bindings: { workflowId: string; assetType: string | null; region: string | null }[]) =>
      unwrap(await api.PUT('/api/v1/workflows/bindings', { body: { bindings } })),
    onSuccess: () => invalidateWorkflow(qc),
  });
}

/** The task inbox, newest first; pages accumulate. */
export function useTasks(opts: { assignee?: TaskScope; status?: TaskStatus; limit?: number; enabled?: boolean } = {}) {
  const api = useApiClient();
  const { status: auth } = useAuth();
  const { assignee = 'me', status = 'pending', limit = 50, enabled = true } = opts;
  return useInfiniteQuery({
    queryKey: ['tasks', 'list', assignee, status, limit],
    initialPageParam: undefined as string | undefined,
    queryFn: async ({ pageParam }) =>
      unwrap(await api.GET('/api/v1/tasks', { params: { query: { assignee, status, limit, cursor: pageParam } } })),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    enabled: enabled && auth === 'authenticated',
  });
}

/** Counts for the nav badge and dashboard; refreshed every minute so an SLA breach shows up within about a minute. */
export function useTaskSummary(enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['tasks', 'summary'],
    queryFn: async () => unwrap(await api.GET('/api/v1/tasks/summary')),
    enabled: enabled && status === 'authenticated',
    refetchInterval: 60_000,
    staleTime: 30_000,
  });
}

export function useDecideTask() {
  const api = useApiClient();
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (v: { id: string; decision: 'approve' | 'reject'; comment?: string }) =>
      unwrap(
        await api.POST('/api/v1/tasks/{id}/decision', {
          params: { path: { id: v.id } },
          body: { decision: v.decision, comment: v.comment ?? null },
        }),
      ),
    onSuccess: () => {
      invalidateWorkflow(qc);
      void qc.invalidateQueries({ queryKey: ['asset'] });
      void qc.invalidateQueries({ queryKey: ['assets'] });
      void qc.invalidateQueries({ queryKey: ['home'] });
    },
  });
}

/** An asset's workflow, or `null` when none applies to it. Only fetched while the asset is (or has been) in review. */
export function useAssetWorkflow(assetId: string, enabled = true) {
  const api = useApiClient();
  const { status } = useAuth();
  return useQuery({
    queryKey: ['assetWorkflow', assetId],
    queryFn: async () =>
      (unwrap(await api.GET('/api/v1/assets/{id}/workflow', { params: { path: { id: assetId } } })).instance ??
        null) as WorkflowInstanceView | null,
    enabled: enabled && status === 'authenticated',
    staleTime: 15_000,
  });
}

export const isOverdue = (t: Pick<WorkflowTask, 'slaBreached' | 'status'>) => t.slaBreached && t.status === 'pending';
