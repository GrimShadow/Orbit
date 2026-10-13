import { render, screen, within, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { useState } from 'react';
import {
  Breadcrumb,
  Button,
  CardMenu,
  Drawer,
  FacetList,
  FileTypeIcon,
  Input,
  Modal,
  Select,
  Skeleton,
  Table,
  Tabs,
  ThumbnailGrid,
  ToastProvider,
  Tree,
  useToast,
  type Facet,
  type TreeNode,
} from './index';

const tree: TreeNode[] = [
  {
    id: 'brand',
    label: 'Acme',
    children: [
      { id: 'roadster', label: 'Roadster', children: [{ id: 'roadster-gt', label: 'Roadster GT' }] },
      { id: 'trail', label: 'Trail X' },
    ],
  },
  { id: 'region', label: 'Regions' },
];

describe('Button', () => {
  it('is disabled and busy while loading', () => {
    render(<Button loading>Save</Button>);
    const b = screen.getByRole('button', { name: 'Save' });
    expect(b).toBeDisabled();
    expect(b).toHaveAttribute('aria-busy', 'true');
  });
});

describe('Field', () => {
  it('links label, hint and error to the control', () => {
    render(<Input label="Title" hint="Shown in search" error="Required" />);
    const input = screen.getByLabelText('Title');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription('Shown in search Required');
  });
});

describe('Table', () => {
  const rows = [
    { id: '1', name: 'Alpha' },
    { id: '2', name: 'Beta' },
  ];
  it('exposes sort state and calls back', async () => {
    const onSort = vi.fn();
    render(
      <Table
        caption="Assets"
        rows={rows}
        getRowId={(r) => r.id}
        sort={{ key: 'name', direction: 'asc' }}
        onSortChange={onSort}
        columns={[{ key: 'name', header: 'Name', sortable: true }]}
      />,
    );
    expect(screen.getByRole('columnheader', { name: /Name/ })).toHaveAttribute('aria-sort', 'ascending');
    await userEvent.click(screen.getByRole('button', { name: /Name/ }));
    expect(onSort).toHaveBeenCalledWith({ key: 'name', direction: 'desc' });
    expect(screen.getByRole('table', { name: 'Assets' })).toBeInTheDocument();
  });
  it('shows empty message', () => {
    render(
      <Table
        caption="Assets"
        rows={[]}
        getRowId={(r: { id: string }) => r.id}
        columns={[{ key: 'id', header: 'Id' }]}
        emptyMessage="No assets"
      />,
    );
    expect(screen.getByText('No assets')).toBeInTheDocument();
  });
  it('renders a checkbox column and reports toggles by row id', async () => {
    const onToggle = vi.fn();
    render(
      <Table
        caption="Assets"
        rows={rows}
        getRowId={(r) => r.id}
        columns={[{ key: 'name', header: 'Name' }]}
        selectedIds={['1']}
        onToggleSelect={onToggle}
        selectLabel={(r) => `Select ${r.name}`}
      />,
    );
    expect(screen.getByRole('checkbox', { name: 'Select Alpha' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Select Beta' })).not.toBeChecked();
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Beta' }));
    expect(onToggle).toHaveBeenCalledWith('2', true);
  });
});

describe('Modal / Drawer', () => {
  function Harness() {
    const [open, setOpen] = useState(false);
    return (
      <>
        <Button onClick={() => setOpen(true)}>Open</Button>
        <Modal open={open} onClose={() => setOpen(false)} title="Delete asset">
          Are you sure?
        </Modal>
      </>
    );
  }
  it('opens with an accessible name and closes via the close button', async () => {
    render(<Harness />);
    expect(screen.queryByText('Are you sure?')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Open' }));
    const dlg = screen.getByRole('dialog', { name: 'Delete asset' });
    expect(within(dlg).getByText('Are you sure?')).toBeInTheDocument();
    await userEvent.click(within(dlg).getByRole('button', { name: 'Close' }));
    expect(screen.queryByText('Are you sure?')).not.toBeInTheDocument();
  });
  it('drawer renders its content when open', () => {
    render(
      <Drawer open onClose={() => {}} title="Details">
        Panel
      </Drawer>,
    );
    expect(screen.getByRole('dialog', { name: 'Details' })).toHaveClass('dam-dialog--drawer');
  });
});

describe('Toast', () => {
  function Pusher() {
    const { push } = useToast();
    return <Button onClick={() => push({ title: 'Saved', tone: 'success', durationMs: 0 })}>Go</Button>;
  }
  it('announces in a live region and can be dismissed', async () => {
    render(
      <ToastProvider>
        <Pusher />
      </ToastProvider>,
    );
    await userEvent.click(screen.getByRole('button', { name: 'Go' }));
    const region = screen.getByRole('region', { name: 'Notifications' });
    expect(region).toHaveAttribute('aria-live', 'polite');
    expect(within(region).getByRole('status')).toHaveTextContent('Saved');
    await userEvent.click(within(region).getByRole('button', { name: 'Dismiss' }));
    expect(within(region).queryByRole('status')).not.toBeInTheDocument();
  });
  it('auto-dismisses', () => {
    vi.useFakeTimers();
    function Auto() {
      const { push } = useToast();
      return <Button onClick={() => push({ title: 'Bye', durationMs: 1000 })}>Go</Button>;
    }
    render(
      <ToastProvider>
        <Auto />
      </ToastProvider>,
    );
    act(() => {
      screen.getByRole('button', { name: 'Go' }).click();
    });
    expect(screen.getByText('Bye')).toBeInTheDocument();
    act(() => {
      vi.advanceTimersByTime(1100);
    });
    expect(screen.queryByText('Bye')).not.toBeInTheDocument();
    vi.useRealTimers();
  });
});

describe('Tabs', () => {
  it('supports arrow-key navigation with roving tabindex', async () => {
    render(
      <Tabs
        label="Asset"
        tabs={[
          { id: 'a', label: 'Details', content: 'Panel A' },
          { id: 'b', label: 'Versions', content: 'Panel B' },
          { id: 'c', label: 'Rights', content: 'Panel C' },
        ]}
      />,
    );
    const [a, b] = screen.getAllByRole('tab');
    expect(a).toHaveAttribute('aria-selected', 'true');
    expect(b).toHaveAttribute('tabindex', '-1');
    a!.focus();
    await userEvent.keyboard('{ArrowRight}');
    expect(b).toHaveAttribute('aria-selected', 'true');
    expect(b).toHaveFocus();
    expect(screen.getByRole('tabpanel')).toHaveTextContent('Panel B');
    await userEvent.keyboard('{End}');
    expect(screen.getAllByRole('tab')[2]).toHaveFocus();
    await userEvent.keyboard('{ArrowRight}'); // wraps
    expect(a).toHaveFocus();
  });
});

describe('Tree', () => {
  it('follows the WAI-ARIA keyboard model', async () => {
    const onSelect = vi.fn();
    render(<Tree nodes={tree} label="Taxonomy" onSelect={onSelect} />);
    expect(screen.getByRole('tree', { name: 'Taxonomy' })).toBeInTheDocument();
    const brand = screen.getByRole('treeitem', { name: /Acme/ });
    expect(brand).toHaveAttribute('aria-expanded', 'false');
    brand.focus();
    await userEvent.keyboard('{ArrowRight}');
    expect(brand).toHaveAttribute('aria-expanded', 'true');
    await userEvent.keyboard('{ArrowRight}'); // into first child
    expect(screen.getByRole('treeitem', { name: 'Roadster' })).toHaveFocus();
    await userEvent.keyboard('{ArrowDown}');
    expect(screen.getByRole('treeitem', { name: 'Trail X' })).toHaveFocus();
    await userEvent.keyboard('{Enter}');
    expect(onSelect).toHaveBeenCalledWith('trail');
    await userEvent.keyboard('{ArrowLeft}'); // leaf: go to parent
    expect(brand).toHaveFocus();
    await userEvent.keyboard('{ArrowLeft}');
    expect(brand).toHaveAttribute('aria-expanded', 'false');
  });
});

describe('FacetList', () => {
  const facets: Facet[] = [
    {
      id: 'region',
      label: 'Region',
      options: [
        { value: 'north', label: 'North', count: 12 },
        { value: 'south', label: 'South', count: 3 },
      ],
    },
  ];
  it('toggles options and reports the selection', async () => {
    const onChange = vi.fn();
    render(<FacetList facets={facets} selected={{}} onChange={onChange} />);
    await userEvent.click(screen.getByRole('checkbox', { name: /North/ }));
    expect(onChange).toHaveBeenCalledWith({ region: ['north'] });
    expect(screen.getByRole('group', { name: 'Region' })).toBeInTheDocument();
  });
});

describe('ThumbnailGrid', () => {
  it('opens and selects items with accessible names', async () => {
    const onOpen = vi.fn();
    const onToggle = vi.fn();
    render(
      <ThumbnailGrid
        label="Assets"
        items={[{ id: '1', title: 'Roadster hero', subtitle: 'JPEG', badge: 'Published' }]}
        onOpen={onOpen}
        onToggleSelect={onToggle}
      />,
    );
    await userEvent.click(screen.getByRole('button', { name: /Roadster hero/ }));
    expect(onOpen).toHaveBeenCalledWith('1');
    await userEvent.click(screen.getByRole('checkbox', { name: 'Select Roadster hero' }));
    expect(onToggle).toHaveBeenCalledWith('1', true);
  });

  it('renders an optional icon and metadata line, and marks a selected card', () => {
    render(
      <ThumbnailGrid
        label="Assets"
        items={[
          {
            id: '1',
            title: 'Roadster hero',
            subtitle: 'Image',
            icon: <span data-testid="type-icon" />,
            meta: 'Updated 6 days ago',
          },
        ]}
        selectedIds={['1']}
        onToggleSelect={() => {}}
      />,
    );
    expect(screen.getByTestId('type-icon')).toBeInTheDocument();
    expect(screen.getByText('Updated 6 days ago')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Select Roadster hero' })).toBeChecked();
    expect(screen.getByRole('listitem')).toHaveClass('dam-thumb--selected');
  });
});

describe('FileTypeIcon', () => {
  it('falls back to a generic file icon for an unknown type', () => {
    const { container: known } = render(<FileTypeIcon type="image" data-testid="icon" />);
    const { container: unknown } = render(<FileTypeIcon type="something-new" data-testid="icon" />);
    // Different asset types render different icon markup (and an unmapped type still renders something, not nothing).
    expect(known.querySelector('svg')).toBeInTheDocument();
    expect(unknown.querySelector('svg')).toBeInTheDocument();
    expect(known.innerHTML).not.toBe(unknown.innerHTML);
  });
});

describe('Breadcrumb', () => {
  it('renders the root and ancestors as buttons, the current segment as text, and navigates on click', async () => {
    const onNavigate = vi.fn();
    render(
      <Breadcrumb
        label="Folder path"
        rootLabel="All folders"
        items={[
          { id: 'brand', label: 'Brand' },
          { id: 'roadster', label: 'Roadster' },
        ]}
        onNavigate={onNavigate}
      />,
    );
    expect(screen.getByRole('navigation', { name: 'Folder path' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'All folders' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Brand' })).toBeInTheDocument();
    expect(screen.getByText('Roadster')).toHaveAttribute('aria-current', 'page');

    await userEvent.click(screen.getByRole('button', { name: 'Brand' }));
    expect(onNavigate).toHaveBeenCalledWith('brand');
    await userEvent.click(screen.getByRole('button', { name: 'All folders' }));
    expect(onNavigate).toHaveBeenCalledWith(undefined);
  });

  it('renders only the current segment as text when at the root', () => {
    render(<Breadcrumb label="Folder path" rootLabel="All folders" items={[]} onNavigate={() => {}} />);
    expect(screen.getByText('All folders')).toHaveAttribute('aria-current', 'page');
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });
});

describe('accessibility (axe)', () => {
  it('has no violations across all components', async () => {
    const { container } = render(
      <ToastProvider>
        <main>
          <h1>Gallery</h1>
          <Button variant="primary">Primary</Button>
          <Button variant="danger">Danger</Button>
          <Input label="Title" hint="Hint" />
          <Input label="Broken" error="Required" />
          <Select
            label="Language"
            options={[
              { value: 'en', label: 'English' },
              { value: 'hi', label: 'हिन्दी' },
            ]}
          />
          <Table
            caption="Assets"
            rows={[{ id: '1', name: 'A' }]}
            getRowId={(r) => r.id}
            columns={[{ key: 'name', header: 'Name', sortable: true }]}
            onSortChange={() => {}}
          />
          <Table
            caption="Loading"
            rows={[]}
            getRowId={(r: { id: string }) => r.id}
            columns={[{ key: 'id', header: 'Id' }]}
            loading
          />
          <Tabs
            label="Sections"
            tabs={[
              { id: 'a', label: 'One', content: 'x' },
              { id: 'b', label: 'Two', content: 'y' },
            ]}
          />
          <Tree nodes={tree} label="Taxonomy" defaultExpanded={['brand']} />
          <FacetList
            facets={[{ id: 'r', label: 'Region', options: [{ value: 'n', label: 'North', count: 1 }] }]}
            selected={{}}
            onChange={() => {}}
          />
          <ThumbnailGrid
            label="Assets"
            items={[{ id: '1', title: 'One', icon: <FileTypeIcon type="image" />, meta: 'Updated 6 days ago' }]}
            selectedIds={['1']}
            onToggleSelect={() => {}}
          />
          <Breadcrumb
            label="Folder path"
            rootLabel="All folders"
            items={[{ id: 'brand', label: 'Brand' }]}
            onNavigate={() => {}}
          />
          <Skeleton />
        </main>
      </ToastProvider>,
    );
    expect(await axe(container)).toHaveNoViolations();
  });

  describe('CardMenu', () => {
    const actions = (onSelect: (k: string) => void) => [
      { key: 'open', label: 'Open details', onSelect: () => onSelect('open') },
      { key: 'submit', label: 'Submit for review', onSelect: () => onSelect('submit') },
    ];

    it('opens a menu on click, moves with arrow keys, runs the chosen action and closes', async () => {
      const chosen: string[] = [];
      render(<CardMenu label="Actions for Hero" actions={actions((k) => chosen.push(k))} />);
      const trigger = screen.getByRole('button', { name: 'Actions for Hero' });
      expect(trigger).toHaveAttribute('aria-expanded', 'false');

      await userEvent.click(trigger);
      expect(trigger).toHaveAttribute('aria-expanded', 'true');
      expect(screen.getByRole('menuitem', { name: 'Open details' })).toHaveFocus();

      await userEvent.keyboard('{ArrowDown}');
      expect(screen.getByRole('menuitem', { name: 'Submit for review' })).toHaveFocus();
      await userEvent.keyboard('{Enter}');
      expect(chosen).toEqual(['submit']);
      expect(screen.queryByRole('menu')).not.toBeInTheDocument();
      expect(trigger).toHaveFocus();
    });

    it('closes on Escape and returns focus to the trigger', async () => {
      render(<CardMenu label="Actions" actions={actions(() => {})} />);
      await userEvent.click(screen.getByRole('button', { name: 'Actions' }));
      await userEvent.keyboard('{Escape}');
      expect(screen.queryByRole('menu')).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Actions' })).toHaveFocus();
    });

    it('renders nothing when there are no actions', () => {
      const { container } = render(<CardMenu label="Actions" actions={[]} />);
      expect(container).toBeEmptyDOMElement();
    });

    it('is shown on a ThumbnailGrid card only when cardActions is given, with no axe violations', async () => {
      const { container } = render(
        <main>
          <ThumbnailGrid
            items={[{ id: 'a1', title: 'Hero' }]}
            label="Assets"
            cardActions={() => actions(() => {})}
            menuLabel={(t) => `Actions for ${t}`}
          />
        </main>,
      );
      expect(screen.getByRole('button', { name: 'Actions for Hero' })).toBeInTheDocument();
      expect(await axe(container)).toHaveNoViolations();
    });
  });
});
