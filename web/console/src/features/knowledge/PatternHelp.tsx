import { useId } from 'react';
import { useTranslation } from 'react-i18next';

const ROWS = [
  { key: 'literal', pattern: 'ACM', example: 'ACM*' },
  { key: 'any', pattern: '?', example: 'ACM??1*' },
  { key: 'set', pattern: '[ABC]', example: 'ACM[LS]1*' },
  { key: 'range', pattern: '[A-C]', example: 'ACM[A-C]*' },
  { key: 'rest', pattern: '*', example: 'ACMLS1*' },
] as const;

/** A short, always-available explanation of the VIN pattern language (ADR-0039 3), with examples. A disclosure, so it stays out
 * of the way once understood. All names in the examples are fictional. */
export function PatternHelp() {
  const { t } = useTranslation();
  const id = useId();
  return (
    <details className="dam-knhelp">
      <summary>{t('knowledge.help.summary')}</summary>
      <div id={id} className="dam-knhelp__body">
        <p>{t('knowledge.help.intro')}</p>
        <ul className="dam-knhelp__list">
          {ROWS.map((r) => (
            <li key={r.key}>
              <code className="dam-chip-code">{r.pattern}</code>
              <span>
                {t(`knowledge.help.${r.key}`)} <code className="dam-chip-code">{r.example}</code>
              </span>
            </li>
          ))}
        </ul>
        <p>{t('knowledge.help.length')}</p>
        <p>
          <code className="dam-chip-code">ACMLS1AB1N0123456</code> {t('knowledge.help.full')}
        </p>
        <p className="dam-hint">{t('knowledge.help.specific')}</p>
      </div>
    </details>
  );
}
