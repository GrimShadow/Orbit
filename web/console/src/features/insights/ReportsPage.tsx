import { DownloadSimple, FileText, Lock, Warning } from '@phosphor-icons/react';
import { useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Button, EmptyState, PageHeader, Select, Skeleton, useToast } from '@dam/ui-kit';
import { useMe } from '../../api/me';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { InsightsPeriodPicker } from './InsightsPeriodPicker';
import { InsightsTabs } from './InsightsTabs';
import { useDownloadReportCsv, useReport, useReportList } from './insightsApi';
import { isForbidden } from '../analytics/analyticsApi';
import { formatDay } from './kpiFormat';
import { ReportPreview } from './ReportPreview';
import { SchedulesSection } from './SchedulesSection';
import { reportFileName, saveBlob } from './download';
import { useInsightsPeriod } from './useInsightsPeriod';
import './insights.css';

/** `/insights/reports` - run a report for a window, read it as a table, download it as CSV; people who manage reports also
 * schedule them by email. */
export function ReportsPage() {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  useDocumentTitle(t('insights.reports.pageTitle'));
  const me = useMe();
  const [days, setDays] = useInsightsPeriod();
  const [params, setParams] = useSearchParams();
  const list = useReportList();
  const reports = list.data ?? [];
  const chosen = reports.find((r) => r.name === params.get('report')) ?? reports[0];
  // A report may cap the period (unused assets go to 3650 days, the rest to 365); the picker itself stops at 365.
  const reportDays = Math.min(days, chosen?.maxDays ?? days);
  const report = useReport(chosen?.name, reportDays);
  const download = useDownloadReportCsv();
  const canManage = !!me.data?.permissions.includes('analytics.manage');

  const choose = (name: string) =>
    setParams(
      (prev) => {
        const p = new URLSearchParams(prev);
        p.set('report', name);
        return p;
      },
      { replace: true },
    );

  const downloadCsv = () => {
    if (!chosen) return;
    download.mutate(
      { name: chosen.name, days: reportDays },
      {
        onSuccess: (blob) => {
          const name = reportFileName(chosen.name);
          saveBlob(name, blob);
          push({ title: t('insights.reports.downloaded', { file: name }), tone: 'success' });
        },
        onError: () => push({ title: t('insights.reports.downloadFailed'), tone: 'error' }),
      },
    );
  };

  let body;
  if (isForbidden(list.error) || isForbidden(report.error)) {
    body = <EmptyState icon={<Lock />} title={t('insights.noAccessTitle')} description={t('insights.noAccessBody')} />;
  } else if (list.error) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<Warning />}
          title={t('insights.reports.listError')}
          description={t('insights.reports.errorHint')}
          action={<Button onClick={() => void list.refetch()}>{t('insights.retry')}</Button>}
        />
      </div>
    );
  } else if (list.isPending) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('insights.reports.loading')}</span>
        <div className="dam-stack" aria-hidden="true">
          <Skeleton height={40} />
          <Skeleton height={220} />
        </div>
      </div>
    );
  } else if (reports.length === 0) {
    body = (
      <EmptyState icon={<FileText />} title={t('insights.reports.noReports')} description={t('insights.reports.noReportsBody')} />
    );
  } else {
    const r = report.data;
    body = (
      <>
        <div className="dam-ins__reportbar">
          <Select
            label={t('insights.reports.report')}
            value={chosen!.name}
            options={reports.map((x) => ({ value: x.name, label: x.title }))}
            hint={chosen!.description ?? undefined}
            onChange={(e) => choose(e.target.value)}
          />
          <Button variant="secondary" loading={download.isPending} onClick={downloadCsv}>
            <DownloadSimple size={16} weight="bold" aria-hidden="true" />
            {t('insights.reports.downloadCsv')}
          </Button>
        </div>
        <div aria-live="polite" aria-busy={report.isFetching}>
          {report.error ? (
            <div role="alert">
              <EmptyState
                titleAs="h2"
                icon={<Warning />}
                title={t('insights.reports.error')}
                description={t('insights.reports.errorHint')}
                action={<Button onClick={() => void report.refetch()}>{t('insights.retry')}</Button>}
              />
            </div>
          ) : !r ? (
            <div role="status">
              <span className="dam-sr-only">{t('insights.reports.loadingReport')}</span>
              <div className="dam-stack" aria-hidden="true">
                <Skeleton height={32} />
                <Skeleton height={180} />
              </div>
            </div>
          ) : (
            <>
              <p className="dam-ins__period">
                {t('insights.reports.periodLine', {
                  title: r.title,
                  from: formatDay(r.period.from, i18n.language),
                  to: formatDay(r.period.to, i18n.language),
                })}
              </p>
              <ReportPreview report={r} />
            </>
          )}
        </div>
      </>
    );
  }

  return (
    <div className="dam-page dam-ins">
      <PageHeader
        eyebrow={t('insights.title')}
        title={t('insights.reports.pageTitle')}
        description={t('insights.reports.description')}
      />
      <InsightsTabs />
      <div className="dam-ins__toolbar">
        <InsightsPeriodPicker days={days} onChange={setDays} />
      </div>
      {body}
      {canManage && <SchedulesSection />}
    </div>
  );
}
