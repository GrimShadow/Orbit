import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { DistributionBar, Table, type Column } from '@dam/ui-kit';
import type { ReportDto } from './insightsTypes';
import { cellOf, formatCell, isNumericColumn, storageSegments } from './reportFormat';
import './insights.css';

/** Rows shown in the table; the CSV holds them all. Keeps a very long report from putting thousands of rows in the page. */
export const PREVIEW_ROWS = 100;

/** A report as a responsive table (it scrolls sideways inside its own wrapper on a phone, never the page), formatted by each
 * column's type. The `storage` report also gets a compact bar view of its folders above the table. */
export function ReportPreview({ report }: { report: ReportDto }) {
  const { t, i18n } = useTranslation();
  const shown = report.rows.slice(0, PREVIEW_ROWS);
  const segments = useMemo(() => (report.name === 'storage' ? storageSegments(report) : []), [report]);

  const columns: Column<number>[] = report.columns.map((c) => ({
    key: c.key,
    header: c.label,
    className: isNumericColumn(c) ? 'dam-ins__num' : undefined,
    render: (i) => formatCell(cellOf(shown[i]!, c), c.type, t, i18n.language),
  }));

  const summary = segments
    .slice(0, 3)
    .map((s) => `${s.label} ${s.display}`)
    .join(', ');

  return (
    <div className="dam-stack">
      {segments.length > 0 && (
        <div className="dam-ins__storagebar">
          <h2 className="dam-ins__h3">{t('insights.reports.storageBars')}</h2>
          <DistributionBar
            label={t('insights.reports.storageBarsLabel', { count: segments.length, summary })}
            segments={segments}
          />
          <p className="dam-hint">{t('insights.reports.storageBarsNote', { count: segments.length })}</p>
        </div>
      )}
      <Table<number>
        caption={t('insights.reports.tableCaption', { report: report.title })}
        columns={columns}
        rows={shown.map((_, i) => i)}
        getRowId={(i) => String(i)}
        emptyMessage={t('insights.reports.noRows')}
      />
      {report.truncated && (
        <p className="dam-hint" role="status">
          {t('insights.reports.capped')}
        </p>
      )}
      {report.rows.length > PREVIEW_ROWS && (
        <p className="dam-hint" role="status">
          {t('insights.reports.truncated', {
            shown: new Intl.NumberFormat(i18n.language).format(PREVIEW_ROWS),
            total: new Intl.NumberFormat(i18n.language).format(report.rows.length),
          })}
        </p>
      )}
    </div>
  );
}
