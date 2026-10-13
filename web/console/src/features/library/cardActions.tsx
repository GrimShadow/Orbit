import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
import { Eye, useToast, type CardAction } from '../../ui';
import { ActionIcon } from '../asset/actionIcon';
import { availableActions } from '../asset/lifecycle';
import { useBulkTransition } from './bulkEditApi';

/** What a grid card's "..." menu offers: open the asset, plus whichever lifecycle transitions are valid for its current
 * status (the same table the detail page and bulk bar use - `../asset/lifecycle`; the server re-validates every one).
 * Shared by the library, search and collection grids so a card behaves identically wherever it appears. */
export function useCardActions(assets: { id: string; status: string }[]): (id: string) => CardAction[] {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const transition = useBulkTransition();
  const { push } = useToast();

  return (id) => {
    const asset = assets.find((a) => a.id === id);
    if (!asset) return [];
    const open: CardAction = {
      key: 'open',
      label: t('library.card.open'),
      icon: <Eye size={16} weight="bold" aria-hidden="true" />,
      onSelect: () => navigate(`/library/assets/${id}`),
    };
    const lifecycle = availableActions(asset.status).map<CardAction>((action) => ({
      key: action,
      label: t(`asset.action.${action}`),
      icon: <ActionIcon action={action} size={16} weight="bold" aria-hidden="true" />,
      tone: action === 'reject' ? 'danger' : undefined,
      onSelect: () =>
        transition.mutate(
          { ids: [id], action },
          {
            onSuccess: (result) =>
              push(
                result.failed.length === 0
                  ? { title: t('library.card.actionDone', { action: t(`asset.action.${action}`) }), tone: 'success' }
                  : { title: t('library.card.actionFailed', { action: t(`asset.action.${action}`) }), tone: 'error' },
              ),
          },
        ),
    }));
    return [open, ...lifecycle];
  };
}
