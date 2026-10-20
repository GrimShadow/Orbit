import { Copy } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, useToast } from '../../../ui';
import { Disclosure } from './Disclosure';
import { languageName } from './aiFormat';

/**
 * The text read from a picture or a scanned page: derived data, shown as it is and already searchable, never edited here.
 * Folded away by default (it can be long); Copy works while it is folded.
 */
export function OcrBlock({
  text,
  truncated,
  language,
  document,
}: {
  text: string;
  truncated: boolean;
  language: string | null;
  document: boolean;
}) {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text);
      push({ title: t('aiAsset.ocr.copied'), tone: 'success', durationMs: 3000 });
    } catch {
      push({ title: t('aiAsset.ocr.copyFailed'), tone: 'error' });
    }
  };
  return (
    <Disclosure
      title={t(document ? 'aiAsset.ocr.titleDocument' : 'aiAsset.ocr.title')}
      actions={
        <Button size="sm" variant="ghost" onClick={() => void copy()}>
          <Copy size={16} weight="bold" aria-hidden="true" />
          {t('aiAsset.ocr.copy')}
        </Button>
      }
    >
      <div className="dam-ai-ocr">
        <div className="dam-ai-ocr__meta">
          {language && (
            <Badge size="sm" tone="neutral">
              {t('aiAsset.ocr.language', { language: languageName(language, i18n.language) })}
            </Badge>
          )}
          <span className="dam-hint">{t('aiAsset.ocr.characters', { count: text.length })}</span>
        </div>
        <pre
          className="dam-ai-ocr__text"
          role="region"
          // A scrolling region must be reachable by keyboard (axe: scrollable-region-focusable).
          // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex
          tabIndex={0}
          aria-label={t(document ? 'aiAsset.ocr.titleDocument' : 'aiAsset.ocr.title')}
        >
          {text}
        </pre>
        {truncated && <p className="dam-hint dam-ai-hint">{t('aiAsset.ocr.truncated')}</p>}
      </div>
    </Disclosure>
  );
}
