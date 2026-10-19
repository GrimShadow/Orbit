import { CheckCircle, XCircle } from '@phosphor-icons/react';
import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Input, Select, Skeleton } from '../../../ui';
import { useRightsCheck, type RightsCheck } from '../rightsApi';
import { problemText } from './rightsFormat';
import { useDimensionTerms, type DimensionTerms } from './useDimensionTerms';

const DEBOUNCE_MS = 300;

/** One dimension's picker: a select over the tenant's vocabulary, or free text for a tenant without one. */
function ContextField({
  label,
  anyLabel,
  hint,
  terms,
  value,
  onChange,
}: {
  label: string;
  anyLabel: string;
  hint: string;
  terms: DimensionTerms;
  value: string;
  onChange: (v: string) => void;
}) {
  if (terms.options.length > 0)
    return (
      <Select
        label={label}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        options={[{ value: '', label: anyLabel }, ...terms.options]}
      />
    );
  return (
    <Input
      label={label}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={anyLabel}
      hint={hint}
      autoComplete="off"
      autoCapitalize="none"
      spellCheck={false}
      disabled={terms.loading}
    />
  );
}

function Scope({ label, codes, none, terms }: { label: string; codes: string[]; none: boolean; terms: DimensionTerms }) {
  const { t } = useTranslation();
  return (
    <div className="dam-facts__row">
      <dt>{label}</dt>
      <dd>
        {none ? (
          <Badge tone="danger" size="sm">
            {t('rightsAsset.check.none')}
          </Badge>
        ) : codes.length === 0 ? (
          t('rightsAsset.check.all')
        ) : (
          <ul className="dam-chips" aria-label={label}>
            {codes.map((c) => (
              <li key={c}>
                <Badge tone="brand" size="sm">
                  {terms.labelOf(c)}
                </Badge>
              </li>
            ))}
          </ul>
        )}
      </dd>
    </div>
  );
}

function Result({
  result,
  channel,
  region,
  channels,
  regions,
}: {
  result: RightsCheck;
  channel: string;
  region: string;
  channels: DimensionTerms;
  regions: DimensionTerms;
}) {
  const { t } = useTranslation();
  const context =
    channel || region
      ? t('rightsAsset.check.context', {
          what: [channel && channels.labelOf(channel), region && regions.labelOf(region)].filter(Boolean).join(' · '),
        })
      : t('rightsAsset.check.contextDatesOnly');
  const Icon = result.allowed ? CheckCircle : XCircle;
  return (
    <div className={`dam-rights-result dam-rights-result--${result.allowed ? 'allowed' : 'denied'}`}>
      <p className="dam-rights-result__verdict">
        <Icon size={20} weight="fill" aria-hidden="true" />
        <strong>{result.allowed ? t('rightsAsset.check.allowed') : t('rightsAsset.check.notAllowed')}</strong>
      </p>
      <p className="dam-rights-result__context">{context}</p>
      {result.reasons.length > 0 && (
        <ul className="dam-rights-result__reasons">
          {result.reasons.map((r) => (
            <li key={r}>{r}</li>
          ))}
        </ul>
      )}
      {result.watermarkRequired && <p className="dam-rights-result__note">{t('rightsAsset.check.watermark')}</p>}
      <dl className="dam-facts">
        <Scope
          label={t('rightsAsset.check.availableChannels')}
          codes={result.effectiveChannels}
          none={result.noChannelsAllowed}
          terms={channels}
        />
        <Scope
          label={t('rightsAsset.check.availableRegions')}
          codes={result.effectiveRegions}
          none={result.noRegionsAllowed}
          terms={regions}
        />
      </dl>
    </div>
  );
}

/** "Can this asset be used on <channel> in <region> today?" - nothing is requested until the button is pressed; after that,
 * changing a field re-checks on its own. The reasons are the server's own sentences. */
export function AvailabilityCheck({ assetId, assetType }: { assetId: string; assetType: string }) {
  const { t } = useTranslation();
  const channels = useDimensionTerms(assetType, 'channel');
  const regions = useDimensionTerms(assetType, 'region');
  const [channel, setChannel] = useState('');
  const [region, setRegion] = useState('');
  const [asked, setAsked] = useState<{ channel: string; region: string } | null>(null);
  const check = useRightsCheck(assetId, asked?.channel ?? '', asked?.region ?? '', asked !== null);

  // After the first check a changed field re-checks by itself; typing waits a moment so each keystroke is not a request.
  useEffect(() => {
    if (!asked) return;
    const next = { channel: channel.trim(), region: region.trim() };
    if (next.channel === asked.channel && next.region === asked.region) return;
    const timer = setTimeout(() => setAsked(next), DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [channel, region, asked]);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const next = { channel: channel.trim(), region: region.trim() };
    if (asked && asked.channel === next.channel && asked.region === next.region) void check.refetch();
    else setAsked(next);
  };

  return (
    <section className="dam-rights-check" aria-labelledby="dam-rights-check-title">
      <h3 id="dam-rights-check-title" className="dam-rights__subtitle">
        {t('rightsAsset.check.title')}
      </h3>
      <p className="dam-hint">{t('rightsAsset.check.intro')}</p>
      <form className="dam-rights-check__form" onSubmit={submit}>
        <ContextField
          label={t('rightsAsset.check.channel')}
          anyLabel={t('rightsAsset.check.anyChannel')}
          hint={t('rightsAsset.check.channelHint')}
          terms={channels}
          value={channel}
          onChange={setChannel}
        />
        <ContextField
          label={t('rightsAsset.check.region')}
          anyLabel={t('rightsAsset.check.anyRegion')}
          hint={t('rightsAsset.check.regionHint')}
          terms={regions}
          value={region}
          onChange={setRegion}
        />
        <Button type="submit" variant="secondary" loading={check.isFetching && asked !== null && !check.data}>
          {t('rightsAsset.check.button')}
        </Button>
      </form>
      <div aria-live="polite" className="dam-rights-check__out">
        {asked !== null &&
          (check.isPending ? (
            <div role="status" aria-label={t('rightsAsset.check.checking')}>
              <Skeleton height={64} />
            </div>
          ) : check.error ? (
            <p className="dam-error" role="alert">
              {t('rightsAsset.check.failed')} {problemText(check.error)}
            </p>
          ) : check.data ? (
            <Result result={check.data} channel={asked.channel} region={asked.region} channels={channels} regions={regions} />
          ) : null)}
      </div>
    </section>
  );
}
