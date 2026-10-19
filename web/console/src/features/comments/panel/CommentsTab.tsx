import { ChatCircleText, WarningCircle } from '@phosphor-icons/react';
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router-dom';
import { useMe } from '../../../api/me';
import { Button, EmptyState, LoadingRegion, Modal, SegmentedControl, Select, Skeleton, useToast } from '../../../ui';
import { useCommentsUi } from '../CommentsContext';
import {
  commentErrorMessage,
  useAddComment,
  useDeleteComment,
  useThreads,
  type CommentDto,
  type CommentThread,
  type ThreadKind,
  type ThreadState,
} from '../commentsApi';
import { annotationNumbers } from '../numbering';
import { Composer } from './Composer';
import { ThreadCard, type Viewer } from './ThreadCard';
import './comments.css';

const STATES: ThreadState[] = ['open', 'resolved', 'all'];
const KINDS: ThreadKind[] = ['all', 'annotation', 'comment'];
const FLASH_MS = 2600;

/** The thread (and the comment within it) with this id, searching replies too. */
export function findComment(threads: readonly CommentThread[], id: string): { root: CommentDto; comment: CommentDto } | null {
  for (const t of threads) {
    if (t.root.id === id) return { root: t.root, comment: t.root };
    const reply = t.replies.find((r) => r.id === id);
    if (reply) return { root: t.root, comment: reply };
  }
  return null;
}

function Skeletons() {
  return (
    <div className="dam-threads__loading">
      {[0, 1, 2].map((i) => (
        <div key={i} className="dam-thread dam-thread--skeleton">
          <div className="dam-comment__head">
            <Skeleton width={24} height={24} />
            <Skeleton width="40%" height={14} />
          </div>
          <Skeleton height={14} />
          <Skeleton width="70%" height={14} />
        </div>
      ))}
    </div>
  );
}

interface Props {
  assetId: string;
  /** Set by the page when the preview asks for the composer (a region was drawn or a time captured). */
  focusRequest?: number;
  onFocusHandled?: () => void;
}

/** The Comments tab: filters, the threads (oldest first, reading order) and the composer at the foot of the list. State,
 * type and scope live in the URL (`state`, `kind`, `scope=all`); `?comment=<id>` opens a thread (what notifications link to). */
export function CommentsTab({ assetId, focusRequest = 0, onFocusHandled }: Props) {
  const { t } = useTranslation();
  const { push } = useToast();
  const ui = useCommentsUi();
  const me = useMe();
  const [params, setParams] = useSearchParams();
  const composerRef = useRef<HTMLTextAreaElement>(null);
  const composerBox = useRef<HTMLDivElement>(null);
  const [toDelete, setToDelete] = useState<CommentDto | null>(null);
  const retried = useRef(false);

  const asKey = <T extends string>(raw: string | null, allowed: readonly T[], fallback: T): T =>
    allowed.includes(raw as T) ? (raw as T) : fallback;
  const state = asKey(params.get('state'), STATES, 'all');
  const kind = asKey(params.get('kind'), KINDS, 'all');
  const versionId = ui.versionId ?? undefined;
  const allVersions = !versionId || params.get('scope') === 'all';

  const setParam = (key: string, value: string, fallback: string) =>
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        if (value === fallback) next.delete(key);
        else next.set(key, value);
        return next;
      },
      { replace: true },
    );

  const list = useThreads(assetId, { versionId: allVersions ? undefined : versionId, state, kind });
  // The numbers on the preview's marks come from the current version's complete list, whatever the filters here are.
  const forNumbers = useThreads(assetId, { versionId });
  const numbers = useMemo(() => annotationNumbers(forNumbers.data?.threads ?? []), [forNumbers.data]);

  const permissions = me.data?.permissions ?? [];
  const viewer: Viewer = {
    userId: me.data?.userId ?? null,
    name: me.data?.displayName ?? '',
    canWrite: permissions.includes('comments.write'),
    canModerate: permissions.includes('assets.update'),
  };

  const add = useAddComment(assetId);
  const del = useDeleteComment(assetId);

  // ---- the preview asked for the composer ----
  const meLoaded = !me.isPending;
  useEffect(() => {
    if (!focusRequest || !meLoaded) return;
    if (composerRef.current) {
      composerBox.current?.scrollIntoView?.({ block: 'center' });
      composerRef.current.focus({ preventScroll: true });
    }
    onFocusHandled?.();
  }, [focusRequest, meLoaded, onFocusHandled]);

  // ---- deep link: ?comment=<id> ----
  // The param stays in the URL while the thread is found, scrolled to and lit (a couple of seconds), then goes.
  const target = params.get('comment');
  const everything = useThreads(assetId, {}, !!target);
  const found = target && everything.data ? findComment(everything.data.threads, target) : null;
  const flashId = found?.comment.id ?? null;
  const resolvedFor = useRef<string | null>(null);
  const scrolledFor = useRef<string | null>(null);
  const timers = useRef<{ giveUp?: ReturnType<typeof setTimeout>; flash?: ReturnType<typeof setTimeout> }>({});
  const clearTarget = useCallback(
    () =>
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          next.delete('comment');
          return next;
        },
        { replace: true },
      ),
    [setParams],
  );

  // 1. Work out where the comment is and make the list able to show it (other version, resolved, not an annotation...).
  useEffect(() => {
    if (!target) {
      resolvedFor.current = null;
      retried.current = false;
      return;
    }
    if (!everything.data || everything.isFetching || resolvedFor.current === target) return;
    if (!found && !retried.current && Date.now() - everything.dataUpdatedAt > 2000) {
      retried.current = true; // what we hold may predate the comment: ask once more before saying it is gone
      void everything.refetch();
      return;
    }
    resolvedFor.current = target;
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        next.set('tab', 'comments');
        if (!found) next.delete('comment');
        else {
          if (versionId && found.root.versionId !== versionId) next.set('scope', 'all');
          if (state !== 'all' && (state === 'open') === found.root.resolved) next.delete('state');
          if (kind !== 'all' && (kind === 'annotation') !== !!found.root.anchor) next.delete('kind');
        }
        return next;
      },
      { replace: true },
    );
    if (!found) push({ title: t('comments.deepLinkMissing'), tone: 'info' });
  }, [target, found, everything, setParams, versionId, state, kind, push, t]);

  // 2. Once its card is on screen: select it, scroll to it, then let the light fade and drop the param.
  useLayoutEffect(() => {
    if (!target) {
      scrolledFor.current = null;
      return;
    }
    if (!found || scrolledFor.current === target) return;
    const el = document.getElementById(`comment-${found.comment.id}`);
    if (!el) return; // filters not applied yet; this runs again with the next render
    scrolledFor.current = target;
    ui.selectThread(found.root.id);
    const calm = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    el.scrollIntoView?.({ block: 'center', behavior: calm ? 'auto' : 'smooth' });
    clearTimeout(timers.current.giveUp);
    timers.current.flash = setTimeout(clearTarget, FLASH_MS);
  });
  useEffect(() => {
    if (!target) return;
    const mine = timers.current;
    mine.giveUp = setTimeout(clearTarget, 5000); // never keep waiting for a card that is not coming
    return () => {
      clearTimeout(mine.giveUp);
      clearTimeout(mine.flash);
    };
  }, [target, clearTarget]);

  const counts = list.data?.counts;
  const total = counts ? counts.open + counts.resolved : 0;
  const threads = list.data?.threads ?? [];
  const filtered = state !== 'all' || kind !== 'all';

  const confirmDelete = async () => {
    const c = toDelete;
    if (!c) return;
    try {
      await del.mutateAsync(c.id);
    } catch (e) {
      push({ title: t('comments.actionFailed'), message: commentErrorMessage(e), tone: 'error' });
    }
    setToDelete(null);
  };

  const postTopLevel = async (body: string) => {
    await add.mutateAsync({ body, versionId: ui.versionId, anchor: ui.draftAnchor });
    ui.setDraftAnchor(null);
    // A new thread is open: do not leave the person staring at a list it cannot appear in.
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        if (state === 'resolved') next.delete('state');
        if (kind !== 'all') next.delete('kind');
        return next;
      },
      { replace: true },
    );
  };

  return (
    <div className="dam-comments">
      <div className="dam-comments__filters">
        <SegmentedControl
          label={t('comments.filter.state')}
          value={state}
          onChange={(v) => setParam('state', v, 'all')}
          options={[
            { value: 'open', label: counts ? t('comments.filter.openN', { count: counts.open }) : t('comments.filter.open') },
            {
              value: 'resolved',
              label: counts ? t('comments.filter.resolvedN', { count: counts.resolved }) : t('comments.filter.resolved'),
            },
            { value: 'all', label: t('comments.filter.all') },
          ]}
        />
        <div className="dam-comments__selects">
          <Select
            label={t('comments.filter.kind')}
            value={kind}
            onChange={(e) => setParam('kind', e.target.value, 'all')}
            options={[
              { value: 'all', label: t('comments.filter.kindAll') },
              {
                value: 'annotation',
                label: counts
                  ? t('comments.filter.annotationsN', { count: counts.annotations })
                  : t('comments.filter.annotations'),
              },
              {
                value: 'comment',
                label: counts
                  ? t('comments.filter.commentsN', { count: Math.max(0, total - counts.annotations) })
                  : t('comments.filter.comments'),
              },
            ]}
          />
          {versionId && (
            <Select
              label={t('comments.filter.scope')}
              value={allVersions ? 'all' : 'version'}
              onChange={(e) => setParam('scope', e.target.value === 'all' ? 'all' : 'version', 'version')}
              options={[
                { value: 'version', label: t('comments.filter.thisVersion') },
                { value: 'all', label: t('comments.filter.allVersions') },
              ]}
            />
          )}
        </div>
      </div>

      {list.isPending ? (
        <LoadingRegion label={t('comments.loading')}>
          <Skeletons />
        </LoadingRegion>
      ) : list.error ? (
        <div role="alert" className="dam-comments__error">
          <WarningCircle size={18} weight="bold" aria-hidden="true" />
          <span>{t('comments.loadFailed')}</span>
          <Button size="sm" onClick={() => void list.refetch()}>
            {t('comments.retry')}
          </Button>
        </div>
      ) : threads.length === 0 ? (
        total === 0 ? (
          <EmptyState
            icon={<ChatCircleText />}
            titleAs="p"
            title={t('comments.empty.title')}
            description={viewer.canWrite ? t('comments.empty.hint', { context: ui.assetType }) : t('comments.empty.hintReadOnly')}
          />
        ) : (
          <EmptyState
            icon={<ChatCircleText />}
            titleAs="p"
            title={t('comments.empty.filtered')}
            action={
              filtered ? (
                <Button
                  size="sm"
                  onClick={() =>
                    setParams(
                      (prev) => {
                        const next = new URLSearchParams(prev);
                        next.delete('state');
                        next.delete('kind');
                        return next;
                      },
                      { replace: true },
                    )
                  }
                >
                  {t('comments.empty.clearFilters')}
                </Button>
              ) : undefined
            }
          />
        )
      ) : (
        <ol className="dam-threads" aria-label={t('comments.listLabel')}>
          {threads.map((th) => (
            <li key={th.root.id}>
              <ThreadCard
                assetId={assetId}
                thread={th}
                number={numbers.get(th.root.id)}
                currentVersionId={ui.versionId}
                showVersions={allVersions && !!versionId}
                viewer={viewer}
                flashId={flashId}
                onDelete={setToDelete}
              />
            </li>
          ))}
        </ol>
      )}

      <div className="dam-comments__compose" ref={composerBox}>
        {me.isPending ? (
          <Skeleton height={84} />
        ) : viewer.canWrite ? (
          <Composer
            assetId={assetId}
            label={t('comments.composer.label')}
            placeholder={t(ui.draftAnchor ? 'comments.composer.placeholderAnnotation' : 'comments.composer.placeholder')}
            submitLabel={t('comments.composer.post')}
            anchor={ui.draftAnchor}
            onClearAnchor={() => ui.setDraftAnchor(null)}
            inputRef={composerRef}
            onSubmit={postTopLevel}
          />
        ) : me.data ? (
          <p className="dam-comments__note">{t('comments.readOnly')}</p>
        ) : null}
      </div>

      <Modal
        open={toDelete !== null}
        title={t('comments.deleteDialog.title')}
        closeLabel={t('comments.deleteDialog.close')}
        onClose={() => setToDelete(null)}
      >
        <div className="dam-stack dam-comments__confirm">
          <p>
            {toDelete && list.data?.threads.some((th) => th.root.id === toDelete.id && th.replies.length > 0)
              ? t('comments.deleteDialog.bodyWithReplies')
              : t('comments.deleteDialog.body')}
          </p>
          <div className="dam-comments__confirm-actions">
            <Button variant="ghost" onClick={() => setToDelete(null)}>
              {t('comments.deleteDialog.cancel')}
            </Button>
            <Button variant="danger" loading={del.isPending} onClick={() => void confirmDelete()}>
              {t('comments.deleteDialog.confirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
