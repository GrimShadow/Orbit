import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Modal } from '../../ui';

interface NameCollectionDialogProps {
  open: boolean;
  title: string;
  busy?: boolean;
  onClose: () => void;
  onSave: (name: string) => void;
}

/** The one small piece of UI shared between "save this search" (Search page, a smart collection) and "save selection
 * as a collection" (Library bulk edit bar, a static collection) - naming is the only input either flow needs beyond
 * what it already has (the current query, or the current selection). */
export function NameCollectionDialog({ open, title, busy, onClose, onSave }: NameCollectionDialogProps) {
  const { t } = useTranslation();
  const [name, setName] = useState('');

  return (
    <Modal
      open={open}
      title={title}
      onClose={() => {
        setName('');
        onClose();
      }}
    >
      <form
        className="dam-stack"
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim()) onSave(name.trim());
        }}
      >
        <Input label={t('collections.nameLabel')} value={name} onChange={(e) => setName(e.target.value)} />
        <Button type="submit" variant="primary" loading={busy} disabled={!name.trim()}>
          {t('collections.save')}
        </Button>
      </form>
    </Modal>
  );
}
