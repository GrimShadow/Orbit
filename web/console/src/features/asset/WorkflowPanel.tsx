import { CheckCircle, Circle, MinusCircle, Prohibit, XCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Badge, Card, Skeleton, type BadgeTone } from '../../ui';
import { relativeTime } from '../../lib/relativeTime';
import { DueBadge } from '../workflow/DueBadge';
import { useNow } from '../workflow/dueText';
import { TaskDecision } from '../workflow/TaskDecision';
import {
  useAssetWorkflow,
  useTasks,
  type WorkflowInstanceView,
  type WorkflowStepView,
  type WorkflowTask,
} from '../workflow/workflowApi';
import './workflowPanel.css';

/** An asset that was never submitted has no approval to show; a deleted one is out of the process. Everything else might
 * be in review, approved by a workflow, or back in draft after a rejection - so ask, and stay silent on "none". */
export const hasWorkflowHistory = (status: string) => status !== 'deleted';

/** The approval a running (or finished) workflow gave this asset: `null` while loading, on error and when none applies. */
export function useRunningStep(assetId: string, status: string): { running: boolean; pending: boolean; stepName: string | null } {
  const wf = useAssetWorkflow(assetId, hasWorkflowHistory(status));
  const instance = wf.data;
  const running = instance?.status === 'running';
  return {
    running,
    // Only an in-review asset can have a running instance: until we know, don't offer the direct actions it would refuse.
    pending: status === 'in_review' && wf.isPending && wf.failureCount === 0,
    stepName: running ? (instance.steps.find((s) => s.state === 'active')?.name ?? null) : null,
  };
}

const STATE_ICON = {
  done: <CheckCircle size={22} weight="fill" aria-hidden="true" />,
  active: <span className="dam-apv-step__dot" aria-hidden="true" />,
  waiting: <Circle size={22} weight="regular" aria-hidden="true" />,
  skipped: <MinusCircle size={22} weight="regular" aria-hidden="true" />,
  rejected: <XCircle size={22} weight="fill" aria-hidden="true" />,
} as const;

function who(t: ReturnType<typeof useTranslation>['t'], task: WorkflowTask): string {
  const parts = [...task.assigneeRoles];
  if (task.assigneeUserIds.length > 0) parts.push(t('assetWorkflow.namedPeople', { count: task.assigneeUserIds.length }));
  return parts.join(', ');
}

function TaskLine({ task, now, canDecide }: { task: WorkflowTask; now: number; canDecide: boolean }) {
  const { t, i18n } = useTranslation();
  const outcome = task.decision === 'approve' ? 'approve' : task.decision === 'reject' ? 'reject' : null;
  const tone: BadgeTone = outcome === 'approve' ? 'success' : outcome === 'reject' ? 'danger' : 'neutral';
  return (
    <li className="dam-apv-task">
      <div className="dam-apv-task__row">
        <span className="dam-apv-task__who">{who(t, task)}</span>
        {task.status === 'pending' ? (
          <>
            <DueBadge task={task} now={now} />
            {task.escalated && (
              <Badge tone="warning" size="sm" title={t('tasks.escalatedHint')}>
                {t('tasks.escalated')}
              </Badge>
            )}
          </>
        ) : outcome ? (
          <Badge tone={tone} size="sm">
            {t(`tasks.decision.${outcome}`)}
          </Badge>
        ) : (
          <Badge size="sm">{t('assetWorkflow.taskCancelled')}</Badge>
        )}
      </div>
      {outcome && (
        <p className="dam-apv-task__by">
          {task.decidedByName && <span>{t('tasks.by', { name: task.decidedByName })}</span>}
          {task.decidedAt && (
            <>
              {task.decidedByName ? ' · ' : ''}
              <time dateTime={task.decidedAt} title={new Date(task.decidedAt).toLocaleString(i18n.language)}>
                {relativeTime(task.decidedAt, i18n.language)}
              </time>
            </>
          )}
        </p>
      )}
      {task.comment && <blockquote className="dam-apv-task__comment">{task.comment}</blockquote>}
      {canDecide && (
        <div className="dam-apv-task__decide">
          <p className="dam-apv-task__your">{t('assetWorkflow.yourTurn')}</p>
          <TaskDecision task={task} />
        </div>
      )}
    </li>
  );
}

function Step({ step, now, mine }: { step: WorkflowStepView; now: number; mine: Set<string> }) {
  const { t } = useTranslation();
  const rejected = step.tasks.some((x) => x.status === 'rejected');
  const meta = [
    t(`assetWorkflow.mode.${step.mode === 'all' ? 'all' : 'any'}`),
    step.slaHours ? t('assetWorkflow.sla', { count: step.slaHours }) : null,
  ]
    .filter(Boolean)
    .join(' · ');
  return (
    <li
      className={`dam-apv-step dam-apv-step--${step.state}${rejected ? ' dam-apv-step--rejected' : ''}`}
      aria-current={step.state === 'active' ? 'step' : undefined}
    >
      <span className="dam-apv-step__marker">
        {rejected ? STATE_ICON.rejected : (STATE_ICON[step.state as keyof typeof STATE_ICON] ?? STATE_ICON.waiting)}
      </span>
      <div className="dam-apv-step__body">
        <div className="dam-apv-step__head">
          <span className="dam-apv-step__name">{step.name}</span>
          <span className="dam-apv-step__state">{t(`assetWorkflow.state.${step.state}`, { defaultValue: step.state })}</span>
        </div>
        <p className="dam-apv-step__meta">{meta}</p>
        {step.tasks.length > 0 && (
          <ul className="dam-apv-tasks">
            {step.tasks.map((task) => (
              <TaskLine key={task.id} task={task} now={now} canDecide={task.status === 'pending' && mine.has(task.id)} />
            ))}
          </ul>
        )}
      </div>
    </li>
  );
}

function Outcome({ instance }: { instance: WorkflowInstanceView }) {
  const { t, i18n } = useTranslation();
  if (instance.status === 'running') return null;
  const kind = instance.status === 'approved' ? 'approved' : instance.status === 'rejected' ? 'rejected' : 'cancelled';
  // A rejection's reason IS the reviewer's comment, which the step below already shows: say it once.
  const showReason =
    !!instance.outcomeReason && !instance.steps.some((st) => st.tasks.some((x) => x.comment === instance.outcomeReason));
  const Icon = kind === 'approved' ? CheckCircle : kind === 'rejected' ? XCircle : Prohibit;
  return (
    <div className={`dam-apv-outcome dam-apv-outcome--${kind}`} role="note">
      <Icon size={20} weight="fill" aria-hidden="true" className="dam-apv-outcome__icon" />
      <div>
        <p className="dam-apv-outcome__title">
          <strong>{t(`assetWorkflow.outcome.${kind}`)}</strong>
          {instance.completedAt && (
            <>
              {' · '}
              <time dateTime={instance.completedAt} title={new Date(instance.completedAt).toLocaleString(i18n.language)}>
                {relativeTime(instance.completedAt, i18n.language)}
              </time>
            </>
          )}
        </p>
        {showReason && <p className="dam-apv-outcome__reason">{instance.outcomeReason}</p>}
      </div>
    </div>
  );
}

/** The approval timeline on the asset page: the workflow's steps in order (done, in progress, waiting, skipped), who has
 * to decide each, what they decided and why, and - when the current user is one of the deciders - Approve / Reject right
 * on the step. Renders nothing for an asset no workflow ever applied to. */
export function WorkflowPanel({ assetId, status }: { assetId: string; status: string }) {
  const { t, i18n } = useTranslation();
  const query = useAssetWorkflow(assetId, hasWorkflowHistory(status));
  const instance = query.data;
  const running = instance?.status === 'running';
  const now = useNow();
  // Which of the open tasks may *I* decide? Ask the inbox (it applies assignee, scope and segregation rules) - never guess.
  const mineQuery = useTasks({ assignee: 'me', status: 'pending', limit: 200, enabled: running });
  const mine = new Set(
    (mineQuery.data?.pages.flatMap((p) => p.items) ?? []).filter((x) => x.assetId === assetId).map((x) => x.id),
  );

  if (query.isPending && status === 'in_review')
    return (
      <Card as="section" padding="md" aria-label={t('assetWorkflow.title')}>
        <Skeleton width="40%" height={18} />
        <Skeleton width="100%" height={48} />
      </Card>
    );
  if (!instance) return null;

  const started = relativeTime(instance.startedAt, i18n.language);
  return (
    <Card as="section" padding="md" aria-labelledby="dam-apv-title" className="dam-apv">
      <h2 id="dam-apv-title" className="dam-metasection__title">
        {t('assetWorkflow.title')}
      </h2>
      <p className="dam-apv__meta">
        {t('assetWorkflow.workflow', { name: instance.workflowName })}
        {' · '}
        {instance.startedByName
          ? t('assetWorkflow.startedBy', { name: instance.startedByName, when: started })
          : t('assetWorkflow.started', { when: started })}
      </p>
      <Outcome instance={instance} />
      <ol className="dam-apv-steps" aria-label={t('assetWorkflow.stepsLabel')}>
        {instance.steps.map((step) => (
          <Step key={step.id} step={step} now={now} mine={mine} />
        ))}
      </ol>
    </Card>
  );
}
