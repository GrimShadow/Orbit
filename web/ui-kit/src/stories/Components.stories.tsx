import type { Meta, StoryObj } from '@storybook/react';
import { useState } from 'react';
import {
  Button,
  Drawer,
  FacetList,
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
  type SortState,
} from '../index';

const meta: Meta = { title: 'Design system/Components' };
export default meta;
type Story = StoryObj;

export const Buttons: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
      <Button variant="primary">Primary</Button>
      <Button>Secondary</Button>
      <Button variant="ghost">Ghost</Button>
      <Button variant="danger">Danger</Button>
      <Button variant="primary" loading>
        Saving
      </Button>
      <Button disabled>Disabled</Button>
    </div>
  ),
};

export const Fields: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 16, maxWidth: 360 }}>
      <Input label="Title" hint="Shown in search results" />
      <Input label="Campaign code" error="This field is required" />
      <Select
        label="Language"
        options={[
          { value: 'en', label: 'English' },
          { value: 'hi', label: 'हिन्दी' },
        ]}
      />
    </div>
  ),
};

const rows = [
  { id: '1', name: 'Thar ROXX hero', type: 'image' },
  { id: '2', name: 'XUV700 walkaround', type: 'video' },
];
export const DataTable: Story = {
  render: function Render() {
    const [sort, setSort] = useState<SortState>({ key: 'name', direction: 'asc' });
    return (
      <Table
        caption="Assets"
        rows={rows}
        getRowId={(r) => r.id}
        sort={sort}
        onSortChange={setSort}
        columns={[
          { key: 'name', header: 'Name', sortable: true },
          { key: 'type', header: 'Type', sortable: true },
        ]}
      />
    );
  },
};
export const TableLoading: Story = {
  render: () => (
    <Table
      caption="Loading"
      rows={[]}
      getRowId={(r: { id: string }) => r.id}
      columns={[
        { key: 'a', header: 'Name' },
        { key: 'b', header: 'Type' },
      ]}
      loading
    />
  ),
};

export const Dialogs: Story = {
  render: function Render() {
    const [m, setM] = useState(false);
    const [d, setD] = useState(false);
    return (
      <div style={{ display: 'flex', gap: 12 }}>
        <Button onClick={() => setM(true)}>Open modal</Button>
        <Button onClick={() => setD(true)}>Open drawer</Button>
        <Modal open={m} onClose={() => setM(false)} title="Archive asset">
          This moves the asset to cold storage.
        </Modal>
        <Drawer open={d} onClose={() => setD(false)} title="Asset details">
          Metadata, versions and rights go here.
        </Drawer>
      </div>
    );
  },
};

function ToastDemo() {
  const { push } = useToast();
  return (
    <div style={{ display: 'flex', gap: 12 }}>
      <Button onClick={() => push({ title: 'Asset published', tone: 'success' })}>Success</Button>
      <Button onClick={() => push({ title: 'Upload failed', message: 'Network dropped at 63%.', tone: 'error' })}>Error</Button>
    </div>
  );
}
export const Toasts: Story = {
  render: () => (
    <ToastProvider>
      <ToastDemo />
    </ToastProvider>
  ),
};

export const TabsStory: Story = {
  name: 'Tabs',
  render: () => (
    <Tabs
      label="Asset sections"
      tabs={[
        { id: 'd', label: 'Details', content: 'Metadata form' },
        { id: 'v', label: 'Versions', content: 'Version history' },
        { id: 'r', label: 'Rights', content: 'Licence and channels' },
      ]}
    />
  ),
};

export const TaxonomyTree: Story = {
  render: () => (
    <Tree
      label="Model taxonomy"
      defaultExpanded={['m']}
      nodes={[
        {
          id: 'm',
          label: 'Mahindra',
          children: [
            { id: 't', label: 'Thar', children: [{ id: 'tr', label: 'Thar ROXX' }] },
            { id: 'x', label: 'XUV700' },
          ],
        },
        {
          id: 'r',
          label: 'Regions',
          children: [
            { id: 'n', label: 'North' },
            { id: 's', label: 'South' },
          ],
        },
      ]}
    />
  ),
};

const facets: Facet[] = [
  {
    id: 'region',
    label: 'Region',
    options: [
      { value: 'n', label: 'North', count: 120 },
      { value: 's', label: 'South', count: 84 },
    ],
  },
  {
    id: 'type',
    label: 'Asset type',
    options: [
      { value: 'i', label: 'Image', count: 900 },
      { value: 'v', label: 'Video', count: 210 },
    ],
  },
];
export const Facets: Story = {
  render: function Render() {
    const [sel, setSel] = useState<Record<string, string[]>>({});
    return <FacetList facets={facets} selected={sel} onChange={setSel} />;
  },
};

export const Thumbnails: Story = {
  render: function Render() {
    const [sel, setSel] = useState<string[]>([]);
    return (
      <ThumbnailGrid
        label="Assets"
        selectedIds={sel}
        onToggleSelect={(id, on) => setSel((s) => (on ? [...s, id] : s.filter((x) => x !== id)))}
        items={[
          { id: '1', title: 'Thar ROXX hero', subtitle: 'JPEG · 4000×2667', badge: 'Published' },
          { id: '2', title: 'XUV700 walkaround', subtitle: 'MP4 · 0:42', badge: 'In review' },
          { id: '3', title: 'Scorpio-N brochure', subtitle: 'PDF · 12 pages' },
        ]}
      />
    );
  },
};

export const Skeletons: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 8, maxWidth: 320 }}>
      <Skeleton height={24} width="60%" />
      <Skeleton height={14} />
      <Skeleton height={14} width="80%" />
    </div>
  ),
};
