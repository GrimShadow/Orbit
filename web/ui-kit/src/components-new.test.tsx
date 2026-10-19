import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { axe } from 'vitest-axe';
import { useState } from 'react';
import {
  Avatar,
  Badge,
  Button,
  Card,
  CheckboxGroup,
  EmptyState,
  IconButton,
  Images,
  Kbd,
  Logo,
  PageHeader,
  Plus,
  Rows,
  SegmentedControl,
  Select,
  SquaresFour,
  StatCard,
  Table,
  TagInput,
  Tabs,
  ThumbnailGrid,
  ThumbnailGridSkeleton,
  initialsOf,
} from './index';

describe('Badge', () => {
  it('keeps the plain dam-badge class for the neutral default and adds a tone modifier otherwise', () => {
    const { rerender } = render(<Badge>Draft</Badge>);
    expect(screen.getByText('Draft')).toHaveClass('dam-badge');
    expect(screen.getByText('Draft').className).toBe('dam-badge');
    rerender(
      <Badge tone="success" size="sm">
        Published
      </Badge>,
    );
    expect(screen.getByText('Published')).toHaveClass('dam-badge', 'dam-badge--success', 'dam-badge--sm');
  });
});

describe('IconButton', () => {
  it('exposes its label as the accessible name and the tooltip', () => {
    render(
      <IconButton label="Upload" variant="primary">
        <Plus aria-hidden="true" />
      </IconButton>,
    );
    const b = screen.getByRole('button', { name: 'Upload' });
    expect(b).toHaveAttribute('title', 'Upload');
    expect(b).toHaveClass('dam-btn', 'dam-btn--icon', 'dam-btn--primary');
  });
  it('is disabled and busy while loading', () => {
    render(
      <IconButton label="Save" loading>
        <Plus />
      </IconButton>,
    );
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save' })).toHaveAttribute('aria-busy', 'true');
  });
});

describe('CheckboxGroup', () => {
  function Harness({ onChange }: { onChange: (v: string[]) => void }) {
    const [value, setValue] = useState<string[]>(['web']);
    return (
      <CheckboxGroup
        label="Channels"
        hint="Where the asset may be used"
        value={value}
        onChange={(v) => {
          setValue(v);
          onChange(v);
        }}
        options={[
          { value: 'web', label: 'Web', count: 12 },
          { value: 'print', label: 'Print', count: 3 },
          { value: 'tv', label: 'TV', disabled: true },
        ]}
      />
    );
  }
  it('is a labelled fieldset of real checkboxes and reports the next selection', async () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    const group = screen.getByRole('group', { name: 'Channels' });
    expect(group).toHaveAccessibleDescription('Where the asset may be used');
    expect(within(group).getByRole('checkbox', { name: /Web/ })).toBeChecked();
    expect(within(group).getByRole('checkbox', { name: /TV/ })).toBeDisabled();
    expect(screen.getByText('12')).toHaveClass('dam-check__count');
    await userEvent.click(within(group).getByRole('checkbox', { name: /Print/ }));
    expect(onChange).toHaveBeenLastCalledWith(['web', 'print']);
    await userEvent.click(within(group).getByRole('checkbox', { name: /Web/ }));
    expect(onChange).toHaveBeenLastCalledWith(['print']);
  });
});

describe('TagInput', () => {
  function Harness({ onChange, initial = [] }: { onChange?: (v: string[]) => void; initial?: string[] }) {
    const [value, setValue] = useState<string[]>(initial);
    return (
      <TagInput
        label="Keywords"
        hint="Press Enter to add"
        placeholder="Add a keyword"
        suggestions={['Summer', 'Winter']}
        value={value}
        onChange={(v) => {
          setValue(v);
          onChange?.(v);
        }}
      />
    );
  }
  it('is a labelled control that adds on Enter or comma, normalises, ignores duplicates and removes on Backspace', async () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} />);
    const input = screen.getByLabelText('Keywords');
    expect(input).toHaveAccessibleDescription('Press Enter to add');
    await userEvent.type(input, '  Summer {Enter}');
    expect(onChange).toHaveBeenLastCalledWith(['summer']);
    await userEvent.type(input, 'beach,summer,Sea{Enter}');
    expect(onChange).toHaveBeenLastCalledWith(['summer', 'beach', 'sea']);
    expect(input).toHaveValue('');
    await userEvent.type(input, '{Backspace}');
    expect(onChange).toHaveBeenLastCalledWith(['summer', 'beach']);
    expect(screen.getByRole('list', { name: 'Keywords (selected)' }).children).toHaveLength(2);
  });
  it('has a labelled remove button per chip', async () => {
    const onChange = vi.fn();
    render(<Harness onChange={onChange} initial={['hero', 'print']} />);
    await userEvent.click(screen.getByRole('button', { name: 'Remove hero' }));
    expect(onChange).toHaveBeenLastCalledWith(['print']);
    expect(screen.getByLabelText('Keywords')).toHaveFocus();
  });
});

describe('Avatar', () => {
  it('derives two upper-case initials and names the image', () => {
    expect(initialsOf('Priya Natarajan')).toBe('PN');
    expect(initialsOf('priya')).toBe('P');
    expect(initialsOf('priya.natarajan@example.com')).toBe('PN');
    render(<Avatar name="Priya Natarajan" size="lg" />);
    const img = screen.getByRole('img', { name: 'Priya Natarajan' });
    expect(img).toHaveTextContent('PN');
    expect(img).toHaveAttribute('data-size', 'lg');
  });
});

describe('SegmentedControl', () => {
  it('is a group of aria-pressed buttons and reports the chosen value', async () => {
    const onChange = vi.fn();
    render(
      <SegmentedControl
        label="View"
        value="grid"
        onChange={onChange}
        options={[
          { value: 'grid', label: 'Grid', icon: <SquaresFour aria-hidden="true" /> },
          { value: 'list', label: 'List', icon: <Rows aria-hidden="true" /> },
        ]}
      />,
    );
    const group = screen.getByRole('group', { name: 'View' });
    expect(within(group).getByRole('button', { name: 'Grid' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(group).getByRole('button', { name: 'List' })).toHaveAttribute('aria-pressed', 'false');
    await userEvent.click(within(group).getByRole('button', { name: 'List' }));
    expect(onChange).toHaveBeenCalledWith('list');
  });
});

describe('PageHeader / EmptyState / StatCard / Card / Kbd / Logo', () => {
  it('renders the page title as a heading with eyebrow, actions and a toolbar row', () => {
    render(
      <PageHeader
        title="Library"
        eyebrow="Assets"
        description="Everything your team has uploaded."
        actions={<Button>Upload</Button>}
      >
        <Kbd>/</Kbd>
      </PageHeader>,
    );
    expect(screen.getByRole('heading', { level: 1, name: 'Library' })).toBeInTheDocument();
    expect(screen.getByText('Assets')).toHaveClass('dam-pagehead__eyebrow');
    expect(screen.getByRole('button', { name: 'Upload' })).toBeInTheDocument();
    expect(screen.getByText('/').tagName).toBe('KBD');
  });
  it('EmptyState composes icon, title, description and action', () => {
    render(
      <EmptyState
        icon={<Images />}
        title="No assets yet"
        description="Upload your first file to get started."
        action={<Button>Upload</Button>}
      />,
    );
    expect(screen.getByRole('heading', { name: 'No assets yet' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Upload' })).toBeInTheDocument();
  });
  it('StatCard shows label, value and hint with the tone class', () => {
    render(<StatCard label="Published" value={1284} hint="+12 this week" tone="success" icon={<Images />} />);
    expect(screen.getByText('1284')).toHaveClass('dam-stat__value');
    expect(screen.getByText('Published').closest('.dam-stat')).toHaveClass('dam-stat--success');
  });
  it('Card is polymorphic and maps padding/interactive to classes', () => {
    render(
      <Card as="section" padding="lg" interactive aria-label="Panel">
        Body
      </Card>,
    );
    const el = screen.getByText('Body');
    expect(el.tagName).toBe('SECTION');
    expect(el).toHaveClass('dam-card', 'dam-card--pad-lg', 'dam-card--interactive');
  });
  it('Logo is a named image with an optional wordmark', () => {
    const { container, rerender } = render(<Logo />);
    expect(screen.getByRole('img', { name: 'Orbit' })).toBeInTheDocument();
    expect(container).toHaveTextContent('Orbit');
    rerender(<Logo wordmark={false} size={16} />);
    expect(container.querySelector('svg')).toHaveAttribute('width', '16');
    expect(container.querySelector('.dam-logo__word')).toBeNull();
  });
});

describe('polished existing components', () => {
  it('Select keeps a native select with a decorative chevron and an error message linked to it', () => {
    render(<Select label="Language" error="Pick one" options={[{ value: 'en', label: 'English' }]} />);
    const sel = screen.getByLabelText('Language');
    expect(sel.tagName).toBe('SELECT');
    expect(sel).toHaveAttribute('aria-invalid', 'true');
    expect(sel).toHaveAccessibleDescription('Pick one');
    expect(sel.parentElement).toHaveClass('dam-select-wrap');
  });
  it('Table marks selected rows and renders a thumbnail cell', () => {
    render(
      <Table
        caption="Assets"
        rows={[{ id: '1', name: 'Alpha' }]}
        getRowId={(r) => r.id}
        selectedIds={['1']}
        onToggleSelect={() => {}}
        columns={[
          { key: 'thumb', header: '', render: () => <img className="dam-table__thumb" src="" alt="" /> },
          { key: 'name', header: 'Name' },
        ]}
      />,
    );
    expect(screen.getByRole('checkbox', { name: 'Select 1' }).closest('tr')).toHaveClass('dam-table__row--selected');
    expect(document.querySelector('.dam-table__thumb')).toBeInTheDocument();
  });
  it('Tabs render a decorative sliding indicator', () => {
    render(
      <Tabs
        label="Sections"
        tabs={[
          { id: 'a', label: 'One', content: 'x' },
          { id: 'b', label: 'Two', content: 'y' },
        ]}
      />,
    );
    expect(document.querySelector('.dam-tablist__indicator')).toHaveAttribute('aria-hidden', 'true');
  });
});

describe('accessibility (axe) - new components', () => {
  it('has no violations', async () => {
    const { container } = render(
      <main>
        <PageHeader
          title="Library"
          eyebrow="Assets"
          description="Everything uploaded."
          actions={<Button variant="primary">Upload</Button>}
        >
          <SegmentedControl
            label="View"
            value="grid"
            onChange={() => {}}
            options={[
              { value: 'grid', label: 'Grid' },
              { value: 'list', label: 'List' },
            ]}
          />
          <IconButton label="More">
            <Plus aria-hidden="true" />
          </IconButton>
        </PageHeader>
        <Badge tone="info">Info</Badge>
        <Badge tone="success">Success</Badge>
        <Badge tone="warning">Warning</Badge>
        <Badge tone="danger">Danger</Badge>
        <Badge tone="brand">Brand</Badge>
        <Badge>Neutral</Badge>
        <StatCard label="Assets" value={42} hint="+3 today" icon={<Images />} tone="brand" />
        <Card as="section" aria-label="Panel">
          <CheckboxGroup
            label="Channels"
            hint="Where it may be used"
            columns={2}
            value={['web']}
            onChange={() => {}}
            options={[
              { value: 'web', label: 'Web', count: 12 },
              { value: 'print', label: 'Print', count: 3, disabled: true },
            ]}
          />
          <TagInput label="Keywords" value={['hero']} onChange={() => {}} hint="Enter adds" suggestions={['summer']} />
        </Card>
        <EmptyState icon={<Images />} title="No assets yet" description="Upload one." action={<Button>Upload</Button>} />
        <Avatar name="Priya Natarajan" />
        <Kbd>K</Kbd>
        <Logo />
      </main>,
    );
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe('library-round additions', () => {
  it('ThumbnailGrid colours a card badge with its tone and marks the grid while something is selected', () => {
    const items = [
      { id: 'a', title: 'One', badge: 'Published', badgeTone: 'success' as const },
      { id: 'b', title: 'Two', badge: 'Draft' },
    ];
    const { rerender } = render(<ThumbnailGrid items={items} label="Assets" selectedIds={[]} onToggleSelect={() => {}} />);
    expect(screen.getByText('Published')).toHaveClass('dam-badge', 'dam-badge--success');
    expect(screen.getByText('Draft').className).toContain('dam-badge');
    expect(screen.getByRole('list', { name: 'Assets' })).not.toHaveClass('dam-grid--selecting');
    rerender(<ThumbnailGrid items={items} label="Assets" selectedIds={['a']} onToggleSelect={() => {}} />);
    expect(screen.getByRole('list', { name: 'Assets' })).toHaveClass('dam-grid--selecting');
  });

  it('ThumbnailGridSkeleton announces loading once and renders the requested number of placeholder cards', async () => {
    const { container } = render(<ThumbnailGridSkeleton count={4} label="Loading assets" />);
    expect(screen.getByRole('status')).toHaveTextContent('Loading assets');
    expect(container.querySelectorAll('.dam-thumb--skeleton')).toHaveLength(4);
    expect(await axe(container)).toHaveNoViolations();
  });

  it('Table applies a column className to header and cells, and accepts a node as a header', () => {
    render(
      <Table
        caption="Things"
        rows={[{ id: '1', a: 'x', b: 'y' }]}
        getRowId={(r) => r.id}
        columns={[
          { key: 'a', header: 'A' },
          { key: 'b', header: <span>Bee</span>, className: 'dam-col--hide-sm' },
        ]}
      />,
    );
    expect(screen.getByRole('columnheader', { name: 'Bee' })).toHaveClass('dam-col--hide-sm');
    expect(screen.getByRole('cell', { name: 'y' })).toHaveClass('dam-col--hide-sm');
    expect(screen.getByRole('cell', { name: 'x' })).not.toHaveAttribute('class');
  });
});
