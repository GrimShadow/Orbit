import { BoundingBox, CheckCircle, Clock, MapPin } from '@phosphor-icons/react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { relativeTime } from '../../../lib/relativeTime';
import { Avatar, Badge, useToast } from '../../../ui';
import { useCommentsUi } from '../CommentsContext';
import { commentErrorMessage, useAddComment, useUpdateComment, type CommentDto, type CommentThread } from '../commentsApi';
import { anchorLabel } from './anchorLabel';
import { Composer } from './Composer';
import { splitBody } from './mentionText';

/** Who the signed-in person is and what they may do here (from /me); `userId` null while that is still loading. */
export interface Viewer {
  userId: string | null;
  name: string;
  /** `comments.write`: reply, resolve, edit. */
  canWrite: boolean;
  /** `assets.update`: may delete other people's comments. */
  canModerate: boolean;
}

/** A body as text and highlighted @Name chips - always text nodes, never HTML. */
export function CommentBody({ comment }: { comment: Pick<CommentDto, 'body' | 'mentions'> }) {
  return (
    <p className="dam-comment__body">
      {splitBody(comment.body, comment.mentions).map((s, i) =>
        s.kind === 'text' ? (
          s.text
        ) : (
          <span key={i} className="dam-mention">
            @{s.name}
          </span>
        ),
      )}
    </p>
  );
}

const ANCHOR_ICONS = { timecode: Clock, point: MapPin, region: BoundingBox } as const;

interface CommentViewProps {
  comment: CommentDto;
  isRoot: boolean;
  viewer: Viewer;
  showVersion: boolean;
  /** Annotation number of a first comment (matches the mark on the preview). */
  number?: number;
  currentVersionId: string | null;
  replying?: boolean;
  onReply?: () => void;
  onToggleResolved?: () => void;
  onEdit: () => void;
  onDelete: () => void;
}

function CommentView({
  comment: c,
  isRoot,
  viewer,
  showVersion,
  number,
  currentVersionId,
  replying,
  onReply,
  onToggleResolved,
  onEdit,
  onDelete,
}: CommentViewProps) {
  const { t, i18n } = useTranslation();
  const ui = useCommentsUi();
  const own = viewer.userId !== null && c.authorId === viewer.userId;
  const exact = (iso: string) => new Date(iso).toLocaleString(i18n.language);

  if (c.deleted)
    return (
      <div className="dam-comment dam-comment--deleted" id={`comment-${c.id}`}>
        <p className="dam-comment__body">{t('comments.deleted')}</p>
      </div>
    );

  const anchor = isRoot && c.anchor ? c.anchor : null;
  const AnchorIcon = anchor ? (ANCHOR_ICONS[anchor.type as keyof typeof ANCHOR_ICONS] ?? BoundingBox) : null;
  // A mark or timecode belongs to the version it was made on: only that version's file can be seeked or paged.
  const onStage = c.versionId === currentVersionId;
  const openAnchor = () => {
    if (!anchor) return;
    ui.selectThread(c.id);
    if (!onStage) return;
    if (anchor.type === 'timecode' && anchor.startMs != null) ui.seekTo(anchor.startMs);
    else if (anchor.page != null) ui.showPage(anchor.page);
  };
  const name = c.authorName;

  return (
    <div className="dam-comment" id={`comment-${c.id}`}>
      <div className="dam-comment__head">
        <Avatar name={name} size="sm" decorative />
        <span className="dam-comment__author">{name}</span>
        <time className="dam-comment__time" dateTime={c.createdAt} title={exact(c.createdAt)}>
          {relativeTime(c.createdAt, i18n.language)}
        </time>
        {c.edited && (
          <span className="dam-comment__edited" title={c.editedAt ? exact(c.editedAt) : undefined}>
            {t('comments.edited')}
          </span>
        )}
        {showVersion && c.versionNo != null && (
          <Badge size="sm" tone={onStage ? 'brand' : 'neutral'}>
            {t('comments.versionBadge', { n: c.versionNo })}
          </Badge>
        )}
      </div>
      {anchor && AnchorIcon && (
        <button
          type="button"
          className="dam-comment__anchor"
          aria-label={
            number != null
              ? t('comments.anchor.openNumbered', { n: number, label: anchorLabel(anchor, t) })
              : t('comments.anchor.open', { label: anchorLabel(anchor, t) })
          }
          onClick={openAnchor}
        >
          {number != null && (
            <span className="dam-annot-num" aria-hidden="true">
              {number}
            </span>
          )}
          <AnchorIcon size={14} weight="bold" aria-hidden="true" />
          <span aria-hidden="true">{anchorLabel(anchor, t)}</span>
        </button>
      )}
      <CommentBody comment={c} />
      {isRoot && c.resolved && (
        <p className="dam-comment__resolved">
          <CheckCircle size={14} weight="fill" aria-hidden="true" />
          {c.resolvedByName ? t('comments.resolvedBy', { name: c.resolvedByName }) : t('comments.resolved')}
          {c.resolvedAt && (
            <>
              {' '}
              <time dateTime={c.resolvedAt} title={exact(c.resolvedAt)}>
                {relativeTime(c.resolvedAt, i18n.language)}
              </time>
            </>
          )}
        </p>
      )}
      <div className="dam-comment__actions">
        {isRoot && viewer.canWrite && !replying && (
          <button
            type="button"
            id={`thread-${c.id}-reply`}
            className="dam-comment__action"
            aria-label={t('comments.replyTo', { name })}
            onClick={onReply}
          >
            {t('comments.reply')}
          </button>
        )}
        {isRoot && viewer.canWrite && (
          <button
            type="button"
            className="dam-comment__action"
            aria-label={t(c.resolved ? 'comments.reopenNamed' : 'comments.resolveNamed', { name })}
            onClick={onToggleResolved}
          >
            {t(c.resolved ? 'comments.reopen' : 'comments.resolve')}
          </button>
        )}
        {own && viewer.canWrite && (
          <button
            type="button"
            id={`comment-${c.id}-edit`}
            className="dam-comment__action"
            aria-label={t('comments.editNamed', { name })}
            onClick={onEdit}
          >
            {t('comments.edit')}
          </button>
        )}
        {(own || viewer.canModerate) && (
          <button
            type="button"
            className="dam-comment__action dam-comment__action--danger"
            aria-label={t('comments.deleteNamed', { name })}
            onClick={onDelete}
          >
            {t('comments.delete')}
          </button>
        )}
      </div>
    </div>
  );
}

const COLLAPSE_OVER = 4;
const SHOWN_WHEN_COLLAPSED = 3;

interface ThreadCardProps {
  assetId: string;
  thread: CommentThread;
  number?: number;
  currentVersionId: string | null;
  showVersions: boolean;
  viewer: Viewer;
  /** A comment or reply to light up briefly (a deep link just opened it). */
  flashId: string | null;
  onDelete: (c: CommentDto) => void;
}

/** One conversation: the first comment, its replies, and the inline reply / edit boxes. Selecting or hovering it lights
 * up the matching mark on the preview (and the other way round) through the shared comments context. */
export function ThreadCard({
  assetId,
  thread,
  number,
  currentVersionId,
  showVersions,
  viewer,
  flashId,
  onDelete,
}: ThreadCardProps) {
  const { t } = useTranslation();
  const { push } = useToast();
  const ui = useCommentsUi();
  const add = useAddComment(assetId);
  const update = useUpdateComment(assetId);
  const { root, replies } = thread;
  const [replying, setReplying] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
  const [expanded, setExpanded] = useState(false);
  const ref = useRef<HTMLElement>(null);
  const selected = ui.selectedThreadId === root.id;
  const hovered = ui.hoveredThreadId === root.id;
  const hiddenCount = !expanded && replies.length > COLLAPSE_OVER ? replies.length - SHOWN_WHEN_COLLAPSED : 0;
  const flashHidden = flashId !== null && replies.slice(0, hiddenCount).some((r) => r.id === flashId);
  const visibleReplies = hiddenCount > 0 && !flashHidden ? replies.slice(hiddenCount) : replies;

  // Chosen on the preview: bring the card into view.
  useEffect(() => {
    if (!selected) return;
    const calm = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    ref.current?.scrollIntoView?.({ block: 'nearest', behavior: calm ? 'auto' : 'smooth' });
  }, [selected]);

  // A card that goes away while the pointer is on it must not leave its mark lit.
  const hoverThread = ui.hoverThread;
  const wasHovered = useRef(false);
  useEffect(() => {
    wasHovered.current = hovered;
  }, [hovered]);
  useEffect(
    () => () => {
      if (wasHovered.current) hoverThread(null);
    },
    [hoverThread],
  );

  const focusLater = (id: string) => setTimeout(() => document.getElementById(id)?.focus(), 0);
  const fail = (e: unknown) => push({ title: t('comments.actionFailed'), message: commentErrorMessage(e), tone: 'error' });
  const toggleResolved = () =>
    update.mutate(
      { id: root.id, resolved: !root.resolved, actor: viewer.userId ? { id: viewer.userId, name: viewer.name } : undefined },
      { onError: fail },
    );

  const view = (c: CommentDto, isRoot: boolean) =>
    editing === c.id ? (
      <div className="dam-comment dam-comment--editing" id={`comment-${c.id}`}>
        <Composer
          assetId={assetId}
          compact
          focusOnMount
          requireChange
          label={t('comments.editLabel')}
          submitLabel={t('comments.save')}
          initialBody={c.body}
          onSubmit={async (body) => {
            await update.mutateAsync({ id: c.id, body });
            setEditing(null);
            focusLater(`comment-${c.id}-edit`);
          }}
          onCancel={() => {
            setEditing(null);
            focusLater(`comment-${c.id}-edit`);
          }}
        />
      </div>
    ) : (
      <CommentView
        comment={c}
        isRoot={isRoot}
        viewer={viewer}
        showVersion={showVersions}
        number={isRoot ? number : undefined}
        currentVersionId={currentVersionId}
        replying={replying}
        onReply={() => setReplying(true)}
        onToggleResolved={toggleResolved}
        onEdit={() => setEditing(c.id)}
        onDelete={() => onDelete(c)}
      />
    );

  return (
    // Selecting a thread by clicking anywhere on it is a pointer convenience; the keyboard route is its anchor button.
    // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions, jsx-a11y/click-events-have-key-events
    <article
      ref={ref}
      className="dam-thread"
      data-selected={selected || undefined}
      data-hovered={hovered || undefined}
      data-resolved={root.resolved || undefined}
      data-flash={flashId === root.id || undefined}
      aria-label={t('comments.threadBy', { name: root.deleted ? t('comments.deletedShort') : root.authorName })}
      onClick={() => ui.selectThread(root.id)}
      onMouseEnter={() => ui.hoverThread(root.id)}
      onMouseLeave={() => ui.hoverThread(null)}
    >
      {view(root, true)}
      {replies.length > 0 && (
        <div className="dam-thread__replies">
          {hiddenCount > 0 && !flashHidden && (
            <button type="button" className="dam-thread__more" aria-expanded={false} onClick={() => setExpanded(true)}>
              {t('comments.moreReplies', { count: hiddenCount })}
            </button>
          )}
          <ul className="dam-thread__list" aria-label={t('comments.repliesList')}>
            {visibleReplies.map((r) => (
              <li key={r.id} className="dam-thread__reply" data-flash={flashId === r.id || undefined}>
                {view(r, false)}
              </li>
            ))}
          </ul>
        </div>
      )}
      {replying && (
        <div className="dam-thread__compose">
          <Composer
            assetId={assetId}
            compact
            focusOnMount
            label={t('comments.replyLabel', { name: root.authorName })}
            placeholder={t('comments.replyPlaceholder')}
            submitLabel={t('comments.reply')}
            onSubmit={async (body) => {
              await add.mutateAsync({ body, parentId: root.id });
              setReplying(false);
              focusLater(`thread-${root.id}-reply`);
            }}
            onCancel={() => {
              setReplying(false);
              focusLater(`thread-${root.id}-reply`);
            }}
          />
        </div>
      )}
    </article>
  );
}
