import { describe, expect, it } from 'vitest';
import { buildFolderTree, type FolderDto } from './folders';

function folder(overrides: Partial<FolderDto>): FolderDto {
  return {
    id: 'id',
    parentId: null,
    path: 'path',
    label: 'Label',
    name: 'name',
    inheritedMetadata: {},
    defaultWorkflowId: null,
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('buildFolderTree', () => {
  it('nests children under their parent', () => {
    const tree = buildFolderTree([
      folder({ id: 'f1', name: 'brand', label: 'Brand', parentId: null }),
      folder({ id: 'f2', name: 'roadster', label: 'Roadster', parentId: 'f1' }),
    ]);
    expect(tree).toEqual([{ id: 'f1', label: 'Brand', children: [{ id: 'f2', label: 'Roadster', children: [] }] }]);
  });

  it('sorts siblings by name', () => {
    const tree = buildFolderTree([
      folder({ id: 'b', name: 'sedan', label: 'Sedan', parentId: null }),
      folder({ id: 'a', name: 'coupe', label: 'Coupe', parentId: null }),
    ]);
    expect(tree.map((n) => n.id)).toEqual(['a', 'b']);
  });

  it('returns an empty tree for no folders', () => {
    expect(buildFolderTree([])).toEqual([]);
  });

  it('handles multiple root folders with no children', () => {
    const tree = buildFolderTree([folder({ id: 'f1', parentId: null }), folder({ id: 'f2', parentId: null })]);
    expect(tree).toHaveLength(2);
    expect(tree.every((n) => n.children?.length === 0)).toBe(true);
  });
});
