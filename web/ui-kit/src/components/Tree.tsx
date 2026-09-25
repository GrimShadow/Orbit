import { useMemo, useRef, useState, type KeyboardEvent } from 'react';

export interface TreeNode {
  id: string;
  label: string;
  children?: TreeNode[];
}

interface TreeProps {
  nodes: TreeNode[];
  label: string;
  selectedId?: string;
  onSelect?: (id: string) => void;
  defaultExpanded?: string[];
}

interface Flat {
  node: TreeNode;
  parentId?: string;
  level: number;
}

/** WAI-ARIA tree: roving tabindex, ↑↓ move, → expand/enter, ← collapse/parent, Home/End, Enter/Space select. */
export function Tree({ nodes, label, selectedId, onSelect, defaultExpanded = [] }: TreeProps) {
  const [expanded, setExpanded] = useState(() => new Set(defaultExpanded));
  const [focusId, setFocusId] = useState(selectedId ?? nodes[0]?.id);
  const root = useRef<HTMLUListElement>(null);

  const visible = useMemo(() => {
    const out: Flat[] = [];
    const walk = (ns: TreeNode[], level: number, parentId?: string) => {
      for (const n of ns) {
        out.push({ node: n, parentId, level });
        if (n.children?.length && expanded.has(n.id)) walk(n.children, level + 1, n.id);
      }
    };
    walk(nodes, 1);
    return out;
  }, [nodes, expanded]);

  const focus = (id: string | undefined) => {
    if (!id) return;
    setFocusId(id);
    root.current?.querySelector<HTMLElement>(`[data-node-id="${CSS.escape(id)}"]`)?.focus();
  };
  const toggle = (id: string, open?: boolean) =>
    setExpanded((s) => {
      const n = new Set(s);
      if (open ?? !n.has(id)) n.add(id);
      else n.delete(id);
      return n;
    });

  const onKey = (e: KeyboardEvent, f: Flat, i: number) => {
    const hasKids = !!f.node.children?.length;
    const open = expanded.has(f.node.id);
    switch (e.key) {
      case 'ArrowDown':
        focus(visible[i + 1]?.node.id);
        break;
      case 'ArrowUp':
        focus(visible[i - 1]?.node.id);
        break;
      case 'Home':
        focus(visible[0]?.node.id);
        break;
      case 'End':
        focus(visible[visible.length - 1]?.node.id);
        break;
      case 'ArrowRight':
        if (hasKids && !open) toggle(f.node.id, true);
        else if (hasKids) focus(visible[i + 1]?.node.id);
        break;
      case 'ArrowLeft':
        if (hasKids && open) toggle(f.node.id, false);
        else focus(f.parentId);
        break;
      case 'Enter':
      case ' ':
        onSelect?.(f.node.id);
        break;
      default:
        return;
    }
    e.preventDefault();
    e.stopPropagation();
  };

  const render = (ns: TreeNode[], level: number, parentId?: string) =>
    ns.map((n) => {
      const i = visible.findIndex((v) => v.node.id === n.id);
      const hasKids = !!n.children?.length;
      const open = expanded.has(n.id);
      const flat: Flat = { node: n, parentId, level };
      return (
        <li
          key={n.id}
          role="treeitem"
          className="dam-treeitem"
          data-node-id={n.id}
          aria-level={level}
          aria-expanded={hasKids ? open : undefined}
          aria-selected={selectedId === n.id}
          tabIndex={focusId === n.id ? 0 : -1}
          onKeyDown={(e) => onKey(e, flat, i)}
          onFocus={(e) => {
            if (e.target === e.currentTarget) setFocusId(n.id);
          }}
          onClick={(e) => {
            e.stopPropagation();
            onSelect?.(n.id);
            if (hasKids) toggle(n.id);
            focus(n.id);
          }}
        >
          <div className="dam-treeitem__row">
            <span className="dam-caret" aria-hidden="true">
              {hasKids ? (open ? '▾' : '▸') : ''}
            </span>
            {n.label}
          </div>
          {hasKids && open && <ul role="group">{render(n.children!, level + 1, n.id)}</ul>}
        </li>
      );
    });

  return (
    <ul ref={root} role="tree" aria-label={label} className="dam-tree">
      {render(nodes, 1)}
    </ul>
  );
}
