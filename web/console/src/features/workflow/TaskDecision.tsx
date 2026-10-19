import { ChatText } from '@phosphor-icons/react';
import { useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../../api/client';
import { Button, IconButton, Modal, useToast, WarningCircle } from '../../ui';
import { errorMessage } from '../asset/assetApi';
import { useDecideTask, type WorkflowTask } from './workflowApi';
import './taskDecision.css';

const MAX_COMMENT = 1000;
type Decision = 'approve' | 'reject';

function DecisionForm({
  decision,
  busy,
  onCancel,
  onSubmit,
}: {
  decision: Decision;
  busy: boolean;
  onCancel: () => void;
  onSubmit: (comment: string) => void;
}) {
  const { t } = useTranslation();
  const id = useId();
  const [comment, setComment] = useState('');
  const [touched, setTouched] = useState(false);
  const reject = decision === 'reject';
  const missing = reject && !comment.trim();
  const error = touched && missing ? t('tasks.dialog.commentRequiredError') : undefined;

  return (
    <form
      className="dam-stack dam-taskdialog"
      noValidate // our own message below, in the product's voice, instead of the browser's bubble
      onSubmit={(e) => {
        e.preventDefault();
        setTouched(true);
        if (!missing) onSubmit(comment.trim());
      }}
    >
      <p className="dam-hint">{t(reject ? 'tasks.dialog.rejectIntro' : 'tasks.dialog.approveIntro')}</p>
      <div className="dam-field">
        <label className="dam-label" htmlFor={id}>
          {t(reject ? 'tasks.dialog.commentRequired' : 'tasks.dialog.commentOptional')}
        </label>
        <textarea
          id={id}
          className="dam-input dam-taskdialog__text"
          value={comment}
          maxLength={MAX_COMMENT}
          rows={4}
          aria-required={reject}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? `${id}-error` : `${id}-count`}
          onChange={(e) => setComment(e.target.value)}
          onBlur={() => setTouched(true)}
          // eslint-disable-next-line jsx-a11y/no-autofocus -- the one field in a dialog opened to fill it
          autoFocus
        />
        {error && (
          <span id={`${id}-error`} className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {error}
          </span>
        )}
        <span id={`${id}-count`} className="dam-hint dam-taskdialog__count">
          {t('tasks.dialog.counter', { count: comment.length, max: MAX_COMMENT })}
        </span>
      </div>
      <div className="dam-taskdialog__actions">
        <Button variant="ghost" onClick={onCancel}>
          {t('tasks.dialog.cancel')}
        </Button>
        <Button type="submit" variant={reject ? 'danger' : 'primary'} loading={busy}>
          {t(reject ? 'tasks.dialog.confirmReject' : 'tasks.dialog.confirmApprove')}
        </Button>
      </div>
    </form>
  );
}

/** Approve / Reject for one pending task. Approve is one click (with a small "approve with a comment" alongside); reject
 * opens a dialog because the reason is required. Shared by the inbox and the asset page so both behave identically. */
export function TaskDecision({ task }: { task: Pick<WorkflowTask, 'id' | 'assetTitle'> }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const queryClient = useQueryClient();
  const decide = useDecideTask();
  const [dialog, setDialog] = useState<Decision | null>(null);
  const [last, setLast] = useState<Decision>('approve');

  const submit = async (decision: Decision, comment: string) => {
    try {
      await decide.mutateAsync({ id: task.id, decision, comment: comment || undefined });
      setDialog(null);
      push({
        title: t(decision === 'approve' ? 'tasks.toast.approved' : 'tasks.toast.rejected', { title: task.assetTitle }),
        tone: 'success',
      });
    } catch (e) {
      push({ title: t('tasks.toast.failed'), message: errorMessage(e), tone: 'error' });
      // A 4xx means the task is no longer ours to decide (someone else got there first): drop the dialog, show the truth.
      if (e instanceof ApiError && e.status >= 400 && e.status < 500) {
        setDialog(null);
        for (const key of ['tasks', 'assetWorkflow', 'asset']) void queryClient.invalidateQueries({ queryKey: [key] });
      }
    }
  };

  const open = (d: Decision) => {
    setLast(d);
    setDialog(d);
  };
  const quickApproving = decide.isPending && dialog === null && decide.variables?.decision === 'approve';

  return (
    <div className="dam-taskdecision" role="group" aria-label={t('tasks.decisionFor', { title: task.assetTitle })}>
      <Button
        size="sm"
        variant="primary"
        loading={quickApproving}
        disabled={decide.isPending}
        onClick={() => void submit('approve', '')}
      >
        {t('tasks.approve')}
      </Button>
      <IconButton
        size="sm"
        variant="secondary"
        label={t('tasks.approveWithComment')}
        disabled={decide.isPending}
        onClick={() => open('approve')}
      >
        <ChatText size={16} weight="bold" aria-hidden="true" />
      </IconButton>
      <Button
        size="sm"
        variant="secondary"
        className="dam-taskdecision__reject"
        disabled={decide.isPending}
        onClick={() => open('reject')}
      >
        {t('tasks.reject')}
      </Button>
      <Modal
        open={dialog !== null}
        title={t(last === 'reject' ? 'tasks.dialog.rejectTitle' : 'tasks.dialog.approveTitle', { title: task.assetTitle })}
        onClose={() => setDialog(null)}
        closeLabel={t('tasks.dialog.cancel')}
      >
        <DecisionForm
          decision={last}
          busy={decide.isPending}
          onCancel={() => setDialog(null)}
          onSubmit={(comment) => void submit(last, comment)}
        />
      </Modal>
    </div>
  );
}
