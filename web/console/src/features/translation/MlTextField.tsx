import { ArrowsLeftRight, WarningCircle } from '@phosphor-icons/react';
import { useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CaretDown, IconButton, SegmentedControl } from '../../ui';
import { languageName, useEnabledLanguages } from './languages';
import { TranslateInline } from './TranslateInline';
import { useTranslationScope } from './TranslationScope';
import './translation.css';

export interface MlTextFieldProps {
  /** The field's schema name (what the server's translate route and suggestions call it). */
  name: string;
  label: string;
  required?: boolean;
  maxLength?: number | null;
  /** language code -> text */
  value: Record<string, string>;
  onChange: (next: Record<string, string>) => void;
  onTouch?: () => void;
  error?: string;
}

type Layout = 'side' | 'stacked';

/**
 * A multilingual text field: two languages shown together, a source and a target, chosen from the tenant's enabled
 * languages. Next to the target the person can ask AI for a translation and review what it suggests (an "AI suggestion"
 * card to accept, edit or reject). The values are the usual `{ language: text }` document, so it plugs into the same
 * draft and save bar as every other field; the AI parts need the asset's scope and are absent on the upload form.
 */
export function MlTextField({ name, label, required, maxLength, value, onChange, onTouch, error }: MlTextFieldProps) {
  const { t, i18n } = useTranslation();
  const languages = useEnabledLanguages();
  const scope = useTranslationScope();
  const id = useId();
  const [source, setSource] = useState(languages.includes('en') ? 'en' : (languages[0] ?? 'en'));
  const [target, setTarget] = useState(
    languages.includes('hi') ? 'hi' : (languages.find((l) => l !== source) ?? languages[0] ?? 'en'),
  );
  const [layout, setLayout] = useState<Layout>('side');
  const targetBox = useRef<HTMLTextAreaElement>(null);

  const filled = languages.filter((l) => (value[l] ?? '').trim() !== '').length;
  const langName = (code: string) => languageName(code, i18n.language);

  // Choosing the language the other pane shows swaps them, so the two panes never show the same language.
  const pick = (which: 'source' | 'target', code: string) => {
    if (which === 'source') {
      if (code === target) setTarget(source);
      setSource(code);
    } else {
      if (code === source) setSource(target);
      setTarget(code);
    }
  };
  const swap = () => {
    setSource(target);
    setTarget(source);
  };

  const pane = (which: 'source' | 'target') => {
    const code = which === 'source' ? source : target;
    const text = value[code] ?? '';
    const over = maxLength != null && text.length > maxLength;
    const textId = `${id}-${which}`;
    return (
      <div className="dam-ml__pane" data-role={which}>
        <div className="dam-ml__panehead">
          <span className="dam-ml__role" aria-hidden="true">
            {t(which === 'source' ? 'translation.editor.source' : 'translation.editor.target')}
          </span>
          <span className="dam-select-wrap dam-ml__select">
            <select
              className="dam-select"
              aria-label={t(which === 'source' ? 'translation.editor.sourceLanguage' : 'translation.editor.targetLanguage', {
                field: label,
              })}
              value={code}
              onChange={(e) => pick(which, e.target.value)}
            >
              {languages.map((l) => (
                <option key={l} value={l}>
                  {`${langName(l)} (${l})${(value[l] ?? '').trim() ? '' : ` - ${t('translation.editor.empty')}`}`}
                </option>
              ))}
            </select>
            <CaretDown size={14} weight="bold" aria-hidden="true" className="dam-select__chevron" />
          </span>
        </div>
        <label className="dam-sr-only" htmlFor={textId}>
          {`${label} - ${langName(code)}`}
        </label>
        <textarea
          id={textId}
          ref={which === 'target' ? targetBox : undefined}
          lang={code}
          rows={4}
          className="dam-input dam-metafield__textarea"
          placeholder={langName(code)}
          value={text}
          aria-required={required || undefined}
          aria-invalid={error || over ? true : undefined}
          aria-describedby={`${textId}-count${error ? ` ${id}-error` : ''}`}
          onBlur={onTouch}
          onChange={(e) => onChange({ ...value, [code]: e.target.value })}
        />
        <p id={`${textId}-count`} className="dam-hint dam-ml__count" data-over={over || undefined}>
          {maxLength != null
            ? t('translation.charsOf', { count: text.length, max: maxLength })
            : t('translation.chars', { count: text.length })}
          {over && <span className="dam-sr-only"> {t('translation.overLimit')}</span>}
        </p>
        {which === 'target' && scope && (
          <TranslateInline
            scope={scope}
            field={name}
            fieldLabel={label}
            source={source}
            target={target}
            sourceText={value[source] ?? ''}
            onDecided={(d) => d === 'accept' && targetBox.current?.focus()}
          />
        )}
      </div>
    );
  };

  return (
    <div className="dam-field dam-ml">
      <div className="dam-ml__head">
        <span className="dam-label">{required ? `${label} *` : label}</span>
        <span className="dam-hint">{t('asset.ml.filled', { filled, total: languages.length })}</span>
      </div>
      <div className="dam-ml__tools">
        <SegmentedControl
          label={t('translation.editor.layout', { field: label })}
          value={layout}
          onChange={(v) => setLayout(v as Layout)}
          options={[
            { value: 'side', label: t('translation.editor.sideBySide') },
            { value: 'stacked', label: t('translation.editor.stacked') },
          ]}
        />
        <IconButton size="sm" label={t('translation.editor.swap', { field: label })} onClick={swap}>
          <ArrowsLeftRight size={16} weight="bold" aria-hidden="true" />
        </IconButton>
      </div>
      <div className="dam-ml__panes" data-layout={layout}>
        {pane('source')}
        {pane('target')}
      </div>
      {error && (
        <span id={`${id}-error`} className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </span>
      )}
    </div>
  );
}
