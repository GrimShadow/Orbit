import { Check, Copy } from '@phosphor-icons/react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { IconButton } from '../../ui';

/** Copies `value` to the clipboard; the icon turns into a tick for a moment and a hidden live region says so. */
export function CopyButton({ value, label }: { value: string; label: string }) {
  const { t } = useTranslation();
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout>>();
  useEffect(() => () => clearTimeout(timer.current), []);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      clearTimeout(timer.current);
      timer.current = setTimeout(() => setCopied(false), 1800);
    } catch {
      /* clipboard blocked (insecure context or permissions): the value is still selectable on screen */
    }
  };

  return (
    <span className="dam-copy">
      <IconButton label={label} size="sm" onClick={() => void copy()}>
        {copied ? <Check size={14} weight="bold" aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
      </IconButton>
      <span className="dam-sr-only" role="status">
        {copied ? t('audit.copied') : ''}
      </span>
    </span>
  );
}
