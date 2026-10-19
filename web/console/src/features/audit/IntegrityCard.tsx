import { CheckCircle, ShieldCheck, ShieldWarning, WarningCircle } from '@phosphor-icons/react';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Card } from '../../ui';
import { ApiError } from '../../api/client';
import { relativeTime } from '../../lib/relativeTime';
import { useVerifyAudit, type AuditVerification } from './auditApi';
import { defaultVerifyRange, type AuditFilters } from './auditFilters';
import { reasonKey, shortHash } from './auditFormat';
import { CopyButton } from './CopyButton';

const STORE_KEY = 'dam.audit.lastVerify';

interface Stored {
  result: AuditVerification;
  at: string;
}

function load(): Stored | null {
  try {
    const raw = window.sessionStorage.getItem(STORE_KEY);
    return raw ? (JSON.parse(raw) as Stored) : null;
  } catch {
    return null;
  }
}

function save(value: Stored) {
  try {
    window.sessionStorage.setItem(STORE_KEY, JSON.stringify(value));
  } catch {
    /* private mode: the result still shows for this visit */
  }
}

/** "Chain integrity": recomputes the hash chain over the filters' date range on demand. It is O(n) on the server, so it
 * never runs by itself; the last result (kept for this browser session) stays on screen with the time it was taken. */
export function IntegrityCard({
  filters,
  onShowEntry,
}: {
  filters: AuditFilters;
  /** Open the entry a failed check stopped at. */
  onShowEntry: (chainNo: number) => void;
}) {
  const { t, i18n } = useTranslation();
  const verify = useVerifyAudit();
  const [last, setLast] = useState<Stored | null>(load);
  const lang = i18n.language;
  const fmt = (iso: string) => new Date(iso).toLocaleString(lang, { dateStyle: 'medium', timeStyle: 'short' });

  const range = defaultVerifyRange(filters, new Date());
  const rangeText = (from: string, to: string | null | undefined) =>
    to ? t('audit.verify.rangeBetween', { from: fmt(from), to: fmt(to) }) : t('audit.verify.rangeSince', { from: fmt(from) });
  const scope = filters.from ? rangeText(range.from, range.to) : t('audit.verify.rangeDefault');

  const run = () => {
    const sent = defaultVerifyRange(filters, new Date());
    verify.mutate(sent, {
      onSuccess: (result) => {
        const stored = { result, at: new Date().toISOString() };
        setLast(stored);
        save(stored);
      },
    });
  };

  let banner: ReactNode = null;
  if (verify.isPending) {
    banner = (
      <div className="dam-verify dam-verify--running" role="status">
        <span className="dam-spinner" aria-hidden="true" />
        <div>
          <strong>{t('audit.verify.running')}</strong>
          <p>{t('audit.verify.runningBody')}</p>
        </div>
      </div>
    );
  } else if (verify.isError) {
    const forbidden = verify.error instanceof ApiError && verify.error.status === 403;
    banner = (
      <div className="dam-verify dam-verify--error" role="alert">
        <WarningCircle size={22} weight="fill" aria-hidden="true" />
        <div>
          <strong>{t('audit.verify.failedToRun')}</strong>
          <p>{forbidden ? t('audit.verify.forbidden') : t('audit.verify.failedToRunBody')}</p>
        </div>
        <Button variant="secondary" size="sm" onClick={run}>
          {t('audit.retry')}
        </Button>
      </div>
    );
  } else if (last) {
    const { result: r, at } = last;
    const meta = (
      <p className="dam-verify__meta">
        {r.from ? rangeText(r.from, r.to) : t('audit.verify.rangeAll')}
        {' · '}
        <time dateTime={at} title={new Date(at).toLocaleString(lang)}>
          {t('audit.verify.checkedAt', { when: relativeTime(at, lang) })}
        </time>
      </p>
    );
    if (r.verified && r.checked === 0) {
      banner = (
        <div className="dam-verify dam-verify--neutral" role="status">
          <ShieldCheck size={22} weight="fill" aria-hidden="true" />
          <div>
            <strong>{t('audit.verify.emptyTitle')}</strong>
            <p>{t('audit.verify.emptyBody')}</p>
            {meta}
          </div>
        </div>
      );
    } else if (r.verified) {
      banner = (
        <div className="dam-verify dam-verify--ok" role="status">
          <CheckCircle size={22} weight="fill" aria-hidden="true" />
          <div>
            <strong>{t('audit.verify.okTitle')}</strong>
            <p>
              {t('audit.verify.okSummary', { count: r.checked, formattedCount: new Intl.NumberFormat(lang).format(r.checked) })}{' '}
              {r.lastHash && (
                <span className="dam-auditmono">
                  <code className="dam-auditmono__text" title={r.lastHash}>
                    {shortHash(r.lastHash)}
                  </code>
                  <CopyButton value={r.lastHash} label={t('audit.verify.copyLastHash')} />
                </span>
              )}
            </p>
            {meta}
          </div>
        </div>
      );
    } else {
      banner = (
        <div className="dam-verify dam-verify--broken" role="alert">
          <ShieldWarning size={22} weight="fill" aria-hidden="true" />
          <div>
            <strong>{t('audit.verify.brokenTitle', { chainNo: r.brokenAtChainNo ?? '?' })}</strong>
            <p>{t(`audit.verify.reason.${reasonKey(r.reason)}`)}</p>
            <p className="dam-verify__hint">{t('audit.verify.brokenHint')}</p>
            {meta}
          </div>
          {r.brokenAtChainNo != null && (
            <Button variant="secondary" size="sm" onClick={() => onShowEntry(r.brokenAtChainNo as number)}>
              {t('audit.verify.showEntry')}
            </Button>
          )}
        </div>
      );
    }
  }

  return (
    <Card as="section" aria-labelledby="audit-integrity-title" className="dam-integrity">
      <div className="dam-integrity__head">
        <div className="dam-integrity__text">
          <h2 id="audit-integrity-title" className="dam-integrity__title">
            {t('audit.verify.title')}
          </h2>
          <p className="dam-integrity__desc">{t('audit.verify.desc')}</p>
          <p className="dam-integrity__scope">{t('audit.verify.scope', { range: scope })}</p>
        </div>
        <Button variant="primary" loading={verify.isPending} onClick={run}>
          {!verify.isPending && <ShieldCheck size={16} weight="bold" aria-hidden="true" />}
          {verify.isPending ? t('audit.verify.verifying') : t('audit.verify.button')}
        </Button>
      </div>
      {banner ? banner : <p className="dam-integrity__idle">{t('audit.verify.idle')}</p>}
    </Card>
  );
}
