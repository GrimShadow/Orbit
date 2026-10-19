import { useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { IconButton, PencilSimple, useToast } from '../../ui';
import { errorMessage, useUpdateAsset } from './assetApi';

interface InlineTitleProps {
  assetId: string;
  title: string;
  /** False for an asset that can no longer be edited (deleted). */
  editable: boolean;
}

/** The page's h1 with rename in place: the pencil swaps it for an input; Enter (or leaving the field) saves through
 * `PATCH /assets/{id}`, Escape puts the old title back. Only the title is sent - tags and metadata stay null (untouched). */
export function InlineTitle({ assetId, title, editable }: InlineTitleProps) {
  const { t } = useTranslation();
  const { push } = useToast();
  const update = useUpdateAsset(assetId);
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(title);
  const [empty, setEmpty] = useState(false);
  const pencil = useRef<HTMLButtonElement>(null);
  const settled = useRef(false); // Escape/Enter already decided; the blur that follows must not decide again

  const begin = () => {
    settled.current = false;
    setValue(title);
    setEmpty(false);
    setEditing(true);
  };
  const finish = () => {
    setEditing(false);
    requestAnimationFrame(() => pencil.current?.focus());
  };

  const commit = () => {
    if (settled.current) return;
    const next = value.trim();
    if (next === '') {
      setEmpty(true);
      return;
    }
    settled.current = true;
    if (next === title) {
      finish();
      return;
    }
    update.mutate(
      { title: next, tags: null, metadata: null },
      {
        onSuccess: () => {
          push({ title: t('asset.renamed'), tone: 'success' });
          finish();
        },
        onError: (e) => {
          settled.current = false;
          push({ title: t('asset.renameFailed'), message: errorMessage(e), tone: 'error' });
        },
      },
    );
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') {
      e.preventDefault();
      settled.current = true;
      finish();
    }
  };

  if (editing)
    return (
      <form
        className="dam-asset-title dam-asset-title--editing"
        onSubmit={(e: FormEvent) => {
          e.preventDefault();
          commit();
        }}
      >
        {/* The page keeps its h1 while the title is being edited. */}
        <h1 className="dam-sr-only">{title}</h1>
        <input
          // The field is the whole point of the pencil click, so it takes focus as it appears.
          // eslint-disable-next-line jsx-a11y/no-autofocus
          autoFocus
          className="dam-input dam-asset-title__input"
          aria-label={t('asset.titleLabel')}
          aria-invalid={empty || undefined}
          aria-describedby={empty ? 'dam-asset-title-error' : undefined}
          value={value}
          maxLength={300}
          disabled={update.isPending}
          onChange={(e) => {
            setValue(e.target.value);
            setEmpty(false);
          }}
          onKeyDown={onKeyDown}
          onFocus={(e) => e.currentTarget.select()}
          onBlur={() => {
            if (value.trim() === '' || update.isPending) {
              if (!update.isPending) {
                settled.current = true;
                finish();
              }
              return;
            }
            commit();
          }}
        />
        {empty && (
          <span id="dam-asset-title-error" className="dam-error" role="alert">
            {t('asset.titleRequired')}
          </span>
        )}
      </form>
    );

  return (
    <div className="dam-asset-title">
      <h1 className="dam-asset-title__text">{title}</h1>
      {editable && (
        <IconButton ref={pencil} label={t('asset.rename')} size="sm" onClick={begin}>
          <PencilSimple size={16} weight="bold" aria-hidden="true" />
        </IconButton>
      )}
    </div>
  );
}
