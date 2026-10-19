import { ApiError, unwrap } from '@dam/sdk-js';
import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState, type FormEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { useApiClient } from '../../api/apiClient';
import {
  Button,
  Eye,
  FolderPlus,
  Input,
  Modal,
  PencilSimple,
  Select,
  ShareNetwork,
  Trash,
  UploadSimple,
  useToast,
  type MenuItem,
} from '../../ui';
import { ActionIcon } from '../asset/actionIcon';
import {
  assetOptions,
  schemaOptions,
  termsOptions,
  vocabulariesOptions,
  type FlatTerm,
  type SchemaDto,
  type VocabularyDto,
} from '../asset/assetApi';
import { availableActions } from '../asset/lifecycle';
import { useBulkTransition } from './bulkEditApi';
import { runBulk } from './bulkOrchestrator';
import { folderOptions, useFolderMutations, useFolders, type FolderDto } from './folders';

/** The one dimension the policy engine calls "channel": a schema field tagged with it (and bound to a vocabulary) is
 * where an asset says which systems it is made available to - the tenant decides the field and the systems. */
const CHANNEL_DIMENSION = 'channel';

export interface AssetLike {
  id: string;
  title: string;
  status: string;
  type: string;
  /** Present on list rows; search/collection hits carry none, so it is read from the asset cache instead. */
  metadata?: Record<string, unknown>;
}

type Dialog =
  | { kind: 'renameAsset'; id: string; title: string }
  | { kind: 'deleteAsset'; id: string; title: string }
  | { kind: 'newFolder'; parentId: string | null }
  | { kind: 'renameFolder'; id: string; name: string }
  | { kind: 'deleteFolder'; id: string; name: string }
  | { kind: 'folderChannel'; id: string; name: string; code: string; label: string };

const asCodes = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : []);
/** The server's own explanation when there is one (`detail`), since an ApiError's message is only its short title. */
const messageOf = (e: unknown) => (e instanceof ApiError ? e.detail || e.title : e instanceof Error ? e.message : String(e));

interface Options {
  /** Called to open a folder in the library (the folder menu's "Open"). */
  onOpenFolder?: (id: string) => void;
  /** Called after a folder is deleted, so a screen showing it can move elsewhere. */
  onFolderDeleted?: (id: string) => void;
}

/** Everything a right-click (or "...") menu on an asset or a folder can do, plus the dialogs those actions need.
 * Shared by the library, search and collection screens so a card behaves the same wherever it appears. Render
 * `dialogs` once on the page. */
export function useItemActions({ onOpenFolder, onFolderDeleted }: Options = {}) {
  const { t } = useTranslation();
  const api = useApiClient();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { push } = useToast();
  const transition = useBulkTransition();
  const folderMutations = useFolderMutations();
  const foldersQuery = useFolders();
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [busy, setBusy] = useState(false);
  const [, setTick] = useState(0);
  const loadedTypes = useRef(new Set<string>()); // asset types whose channel setup has been looked up (found or not)
  const bump = () => setTick((n) => n + 1);

  const refreshLists = () =>
    queryClient.invalidateQueries({ predicate: (q) => ['assets', 'search', 'collections'].includes(String(q.queryKey[0])) });
  const ok = (title: string) => push({ title, tone: 'success' });
  const fail = (e: unknown) => push({ title: messageOf(e), tone: 'error' });

  // ---- the channel ("make available for") field and its systems, read from what the schema + vocabularies say

  const channelField = (schema: SchemaDto | undefined) =>
    schema?.fields.find((f) => f.accessDimension === CHANNEL_DIMENSION && f.vocabulary) ?? null;

  const channelInfo = (type: string): { field: string; terms: FlatTerm[] } | null => {
    const field = channelField(queryClient.getQueryData<SchemaDto>(schemaOptions(api, type).queryKey));
    if (!field) return null;
    const vocab = queryClient
      .getQueryData<VocabularyDto[]>(vocabulariesOptions(api).queryKey)
      ?.find((v) => v.key === field.vocabulary);
    const terms = vocab ? queryClient.getQueryData<FlatTerm[]>(termsOptions(api, vocab.id).queryKey) : undefined;
    return terms ? { field: field.name, terms } : null;
  };

  /** Loads what a menu's items depend on (the asset's current channels, the type's schema, the systems). Items render
   * immediately from whatever is cached and fill in when this resolves. */
  const prefetch = (type: string, assetId?: string) => {
    void (async () => {
      try {
        const [schema, vocabs] = await Promise.all([
          queryClient.fetchQuery({ ...schemaOptions(api, type), staleTime: 60_000 }),
          queryClient.fetchQuery({ ...vocabulariesOptions(api), staleTime: 60_000 }),
          assetId ? queryClient.fetchQuery({ ...assetOptions(api, assetId), staleTime: 15_000 }) : undefined,
        ]);
        const vocab = vocabs.find((v) => v.key === channelField(schema)?.vocabulary);
        if (vocab) await queryClient.fetchQuery({ ...termsOptions(api, vocab.id), staleTime: 60_000 });
      } catch {
        /* the submenu shows nothing configured; the action itself reports any real error */
      } finally {
        loadedTypes.current.add(type);
        bump();
      }
    })();
  };

  const patchAsset = async (id: string, body: { title?: string; metadata?: Record<string, unknown> }) => {
    const updated = unwrap(
      await api.PATCH('/api/v1/assets/{id}', {
        params: { path: { id } },
        body: { title: body.title ?? null, tags: null, metadata: (body.metadata ?? null) as never },
      }),
    );
    queryClient.setQueryData(assetOptions(api, id).queryKey, updated);
    return updated;
  };

  /** What a systems submenu says when it has nothing to list: still loading, or this tenant has no such field/systems. */
  const emptyChannelsLabel = (type: string, found: boolean) =>
    found ? t('item.noChannels') : loadedTypes.current.has(type) ? t('item.noChannelField') : t('item.loadingChannels');

  const channelsMenu = (asset: AssetLike): MenuItem => {
    const info = channelInfo(asset.type);
    const meta =
      asset.metadata ??
      queryClient.getQueryData<{ metadata: Record<string, unknown> }>(assetOptions(api, asset.id).queryKey)?.metadata;
    const current = new Set(info ? asCodes(meta?.[info.field]) : []);
    return {
      key: 'channels',
      label: t('item.makeAvailable'),
      icon: <ShareNetwork size={16} weight="bold" />,
      children: (info?.terms ?? []).map((term) => ({
        key: term.code,
        label: term.label,
        checked: meta ? current.has(term.code) : undefined,
        onSelect: () => void toggleChannel(asset, term),
      })),
      emptyLabel: emptyChannelsLabel(asset.type, info !== null),
    };
  };

  const toggleChannel = async (asset: AssetLike, term: FlatTerm) => {
    try {
      const info = channelInfo(asset.type);
      if (!info) return;
      // Read the asset fresh: metadata is saved as a whole document, so a stale copy would overwrite someone's edit.
      const fresh = await queryClient.fetchQuery({ ...assetOptions(api, asset.id), staleTime: 0 });
      const metadata = fresh.metadata as Record<string, unknown>;
      const codes = new Set(asCodes(metadata[info.field]));
      const had = codes.delete(term.code);
      if (!had) codes.add(term.code);
      await patchAsset(asset.id, { metadata: { ...metadata, [info.field]: [...codes] } });
      void refreshLists();
      ok(t(had ? 'item.noLongerAvailable' : 'item.nowAvailable', { title: asset.title, system: term.label }));
    } catch (e) {
      fail(e);
    }
  };

  // ---- menus

  const assetMenu = (asset: AssetLike): MenuItem[] => {
    const open: MenuItem = {
      key: 'open',
      label: t('library.card.open'),
      icon: <Eye size={16} weight="bold" />,
      onSelect: () => navigate(`/library/assets/${asset.id}`),
    };
    if (asset.status === 'deleted') return [open];
    const lifecycle = availableActions(asset.status).map<MenuItem>((action) => ({
      key: action,
      label: t(`asset.action.${action}`),
      icon: <ActionIcon action={action} size={16} weight="bold" />,
      tone: action === 'reject' ? 'danger' : undefined,
      onSelect: () =>
        transition.mutate(
          { ids: [asset.id], action },
          {
            onSuccess: (result) =>
              push(
                result.failed.length === 0
                  ? { title: t('library.card.actionDone', { action: t(`asset.action.${action}`) }), tone: 'success' }
                  : {
                      title: t('library.card.actionFailed', { action: t(`asset.action.${action}`) }),
                      message: result.failed[0]?.error,
                      tone: 'error',
                    },
              ),
          },
        ),
    }));
    return [
      open,
      {
        key: 'rename',
        label: t('item.rename'),
        icon: <PencilSimple size={16} weight="bold" />,
        onSelect: () => setDialog({ kind: 'renameAsset', id: asset.id, title: asset.title }),
      },
      channelsMenu(asset),
      ...lifecycle.map((item, i) => (i === 0 ? { ...item, separatorBefore: true } : item)),
      {
        key: 'delete',
        label: t('item.delete'),
        icon: <Trash size={16} weight="bold" />,
        tone: 'danger',
        separatorBefore: true,
        onSelect: () => setDialog({ kind: 'deleteAsset', id: asset.id, title: asset.title }),
      },
    ];
  };

  const folderMenu = (folderId: string): MenuItem[] => {
    const folder = foldersQuery.data?.find((f) => f.id === folderId);
    if (!folder) return [];
    const info = channelInfo('image'); // the systems are tenant data shared by every type's channel field
    return [
      ...(onOpenFolder
        ? [
            {
              key: 'open',
              label: t('item.openFolder'),
              icon: <Eye size={16} weight="bold" />,
              onSelect: () => onOpenFolder(folder.id),
            },
          ]
        : []),
      {
        key: 'uploadHere',
        label: t('item.uploadHere'),
        icon: <UploadSimple size={16} weight="bold" />,
        onSelect: () => navigate(`/upload?folder=${folder.id}`),
      },
      {
        key: 'newSub',
        label: t('item.newSubfolder'),
        icon: <FolderPlus size={16} weight="bold" />,
        onSelect: () => setDialog({ kind: 'newFolder', parentId: folder.id }),
      },
      {
        key: 'rename',
        label: t('item.rename'),
        icon: <PencilSimple size={16} weight="bold" />,
        onSelect: () => setDialog({ kind: 'renameFolder', id: folder.id, name: folder.name }),
      },
      {
        key: 'channels',
        label: t('item.makeAvailable'),
        icon: <ShareNetwork size={16} weight="bold" />,
        children: (info?.terms ?? []).map((term) => ({
          key: term.code,
          label: term.label,
          onSelect: () =>
            setDialog({ kind: 'folderChannel', id: folder.id, name: folder.name, code: term.code, label: term.label }),
        })),
        emptyLabel: emptyChannelsLabel('image', info !== null),
      },
      {
        key: 'delete',
        label: t('item.delete'),
        icon: <Trash size={16} weight="bold" />,
        tone: 'danger',
        separatorBefore: true,
        onSelect: () => setDialog({ kind: 'deleteFolder', id: folder.id, name: folder.name }),
      },
    ];
  };

  // ---- what the dialogs do

  const attempt = async (work: () => Promise<string | void>) => {
    setBusy(true);
    try {
      const message = await work();
      if (message) ok(message);
      setDialog(null);
    } catch (e) {
      fail(e);
    } finally {
      setBusy(false);
    }
  };

  const addChannelToFolder = (folderId: string, folderName: string, term: { code: string; label: string }) =>
    attempt(async () => {
      const assets: { id: string; type: string; metadata: Record<string, unknown> }[] = [];
      let cursor: string | undefined;
      do {
        const page = unwrap(
          await api.GET('/api/v1/assets', { params: { query: { folderId, includeSubfolders: true, limit: 200, cursor } } }),
        );
        assets.push(...page.items.map((a) => ({ id: a.id, type: a.type, metadata: a.metadata as Record<string, unknown> })));
        cursor = page.nextCursor ?? undefined;
      } while (cursor);
      if (assets.length === 0) return t('item.folderEmpty', { name: folderName });

      const result = await runBulk(
        assets.map((a) => a.id),
        async (id) => {
          const a = assets.find((x) => x.id === id)!;
          const schema = await queryClient.fetchQuery({ ...schemaOptions(api, a.type), staleTime: 60_000 });
          const field = channelField(schema);
          if (!field) return; // this type has no channel field - nothing to add
          const codes = new Set(asCodes(a.metadata[field.name]));
          if (codes.has(term.code)) return;
          codes.add(term.code);
          await patchAsset(id, { metadata: { ...a.metadata, [field.name]: [...codes] } });
        },
      );
      void refreshLists();
      if (result.failed.length > 0)
        throw new Error(t('library.bulk.someFailed', { succeeded: result.succeeded.length, failed: result.failed.length }));
      return t('item.folderChannelDone', { count: result.succeeded.length, system: term.label });
    });

  const dialogs = (
    <ItemDialogs
      dialog={dialog}
      busy={busy}
      folders={foldersQuery.data}
      onClose={() => setDialog(null)}
      onRenameAsset={(id, title) =>
        attempt(async () => {
          await patchAsset(id, { title });
          void refreshLists();
          return t('item.renamed');
        })
      }
      onDeleteAsset={(id) =>
        attempt(async () => {
          unwrap(await api.DELETE('/api/v1/assets/{id}', { params: { path: { id } } }));
          void refreshLists();
          return t('item.deleted');
        })
      }
      onCreateFolder={(name, parentId) =>
        attempt(async () => {
          const created = await folderMutations.create.mutateAsync({ name, parentId });
          onOpenFolder?.(created.folder.id);
          return t('item.folderCreated', { name });
        })
      }
      onRenameFolder={(id, name) =>
        attempt(async () => {
          await folderMutations.rename.mutateAsync({ id, name });
          return t('item.folderRenamed');
        })
      }
      onDeleteFolder={(id) =>
        attempt(async () => {
          await folderMutations.remove.mutateAsync(id);
          onFolderDeleted?.(id);
          return t('item.folderDeleted');
        })
      }
      onFolderChannel={addChannelToFolder}
    />
  );

  return {
    assetMenu,
    folderMenu,
    /** Start loading what an asset's / the folder menu's items depend on. */
    prefetchAsset: (asset: AssetLike) => prefetch(asset.type, asset.id),
    prefetchFolder: () => prefetch('image'),
    newFolder: (parentId: string | null) => setDialog({ kind: 'newFolder', parentId }),
    dialogs,
  };
}

interface ItemDialogsProps {
  dialog: Dialog | null;
  busy: boolean;
  folders: readonly FolderDto[] | undefined;
  onClose: () => void;
  onRenameAsset: (id: string, title: string) => void;
  onDeleteAsset: (id: string) => void;
  onCreateFolder: (name: string, parentId: string | null) => void;
  onRenameFolder: (id: string, name: string) => void;
  onDeleteFolder: (id: string) => void;
  onFolderChannel: (id: string, name: string, term: { code: string; label: string }) => void;
}

function NameForm({
  label,
  initial,
  submit,
  busy,
  onSubmit,
  extra,
}: {
  label: string;
  initial: string;
  submit: string;
  busy: boolean;
  onSubmit: (value: string) => void;
  extra?: ReactNode;
}) {
  const [value, setValue] = useState(initial);
  const submitForm = (e: FormEvent) => {
    e.preventDefault();
    if (value.trim()) onSubmit(value.trim());
  };
  return (
    <form className="dam-stack" onSubmit={submitForm}>
      <Input label={label} value={value} maxLength={200} onChange={(e) => setValue(e.target.value)} />
      {extra}
      <Button type="submit" variant="primary" loading={busy} disabled={!value.trim()}>
        {submit}
      </Button>
    </form>
  );
}

function Confirm({
  body,
  confirm,
  busy,
  danger,
  onConfirm,
  onCancel,
}: {
  body: string;
  confirm: string;
  busy: boolean;
  danger?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation();
  return (
    <div className="dam-stack">
      <p>{body}</p>
      <div className="dam-upload__done">
        <Button variant={danger ? 'danger' : 'primary'} loading={busy} onClick={onConfirm}>
          {confirm}
        </Button>
        <Button variant="secondary" onClick={onCancel}>
          {t('item.cancel')}
        </Button>
      </div>
    </div>
  );
}

function ItemDialogs(p: ItemDialogsProps) {
  const { t } = useTranslation();
  const d = p.dialog;
  const [parent, setParent] = useState<string>('');
  const title =
    d?.kind === 'renameAsset'
      ? t('item.renameAssetTitle')
      : d?.kind === 'deleteAsset'
        ? t('item.deleteAssetTitle')
        : d?.kind === 'newFolder'
          ? t('item.newFolderTitle')
          : d?.kind === 'renameFolder'
            ? t('item.renameFolderTitle')
            : d?.kind === 'deleteFolder'
              ? t('item.deleteFolderTitle')
              : d?.kind === 'folderChannel'
                ? t('item.folderChannelTitle', { system: d.label })
                : '';

  return (
    <Modal
      open={d !== null}
      title={title}
      onClose={() => {
        setParent('');
        p.onClose();
      }}
    >
      {d?.kind === 'renameAsset' && (
        <NameForm
          label={t('item.nameLabel')}
          initial={d.title}
          submit={t('item.save')}
          busy={p.busy}
          onSubmit={(v) => p.onRenameAsset(d.id, v)}
        />
      )}
      {d?.kind === 'deleteAsset' && (
        <Confirm
          body={t('item.deleteAssetBody', { title: d.title })}
          confirm={t('item.confirmDelete')}
          danger
          busy={p.busy}
          onConfirm={() => p.onDeleteAsset(d.id)}
          onCancel={p.onClose}
        />
      )}
      {d?.kind === 'newFolder' && (
        <NameForm
          key={d.parentId ?? 'top'}
          label={t('item.nameLabel')}
          initial=""
          submit={t('item.create')}
          busy={p.busy}
          onSubmit={(v) => p.onCreateFolder(v, parent === '' ? (d.parentId ?? null) : parent === '__top' ? null : parent)}
          extra={
            <Select
              label={t('item.parentFolder')}
              value={parent === '' ? (d.parentId ?? '__top') : parent}
              onChange={(e) => setParent(e.target.value)}
              options={[{ value: '__top', label: t('item.topLevel') }, ...(p.folders ? folderOptions(p.folders) : [])]}
            />
          }
        />
      )}
      {d?.kind === 'renameFolder' && (
        <NameForm
          label={t('item.nameLabel')}
          initial={d.name}
          submit={t('item.save')}
          busy={p.busy}
          onSubmit={(v) => p.onRenameFolder(d.id, v)}
        />
      )}
      {d?.kind === 'deleteFolder' && (
        <Confirm
          body={t('item.deleteFolderBody', { name: d.name })}
          confirm={t('item.confirmDelete')}
          danger
          busy={p.busy}
          onConfirm={() => p.onDeleteFolder(d.id)}
          onCancel={p.onClose}
        />
      )}
      {d?.kind === 'folderChannel' && (
        <Confirm
          body={t('item.folderChannelBody', { name: d.name, system: d.label })}
          confirm={t('item.confirmAdd')}
          busy={p.busy}
          onConfirm={() => p.onFolderChannel(d.id, d.name, { code: d.code, label: d.label })}
          onCancel={p.onClose}
        />
      )}
    </Modal>
  );
}
