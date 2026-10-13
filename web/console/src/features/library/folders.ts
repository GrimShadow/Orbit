import { unwrap, type components } from '@dam/sdk-js';
import type { BreadcrumbItem, SelectOption, TreeNode } from '@dam/ui-kit';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';

/** `name` is the folder's display name (renameable); `label` is its slug, the immutable segment of its dotted `path` -
 * so anything shown to a person uses `name`. */
export type FolderDto = components['schemas']['FolderDto'];

export function useFolders() {
  const api = useApiClient();
  return useQuery({
    queryKey: ['folders'],
    queryFn: async () => unwrap(await api.GET('/api/v1/folders')),
  });
}

/** Builds the tree ui-kit's <Tree> renders from the flat, path-ordered list `GET /folders` returns - `?tree=true`
 * exists too, but its OpenAPI-documented response type (FolderDto[]) does not actually match what it returns at
 * runtime (FolderNodeDto[]); building the tree here instead sidesteps that mismatch entirely and needs no fix on
 * the backend to stay correctly typed. */
export function buildFolderTree(folders: readonly FolderDto[]): TreeNode[] {
  const byParent = new Map<string, FolderDto[]>();
  for (const folder of folders) {
    const key = folder.parentId ?? '';
    const siblings = byParent.get(key);
    if (siblings) siblings.push(folder);
    else byParent.set(key, [folder]);
  }
  for (const siblings of byParent.values()) siblings.sort((a, b) => a.name.localeCompare(b.name));

  const build = (parentKey: string): TreeNode[] =>
    (byParent.get(parentKey) ?? []).map((folder) => ({ id: folder.id, label: folder.name, children: build(folder.id) }));
  return build('');
}

/** A flat, depth-indented option list for a plain <select> (the upload form's folder picker) - built from the same
 * path-ordered list `buildFolderTree` uses, indenting by each folder's own ltree-style dotted path depth. */
export function folderOptions(folders: readonly FolderDto[]): SelectOption[] {
  return [...folders]
    .sort((a, b) => a.path.localeCompare(b.path))
    .map((folder) => ({ value: folder.id, label: `${'—'.repeat(folder.path.split('.').length - 1)} ${folder.name}`.trim() }));
}

/** The ancestor chain (root-first) for the currently-selected folder, for a breadcrumb trail above the library grid -
 * walks `parentId` rather than parsing `path`, since the path is a name-based ltree key (not ids) and a folder can be
 * renamed without its id changing. */
export function folderBreadcrumb(folders: readonly FolderDto[], folderId: string | undefined): BreadcrumbItem[] {
  if (!folderId) return [];
  const byId = new Map(folders.map((f) => [f.id, f]));
  const chain: BreadcrumbItem[] = [];
  for (let current = byId.get(folderId); current; current = current.parentId ? byId.get(current.parentId) : undefined) {
    chain.unshift({ id: current.id, label: current.name });
  }
  return chain;
}

/** Folder create / rename / delete - the backend has always had these (`POST/PATCH/DELETE /folders`); the console just
 * had no way to reach them. Each refreshes the folder list so the tree, breadcrumb and pickers all update. */
export function useFolderMutations() {
  const api = useApiClient();
  const queryClient = useQueryClient();
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['folders'] });
  const create = useMutation({
    mutationFn: async ({ name, parentId }: { name: string; parentId: string | null }) =>
      unwrap(await api.POST('/api/v1/folders', { body: { parentId, name, label: null, inheritedMetadata: null } })),
    onSuccess: refresh,
  });
  const rename = useMutation({
    mutationFn: async ({ id, name }: { id: string; name: string }) =>
      unwrap(
        await api.PATCH('/api/v1/folders/{id}', {
          params: { path: { id } },
          body: { name, inheritedMetadata: null, defaultWorkflowId: null, clearDefaultWorkflow: false },
        }),
      ),
    onSuccess: refresh,
  });
  const remove = useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE('/api/v1/folders/{id}', { params: { path: { id } } })),
    onSuccess: refresh,
  });
  return { create, rename, remove };
}
