import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Modal } from '../../ui';

interface NameCollectionDialogProps {
  open: boolean;
  title: string;
  busy?: boolean;
  /** One line of context under the title (what is about to be saved). */
  description?: string;
  /** Text of the confirm button; "Save" by default. */
  confirmLabel?: string;
  /** Server/validation message to show under the name field. */
  error?: string;
  onClose: () => void;
  onSave: (name: string) => void;
}

function NameForm({
  busy,
  description,
  confirmLabel,
  error,
  onClose,
  onSave,
}: Omit<NameCollectionDialogProps, 'open' | 'title'>) {
  const { t } = useTranslation();
  const [name, setName] = useState('');
  return (
    <form
      className="dam-stack dam-dialogform"
      onSubmit={(e) => {
        e.preventDefault();
        if (name.trim()) onSave(name.trim());
      }}
    >
      {description && <p className="dam-hint">{description}</p>}
      <Input label={t('collections.nameLabel')} value={name} onChange={(e) => setName(e.target.value)} error={error} />
      <div className="dam-dialogform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('collections.cancel')}
        </Button>
        <Button type="submit" variant="primary" loading={busy} disabled={!name.trim()}>
          {confirmLabel ?? t('collections.save')}
        </Button>
      </div>
    </form>
  );
}

/** The one small piece of UI shared between "save this search" (Search page, a smart collection), "save selection
 * as a collection" (Library bulk edit bar, a static collection) and "New collection" - naming is the only input any
 * of them needs beyond what it already has (the current query, the current selection, or nothing). The form is
 * remounted on every open, so a name typed earlier never lingers. */
export function NameCollectionDialog({ open, title, onClose, ...form }: NameCollectionDialogProps) {
  return (
    <Modal open={open} title={title} onClose={onClose}>
      <NameForm {...form} onClose={onClose} />
    </Modal>
  );
}
