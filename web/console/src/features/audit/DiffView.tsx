import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, SegmentedControl } from '../../ui';
import { buildDiff, formatValue, prettyJson, type DiffKind, type DiffRow } from './auditDiff';
import { CopyButton } from './CopyButton';

const INITIAL_ROWS = 14;
const RAW_COLLAPSED_LINES = 24;

const MARK: Record<DiffKind, string> = { added: '+', removed: '−', changed: '~', unchanged: '' };

function Value({ value, kind }: { value: unknown; kind: 'added' | 'removed' | 'plain' }) {
  const { text, truncated } = formatValue(value);
  return (
    <code className={`dam-diff__val dam-diff__val--${kind}`} title={truncated ? formatValue(value, 5000).text : undefined}>
      {text}
    </code>
  );
}

function Row({ row, mode }: { row: DiffRow; mode: 'diff' | 'after' | 'before' }) {
  const { t } = useTranslation();
  const snapshot = mode !== 'diff';
  return (
    <li className={`dam-diff__row dam-diff__row--${snapshot ? 'plain' : row.kind}${snapshot ? ' dam-diff__row--nomark' : ''}`}>
      {!snapshot && (
        <span className="dam-diff__mark" aria-hidden="true">
          {MARK[row.kind]}
        </span>
      )}
      <span className="dam-diff__path">
        {!snapshot && row.kind !== 'unchanged' && <span className="dam-sr-only">{t(`audit.diff.${row.kind}`)}: </span>}
        {row.path}
      </span>
      <span className="dam-diff__values">
        {row.kind === 'changed' ? (
          <>
            <Value value={row.before} kind="removed" />
            <span className="dam-diff__arrow" aria-hidden="true">
              →
            </span>
            <Value value={row.after} kind="added" />
          </>
        ) : row.kind === 'removed' ? (
          <Value value={row.before} kind="removed" />
        ) : row.kind === 'added' ? (
          <Value value={row.after} kind="added" />
        ) : (
          <Value value={mode === 'before' ? row.before : row.after} kind="plain" />
        )}
      </span>
    </li>
  );
}

function RawBlock({ title, value }: { title: string; value: unknown }) {
  const { t } = useTranslation();
  const text = prettyJson(value);
  const lines = text.split('\n').length;
  const long = lines > RAW_COLLAPSED_LINES;
  const [open, setOpen] = useState(false);
  return (
    <div className="dam-diff__raw">
      <div className="dam-diff__rawhead">
        <h4 className="dam-diff__rawtitle">{title}</h4>
        <CopyButton value={text} label={t('audit.copyJson', { what: title })} />
      </div>
      {/* A scrollable region must be reachable by keyboard so its overflow can be read. */}
      {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex */}
      <pre className={`dam-diff__pre${long && !open ? ' dam-diff__pre--clamp' : ''}`} tabIndex={0} aria-label={title}>
        {text}
      </pre>
      {long && (
        <Button variant="ghost" size="sm" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
          {open ? t('audit.diff.showLess') : t('audit.diff.showMoreLines', { count: lines - RAW_COLLAPSED_LINES })}
        </Button>
      )}
    </div>
  );
}

/** What changed in an entry: added / removed / changed fields highlighted (with a marker and hidden text as well as
 * colour), unchanged ones tucked away, long documents cut off with "show more", and a raw JSON view. */
export function DiffView({ before, after }: { before: unknown; after: unknown }) {
  const { t } = useTranslation();
  const diff = useMemo(() => buildDiff(before, after), [before, after]);
  const [view, setView] = useState<'changes' | 'raw'>('changes');
  const [showUnchanged, setShowUnchanged] = useState(false);
  const [limit, setLimit] = useState(INITIAL_ROWS);

  if (diff.mode === 'none') return <p className="dam-diff__none">{t('audit.diff.none')}</p>;

  const { counts } = diff;
  const visible = diff.mode === 'diff' && !showUnchanged ? diff.rows.filter((r) => r.kind !== 'unchanged') : diff.rows;
  const shown = visible.slice(0, limit);
  const summary =
    diff.mode === 'diff'
      ? [
          counts.added > 0 && t('audit.diff.nAdded', { count: counts.added }),
          counts.changed > 0 && t('audit.diff.nChanged', { count: counts.changed }),
          counts.removed > 0 && t('audit.diff.nRemoved', { count: counts.removed }),
        ]
          .filter(Boolean)
          .join(' · ') || t('audit.diff.noChanges')
      : t('audit.diff.nFields', { count: diff.rows.length });

  return (
    <div className="dam-diff">
      <div className="dam-diff__bar">
        <p className="dam-diff__summary">{summary}</p>
        <SegmentedControl
          label={t('audit.diff.viewLabel')}
          value={view}
          onChange={(v) => setView(v as 'changes' | 'raw')}
          options={[
            { value: 'changes', label: diff.mode === 'diff' ? t('audit.diff.changes') : t('audit.diff.fields') },
            { value: 'raw', label: t('audit.diff.raw') },
          ]}
        />
      </div>

      {view === 'raw' ? (
        <div className="dam-diff__rawwrap">
          {diff.mode !== 'after' && <RawBlock title={t('audit.diff.before')} value={before} />}
          {diff.mode !== 'before' && <RawBlock title={t('audit.diff.after')} value={after} />}
        </div>
      ) : (
        <>
          {shown.length > 0 && (
            <ul className="dam-diff__list" aria-label={t('audit.diff.listLabel')}>
              {shown.map((r) => (
                <Row key={r.path} row={r} mode={diff.mode as 'diff' | 'after' | 'before'} />
              ))}
            </ul>
          )}
          <div className="dam-diff__more">
            {visible.length > limit && (
              <Button variant="ghost" size="sm" onClick={() => setLimit((l) => l + 50)}>
                {t('audit.diff.showMore', { count: visible.length - limit })}
              </Button>
            )}
            {diff.mode === 'diff' && counts.unchanged > 0 && (
              <Button variant="ghost" size="sm" aria-pressed={showUnchanged} onClick={() => setShowUnchanged((s) => !s)}>
                {showUnchanged ? t('audit.diff.hideUnchanged') : t('audit.diff.showUnchanged', { count: counts.unchanged })}
              </Button>
            )}
          </div>
        </>
      )}
    </div>
  );
}
