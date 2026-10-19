import { useCallback, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, CaretDown, DotsThree, IconButton, PopoverMenu, useToast, type MenuItem } from '../../ui';
import { ActionIcon } from './actionIcon';
import { errorMessage, useTransition } from './assetApi';
import { availableActions, primaryAction } from './lifecycle';
import { useRunningStep } from './WorkflowPanel';

/** While a workflow runs, approving and rejecting are decisions on its tasks, not on the asset: the server refuses the
 * direct transitions (409) for everybody, so they are not offered. */
const TASK_DECIDED = new Set(['approve', 'reject']);

/** The lifecycle controls: the next step as the prominent button, every other valid action (reject stays danger-toned)
 * in an overflow menu. Which actions are valid is only a courtesy - the server re-checks each transition. */
export function LifecycleActions({ assetId, status }: { assetId: string; status: string }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const transition = useTransition(assetId);
  const [open, setOpen] = useState(false);
  const [trigger, setTrigger] = useState<HTMLButtonElement | null>(null);
  const menuId = useId();
  const close = useCallback(() => setOpen(false), []);

  const workflow = useRunningStep(assetId, status);
  // An in-review asset's controls wait for the answer (one quick request) rather than flashing Approve and taking it away.
  if (workflow.pending) return null;
  const actions = availableActions(status).filter((a) => !(workflow.running && TASK_DECIDED.has(a)));
  if (actions.length === 0 && !workflow.running) return null;
  const primary = actions.includes(primaryAction(status) ?? '') ? primaryAction(status) : null;
  const pendingAction = transition.isPending ? transition.variables?.action : undefined;

  const run = (action: string) =>
    transition.mutate(
      { action },
      {
        onSuccess: (asset) =>
          push({ title: t('asset.transitionDone', { status: t(`library.status.${asset.status}`) }), tone: 'success' }),
        onError: (e) =>
          push({
            title: t('asset.transitionFailed', { action: t(`asset.action.${action}`) }),
            message: errorMessage(e),
            tone: 'error',
          }),
      },
    );

  const items: MenuItem[] = actions
    .filter((a) => a !== primary)
    .map((action) => ({
      key: action,
      label: t(`asset.action.${action}`),
      icon: <ActionIcon action={action} size={16} weight="bold" />,
      tone: action === 'reject' ? ('danger' as const) : undefined,
      separatorBefore: action === 'reject' || action === 'withdraw',
      disabled: transition.isPending,
      onSelect: () => run(action),
    }));
  if (items[0]) items[0] = { ...items[0], separatorBefore: false };

  const toggle = () => setOpen((o) => !o);
  const menuProps = {
    'aria-haspopup': 'menu' as const,
    'aria-expanded': open,
    'aria-controls': open ? menuId : undefined,
  };

  return (
    <div className="dam-lifecycle" role="group" aria-label={t('asset.lifecycleActions')}>
      {workflow.running && (
        <p className="dam-lifecycle__awaiting">
          {workflow.stepName ? t('assetWorkflow.awaiting', { step: workflow.stepName }) : t('assetWorkflow.awaitingGeneric')}
        </p>
      )}
      {primary && (
        <Button
          variant="primary"
          loading={pendingAction === primary}
          disabled={transition.isPending}
          onClick={() => run(primary)}
        >
          <ActionIcon action={primary} size={16} weight="bold" aria-hidden="true" />
          {t(`asset.action.${primary}`)}
        </Button>
      )}
      {items.length > 0 &&
        (primary ? (
          <IconButton ref={setTrigger} variant="secondary" label={t('asset.moreActions')} onClick={toggle} {...menuProps}>
            <DotsThree size={18} weight="bold" aria-hidden="true" />
          </IconButton>
        ) : (
          <Button ref={setTrigger} variant="secondary" onClick={toggle} {...menuProps}>
            {t('asset.actions')}
            <CaretDown size={14} weight="bold" aria-hidden="true" />
          </Button>
        ))}
      {open && <PopoverMenu id={menuId} anchor={trigger} label={t('asset.moreActions')} items={items} onClose={close} />}
    </div>
  );
}
