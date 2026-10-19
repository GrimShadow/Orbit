import type { Meta, StoryObj } from '@storybook/react';
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
  SquaresFour,
  StatCard,
  TagInput,
  Table,
  ThumbnailGrid,
  UploadSimple,
} from '../index';

const meta: Meta = { title: 'Design system/Composition' };
export default meta;
type Story = StoryObj;

export const Badges: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
      <Badge>Draft</Badge>
      <Badge tone="info">In review</Badge>
      <Badge tone="success">Published</Badge>
      <Badge tone="warning">Expiring</Badge>
      <Badge tone="danger">Rejected</Badge>
      <Badge tone="brand">Featured</Badge>
      <Badge tone="success" size="sm">
        Small
      </Badge>
    </div>
  ),
};

export const IconButtons: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
      <IconButton label="Upload" variant="primary">
        <UploadSimple size={18} aria-hidden="true" />
      </IconButton>
      <IconButton label="Add" variant="secondary">
        <Plus size={18} aria-hidden="true" />
      </IconButton>
      <IconButton label="More">
        <Plus size={18} aria-hidden="true" />
      </IconButton>
      <IconButton label="Small" size="sm">
        <Plus size={16} aria-hidden="true" />
      </IconButton>
      <IconButton label="Saving" loading>
        <Plus size={18} aria-hidden="true" />
      </IconButton>
    </div>
  ),
};

export const PageHeaderStory: Story = {
  name: 'PageHeader',
  render: function Render() {
    const [view, setView] = useState('grid');
    return (
      <PageHeader
        eyebrow="Assets"
        title="Library"
        description="Everything your team has uploaded, with the newest files first."
        actions={
          <>
            <Button>Save search</Button>
            <Button variant="primary">Upload</Button>
          </>
        }
      >
        <SegmentedControl
          label="View"
          value={view}
          onChange={setView}
          options={[
            { value: 'grid', label: 'Grid', icon: <SquaresFour size={16} aria-hidden="true" /> },
            { value: 'list', label: 'List', icon: <Rows size={16} aria-hidden="true" /> },
          ]}
        />
        <span className="dam-hint">
          Press <Kbd>/</Kbd> to search
        </span>
      </PageHeader>
    );
  },
};

export const Stats: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 16, gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))' }}>
      <StatCard label="Assets" value="12,480" hint="+312 this week" icon={<Images size={20} aria-hidden="true" />} tone="brand" />
      <StatCard
        label="Published"
        value="9,117"
        hint="73% of library"
        icon={<Images size={20} aria-hidden="true" />}
        tone="success"
      />
      <StatCard label="In review" value={46} icon={<Images size={20} aria-hidden="true" />} tone="info" />
      <StatCard label="Expiring" value={7} hint="Within 30 days" icon={<Images size={20} aria-hidden="true" />} tone="warning" />
    </div>
  ),
};

export const EmptyStateStory: Story = {
  name: 'EmptyState',
  render: () => (
    <Card padding="none">
      <EmptyState
        icon={<Images aria-hidden="true" />}
        title="No assets in this folder"
        description="Upload files here or move existing assets in from the library."
        action={<Button variant="primary">Upload files</Button>}
      />
    </Card>
  ),
};

export const Selection: Story = {
  render: function Render() {
    const [channels, setChannels] = useState<string[]>(['web']);
    const [tags, setTags] = useState<string[]>(['hero', 'spring']);
    return (
      <Card as="section" aria-label="Selection controls" style={{ maxWidth: 420, display: 'grid', gap: 20 }}>
        <CheckboxGroup
          label="Channels"
          hint="Where this asset may be used"
          value={channels}
          onChange={setChannels}
          options={[
            { value: 'web', label: 'Web', count: 1204 },
            { value: 'print', label: 'Print', count: 388 },
            { value: 'social', label: 'Social', count: 97 },
            { value: 'tv', label: 'TV', count: 0, disabled: true },
          ]}
        />
        <TagInput
          label="Keywords"
          hint="Enter or comma adds a keyword"
          placeholder="Add a keyword"
          value={tags}
          onChange={setTags}
          suggestions={['summer', 'autumn', 'launch']}
        />
      </Card>
    );
  },
};

export const Avatars: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 12, alignItems: 'center' }}>
      <Avatar name="Priya Natarajan" size="sm" />
      <Avatar name="Tomasz Kowalski" />
      <Avatar name="amara" size="lg" />
    </div>
  ),
};

export const LogoStory: Story = {
  name: 'Logo',
  render: () => (
    <div style={{ display: 'flex', gap: 32, alignItems: 'center' }}>
      <Logo size={64} wordmark={false} />
      <Logo size={32} />
      <Logo size={20} />
    </div>
  ),
};

export const CardsWithMenus: Story = {
  render: function Render() {
    const [sel, setSel] = useState<string[]>([]);
    return (
      <ThumbnailGrid
        label="Assets"
        selectedIds={sel}
        onToggleSelect={(id, on) => setSel((s) => (on ? [...s, id] : s.filter((x) => x !== id)))}
        cardActions={() => [
          { key: 'open', label: 'Open details', onSelect: () => {} },
          {
            key: 'avail',
            label: 'Make available for',
            children: [
              { key: 'a', label: 'Design tool', checked: true, onSelect: () => {} },
              { key: 'b', label: 'Web API', checked: false, onSelect: () => {} },
            ],
          },
          { key: 'delete', label: 'Delete', tone: 'danger', separatorBefore: true, onSelect: () => {} },
        ]}
        items={[
          { id: '1', title: 'Roadster GT hero', subtitle: 'JPEG · 4000×2667', badge: 'Published' },
          { id: '2', title: 'Trail X walkaround', subtitle: 'MP4 · 0:42', badge: 'In review' },
        ]}
      />
    );
  },
};

export const TableWithThumbnails: Story = {
  render: () => (
    <Table
      caption="Assets"
      rows={[
        { id: '1', name: 'Roadster GT hero', type: 'Image', size: '4.2 MB' },
        { id: '2', name: 'Trail X walkaround', type: 'Video', size: '182.4 MB' },
      ]}
      getRowId={(r) => r.id}
      selectedIds={['2']}
      onToggleSelect={() => {}}
      selectLabel={(r) => `Select ${r.name}`}
      columns={[
        { key: 'thumb', header: '', render: () => <span className="dam-table__thumb" aria-hidden="true" /> },
        { key: 'name', header: 'Name', sortable: true },
        { key: 'type', header: 'Type' },
        { key: 'size', header: 'Size' },
      ]}
      sort={{ key: 'name', direction: 'asc' }}
      onSortChange={() => {}}
    />
  ),
};
