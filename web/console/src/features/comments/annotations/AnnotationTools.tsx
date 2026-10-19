import { BoundingBox, Eye, EyeSlash } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton } from '../../../ui';
import type { ImageAnnotations } from './useImageAnnotations';

/** The annotate controls for the stage toolbar: annotate mode (only with `comments.write`) and show/hide of the existing
 * annotations (only when there are any). */
export function AnnotationTools({ ann }: { ann: ImageAnnotations }) {
  const { t } = useTranslation();
  if (!ann.canAnnotate && ann.total === 0) return null;
  return (
    <>
      {ann.canAnnotate && (
        <Button
          variant={ann.annotating ? 'primary' : 'ghost'}
          size="sm"
          aria-pressed={ann.annotating}
          aria-keyshortcuts="A"
          title={t('annotations.annotateHint')}
          onClick={ann.toggleAnnotating}
        >
          <BoundingBox size={16} weight="bold" aria-hidden="true" />
          <span className="dam-annot__tool-label">{t('annotations.annotate')}</span>
        </Button>
      )}
      {ann.total > 0 && (
        <IconButton
          size="sm"
          label={t('annotations.show', { count: ann.total })}
          aria-pressed={ann.visible}
          onClick={() => ann.setVisible(!ann.visible)}
        >
          {ann.visible ? (
            <Eye size={16} weight="bold" aria-hidden="true" />
          ) : (
            <EyeSlash size={16} weight="bold" aria-hidden="true" />
          )}
        </IconButton>
      )}
    </>
  );
}
