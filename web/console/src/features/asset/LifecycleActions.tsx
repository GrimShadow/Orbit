import { useTranslation } from 'react-i18next';
import { Button } from '../../ui';
import { useTransition } from './assetApi';
import { availableActions } from './lifecycle';

export function LifecycleActions({ assetId, status }: { assetId: string; status: string }) {
  const { t } = useTranslation();
  const transition = useTransition(assetId);
  const actions = availableActions(status);
  if (actions.length === 0) return null;

  return (
    <div className="dam-library__viewToggle" role="group" aria-label={t('asset.lifecycleActions')}>
      {actions.map((action) => (
        <Button
          key={action}
          variant={action === 'reject' ? 'danger' : 'secondary'}
          loading={transition.isPending && transition.variables?.action === action}
          onClick={() => transition.mutate({ action })}
        >
          {t(`asset.action.${action}`)}
        </Button>
      ))}
      {transition.isError && (
        <span role="alert" className="dam-hint">
          {transition.error.message}
        </span>
      )}
    </div>
  );
}
