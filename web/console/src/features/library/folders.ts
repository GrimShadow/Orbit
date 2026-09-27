import { unwrap, type components } from '@dam/sdk-js';
import type { SelectOption, TreeNode } from '@dam/ui-kit';
import { useQuery } from '@tanstack/react-query';
import { useApiClient } from '../../api/apiClient';

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
    (byParent.get(parentKey) ?? []).map((folder) => ({ id: folder.id, label: folder.label, children: build(folder.id) }));
  return build('');
}

/** A flat, depth-indented option list for a plain <select> (the upload form's folder picker) - built from the same
 * path-ordered list `buildFolderTree` uses, indenting by each folder's own ltree-style dotted path depth. */
export function folderOptions(folders: readonly FolderDto[]): SelectOption[] {
  return [...folders]
    .sort((a, b) => a.path.localeCompare(b.path))
    .map((folder) => ({ value: folder.id, label: `${'—'.repeat(folder.path.split('.').length - 1)} ${folder.label}`.trim() }));
}
