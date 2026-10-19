import { CaretDown, DownloadSimple } from '@phosphor-icons/react';
import { useCallback, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, PopoverMenu, useToast, type MenuItem } from '../../ui';
import { formatBytes } from '../../lib/format';
import { useAuditExport, type ExportFormat } from './auditApi';
import type { AuditFilters } from './auditFilters';

/** "Export" menu: CSV for spreadsheets, CEF for SIEM tools. The file is fetched with the bearer token and saved from a
 * Blob; the button shows progress while it streams in. */
export function ExportMenu({ filters, disabled }: { filters: AuditFilters; disabled?: boolean }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const { state, run } = useAuditExport();
  const [open, setOpen] = useState(false);
  const [trigger, setTrigger] = useState<HTMLButtonElement | null>(null);
  const menuId = useId();
  const close = useCallback(() => setOpen(false), []);

  const start = async (format: ExportFormat) => {
    try {
      const { filename, bytes } = await run(format, filters);
      push({
        title: t('audit.export.done', { name: filename }),
        message: t('audit.export.doneBody', { size: formatBytes(bytes) }),
        tone: 'success',
      });
    } catch {
      push({ title: t('audit.export.failed'), message: t('audit.export.failedBody'), tone: 'error' });
    }
  };

  const items: MenuItem[] = [
    { key: 'csv', label: t('audit.export.csv'), onSelect: () => void start('csv') },
    { key: 'cef', label: t('audit.export.cef'), onSelect: () => void start('cef') },
  ];

  return (
    <div className="dam-auditexport">
      <Button
        ref={setTrigger}
        variant="secondary"
        disabled={disabled || state.busy}
        loading={state.busy}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={() => setOpen((o) => !o)}
      >
        {!state.busy && <DownloadSimple size={16} weight="bold" aria-hidden="true" />}
        {state.busy
          ? state.bytes > 0
            ? t('audit.export.preparingSize', { size: formatBytes(state.bytes) })
            : t('audit.export.preparing')
          : t('audit.export.button')}
        {!state.busy && <CaretDown size={12} weight="bold" aria-hidden="true" />}
      </Button>
      <span className="dam-sr-only" role="status">
        {state.busy ? t('audit.export.preparing') : ''}
      </span>
      {open && <PopoverMenu id={menuId} anchor={trigger} label={t('audit.export.menuLabel')} items={items} onClose={close} />}
    </div>
  );
}
