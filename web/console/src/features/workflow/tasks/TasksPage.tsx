import { CheckCircle, ClipboardText } from '@phosphor-icons/react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';
import {
  Badge,
  Button,
  EmptyState,
  FileTypeIcon,
  ImageWithFallback,
  LoadingRegion,
  PageHeader,
  SegmentedControl,
  Skeleton,
  Tabs,
} from '../../../ui';
import { useMe } from '../../../api/me';
import { relativeTime } from '../../../lib/relativeTime';
import { hasThumbnail } from '../../../lib/thumbnails';
import { useThumbnailLinks } from '../../asset/assetApi';
import { useReviewSummary } from '../../translation/translationApi';
import { useDocumentTitle } from '../../library/useDocumentTitle';
import { DueBadge } from '../DueBadge';
import { sortPending, useNow } from '../dueText';
import { TaskDecision } from '../TaskDecision';
import { useTaskSummary, useTasks, type TaskScope, type TaskStatus, type WorkflowTask } from '../workflowApi';
import { TranslationsTab } from './TranslationsTab';
import './tasks.css';

type View = Exclude<TaskStatus, 'all'>;

function TaskRow({
  task,
  thumb,
  now,
  scope,
}: {
  task: WorkflowTask;
  thumb: string | null | undefined;
  now: number;
  scope: TaskScope;
}) {
  const { t, i18n } = useTranslation();
  const pending = task.status === 'pending';
  const outcome = task.decision === 'approve' ? 'approve' : task.decision === 'reject' ? 'reject' : 'cancelled';
  const decidedWhen = task.decidedAt ? new Date(task.decidedAt).toLocaleString(i18n.language) : undefined;

  return (
    <li className="dam-taskrow">
      <ImageWithFallback
        className="dam-taskrow__thumb"
        src={thumb}
        loading="lazy"
        fallback={
          <span className="dam-taskrow__thumb dam-taskrow__thumb--empty" aria-hidden="true">
            <FileTypeIcon type={task.assetType} size={22} />
          </span>
        }
      />
      <div className="dam-taskrow__main">
        <Link to={`/library/assets/${task.assetId}`} className="dam-taskrow__title">
          {task.assetTitle}
        </Link>
        <p className="dam-taskrow__step">{t('tasks.stepLine', { workflow: task.workflowName, step: task.stepName })}</p>
        {pending ? (
          <div className="dam-taskrow__badges">
            <DueBadge task={task} now={now} />
            {task.escalated && (
              <Badge tone="warning" size="sm" title={t('tasks.escalatedHint')}>
                {t('tasks.escalated')}
              </Badge>
            )}
            {scope === 'all' && task.assigneeRoles.length > 0 && (
              <span className="dam-taskrow__assignees">{t('tasks.assignedTo', { who: task.assigneeRoles.join(', ') })}</span>
            )}
          </div>
        ) : (
          <>
            <div className="dam-taskrow__badges">
              <Badge tone={outcome === 'approve' ? 'success' : outcome === 'reject' ? 'danger' : 'neutral'} size="sm">
                {t(`tasks.decision.${outcome}`)}
              </Badge>
              <span className="dam-taskrow__assignees">
                {task.decidedByName && t('tasks.by', { name: task.decidedByName })}
                {task.decidedByName && task.decidedAt ? ' · ' : ''}
                {task.decidedAt && (
                  <time dateTime={task.decidedAt} title={decidedWhen}>
                    {relativeTime(task.decidedAt, i18n.language)}
                  </time>
                )}
              </span>
            </div>
            {task.comment && <blockquote className="dam-taskrow__comment">{task.comment}</blockquote>}
          </>
        )}
      </div>
      {pending && scope === 'me' && (
        <div className="dam-taskrow__actions">
          <TaskDecision task={task} />
        </div>
      )}
    </li>
  );
}

function RowsSkeleton() {
  return (
    <div className="dam-tasklist dam-tasklist--skeleton">
      {[0, 1, 2].map((i) => (
        <div key={i} className="dam-taskrow" aria-hidden="true">
          <Skeleton width={56} height={56} />
          <div className="dam-taskrow__main">
            <Skeleton width="40%" height={16} />
            <Skeleton width="25%" height={12} />
            <Skeleton width={120} height={20} />
          </div>
        </div>
      ))}
    </div>
  );
}

function ApprovalTasks() {
  const { t } = useTranslation();
  const { data: me } = useMe();
  const [params, setParams] = useSearchParams();
  const manager = !!me?.permissions.includes('workflow.manage');
  const view: View = params.get('view') === 'decided' ? 'decided' : 'pending';
  const scope: TaskScope = manager && params.get('scope') === 'all' ? 'all' : 'me';
  const now = useNow();

  const set = (key: 'view' | 'scope', value: string, fallback: string) =>
    setParams(
      (p) => {
        const next = new URLSearchParams(p);
        if (value === fallback) next.delete(key);
        else next.set(key, value);
        return next;
      },
      { replace: true },
    );

  const query = useTasks({ assignee: scope, status: view });
  const summary = useTaskSummary(scope === 'me' && view === 'pending');
  const loaded = query.data?.pages.flatMap((p) => p.items) ?? [];
  const items = useMemo(
    () =>
      view === 'pending'
        ? sortPending(loaded, now)
        : [...loaded].sort((a, b) => new Date(b.decidedAt ?? 0).getTime() - new Date(a.decidedAt ?? 0).getTime()),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `loaded` is rebuilt from query.data each render
    [query.data, view, now],
  );
  const thumbs = useThumbnailLinks(items.filter((x) => hasThumbnail(x.assetType)).map((x) => x.assetId));

  const s = summary.data;
  const summaryText =
    scope === 'me' && view === 'pending' && s
      ? [
          t('tasks.summaryWaiting', { count: s.pendingForMe }),
          s.overdueForMe > 0 ? t('tasks.summaryOverdue', { count: s.overdueForMe }) : null,
        ]
          .filter(Boolean)
          .join(' · ')
      : null;

  const emptyKey = scope === 'all' ? (view === 'pending' ? 'everyonePending' : 'everyoneDecided') : view;
  const empty = { title: t(`tasks.empty.${emptyKey}Title`), body: t(`tasks.empty.${emptyKey}Body`) };

  return (
    <>
      <div className="dam-tasks__controls">
        <SegmentedControl
          label={t('tasks.viewLabel')}
          value={view}
          onChange={(v) => set('view', v, 'pending')}
          options={[
            { value: 'pending', label: t('tasks.pending') },
            { value: 'decided', label: t('tasks.decided') },
          ]}
        />
        {manager && (
          <SegmentedControl
            label={t('tasks.scopeLabel')}
            value={scope}
            onChange={(v) => set('scope', v, 'me')}
            options={[
              { value: 'me', label: t('tasks.mine') },
              { value: 'all', label: t('tasks.everyone') },
            ]}
          />
        )}
        {summaryText && (
          <p className="dam-tasks__summary" aria-live="polite">
            {summaryText}
          </p>
        )}
      </div>

      {query.isPending ? (
        <LoadingRegion label={t('tasks.loading')}>
          <RowsSkeleton />
        </LoadingRegion>
      ) : query.error ? (
        <div className="dam-tasks__error" role="alert">
          <span>{t('tasks.error')}</span>
          <Button variant="secondary" size="sm" onClick={() => void query.refetch()}>
            {t('tasks.retry')}
          </Button>
        </div>
      ) : items.length === 0 ? (
        <EmptyState
          icon={view === 'pending' ? <CheckCircle /> : <ClipboardText />}
          title={empty.title}
          description={empty.body}
        />
      ) : (
        <>
          <ul className="dam-tasklist" aria-label={t(view === 'pending' ? 'tasks.listPending' : 'tasks.listDecided')}>
            {items.map((task) => (
              <TaskRow key={task.id} task={task} thumb={thumbs.data?.[task.assetId]} now={now} scope={scope} />
            ))}
          </ul>
          {query.hasNextPage && (
            <div className="dam-tasks__more">
              <Button variant="secondary" loading={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
                {t('tasks.loadMore')}
              </Button>
            </div>
          )}
        </>
      )}
    </>
  );
}

/** `/tasks`: the approval inbox, plus (for people who edit assets) the translations waiting for review. The approvals view is
 * the default and unchanged; the Translations tab appears for editors and whenever something is waiting for the reader. */
export function TasksPage() {
  const { t } = useTranslation();
  useDocumentTitle(t('tasks.title'));
  const { data: me } = useMe();
  const [params, setParams] = useSearchParams();
  const waiting = useReviewSummary().data?.translation ?? 0;
  const tab = params.get('tab') === 'translations' ? 'translations' : 'approvals';
  const showTranslations = !!me?.permissions.includes('assets.update') || waiting > 0 || tab === 'translations';

  return (
    <div className="dam-page dam-tasks">
      <PageHeader title={t('tasks.title')} description={t('tasks.description')} />
      {showTranslations ? (
        <Tabs
          label={t('tasks.tabs.label')}
          value={tab}
          // Each tab keeps its own filters out of the address: switching drops the other's.
          onChange={(id) => setParams(id === 'approvals' ? {} : { tab: id }, { replace: true })}
          tabs={[
            { id: 'approvals', label: t('tasks.tabs.approvals'), content: <ApprovalTasks /> },
            {
              id: 'translations',
              label: waiting > 0 ? t('tasks.tabs.translationsCount', { count: waiting }) : t('tasks.tabs.translations'),
              content: <TranslationsTab />,
            },
          ]}
        />
      ) : (
        <ApprovalTasks />
      )}
    </div>
  );
}
