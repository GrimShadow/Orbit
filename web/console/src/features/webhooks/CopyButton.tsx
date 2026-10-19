import { Check, Copy } from '@phosphor-icons/react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { IconButton, useToast } from '../../ui';

/** Copies `text`; false when the browser refuses (insecure origin, no permission). */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

/** An icon button that copies `text`, flips to a check mark for two seconds and says so in a toast. */
export function CopyButton({ text, label, size = 'sm' }: { text: string; label: string; size?: 'sm' | 'md' }) {
  const { t } = useTranslation();
  const { push } = useToast();
  const [done, setDone] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  return (
    <>
      <IconButton
        label={label}
        size={size}
        onClick={async () => {
          if (await copyText(text)) {
            setDone(true);
            clearTimeout(timer.current);
            timer.current = setTimeout(() => setDone(false), 2000);
            push({ title: t('webhooks.copied'), tone: 'success', durationMs: 2500 });
          } else push({ title: t('webhooks.copyFailed'), tone: 'error' });
        }}
      >
        {done ? <Check size={16} weight="bold" aria-hidden="true" /> : <Copy size={16} aria-hidden="true" />}
      </IconButton>
      {/* Announced even when a modal covers the toast region. */}
      <span className="dam-sr-only" role="status">
        {done ? t('webhooks.copied') : ''}
      </span>
    </>
  );
}
