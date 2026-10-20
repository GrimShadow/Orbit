import { MagnifyingGlass, WarningCircle } from '@phosphor-icons/react';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Badge, Button, Card, Input, PageHeader, Select, Skeleton } from '../../ui';
import { StatusBadge } from '../asset/StatusBadge';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { languageLabel, useEnabledLanguages } from '../translation/languages';
import { ChapterTree } from './ChapterTree';
import { KnowledgeTabs } from './KnowledgeTabs';
import { VehicleFacts } from './VehicleFacts';
import { classifyResolveError, useResolve, type ResolveParams } from './knowledgeApi';
import { MAX_VIN_LENGTH, assetHref, normalizeVin, vinProblem } from './knowledgeModel';
import { DOC_TYPES, type DocType, type ResolveDto } from './knowledgeTypes';
import './knowledge.css';

function Result({ r }: { r: ResolveDto }) {
  const { t, i18n } = useTranslation();
  const d = r.document;
  return (
    <div className="dam-stack">
      <Card as="section" padding="md" aria-labelledby="dam-knres-vehicle">
        <h2 id="dam-knres-vehicle" className="dam-knh2">
          {t('knowledge.result.vehicle')}
        </h2>
        <VehicleFacts vehicle={r.vehicle} />
        <p className="dam-hint dam-knres__rule">{t('knowledge.result.rule', { name: r.rule.name, priority: r.rule.priority })}</p>
      </Card>

      <Card as="section" padding="md" aria-labelledby="dam-knres-doc">
        <h2 id="dam-knres-doc" className="dam-knh2">
          {t('knowledge.result.document')}
        </h2>
        <p className="dam-knres__doctitle">
          <Link className="dam-knlink" to={assetHref(d.assetId)}>
            {d.title}
          </Link>
        </p>
        <dl className="dam-facts">
          <div className="dam-facts__row">
            <dt>{t('knowledge.field.docType')}</dt>
            <dd>{t(`knowledge.docType.${d.docType}`, { defaultValue: d.docType })}</dd>
          </div>
          <div className="dam-facts__row">
            <dt>{t('knowledge.col.status')}</dt>
            <dd>
              <StatusBadge status={d.assetStatus} />
            </dd>
          </div>
          <div className="dam-facts__row">
            <dt>{t('knowledge.result.version')}</dt>
            <dd>{d.versionNumber != null ? t('knowledge.result.versionNo', { n: d.versionNumber }) : t('knowledge.none')}</dd>
          </div>
          <div className="dam-facts__row">
            <dt>{t('knowledge.col.language')}</dt>
            <dd>{languageLabel(d.language, i18n.language)}</dd>
          </div>
        </dl>
      </Card>

      <Card as="section" padding="md" aria-labelledby="dam-knres-chapters">
        <h2 id="dam-knres-chapters" className="dam-knh2">
          {t('knowledge.result.chapters', { count: d.chapters.length })}
        </h2>
        {d.chapters.length === 0 ? (
          <p className="dam-hint">{t('knowledge.result.noChapters')}</p>
        ) : (
          <ChapterTree chapters={d.chapters} assetId={d.assetId} label={t('knowledge.result.chaptersLabel')} />
        )}
      </Card>

      {r.otherLanguages.length > 0 && (
        <Card as="section" padding="md" aria-labelledby="dam-knres-langs">
          <h2 id="dam-knres-langs" className="dam-knh2">
            {t('knowledge.result.otherLanguages')}
          </h2>
          <ul className="dam-chips" aria-label={t('knowledge.result.otherLanguages')}>
            {r.otherLanguages.map((o) => (
              <li key={o.documentId}>
                <Link className="dam-knlang" to={assetHref(o.assetId)}>
                  <Badge tone="brand">{languageLabel(o.language, i18n.language)}</Badge>
                </Link>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}

/** `/knowledge/resolver`: type a VIN and see what an app would be shown for it - the rule that matched, the vehicle it names and
 * the document with its chapters - as the signed-in user, so status and access rules apply exactly as they do to an app. For
 * support staff and administrators. */
export function ResolverPage() {
  const { t, i18n } = useTranslation();
  useDocumentTitle(t('knowledge.resolver.title'));
  const languages = useEnabledLanguages();
  const [vin, setVin] = useState('');
  const [type, setType] = useState<DocType>('owner_manual');
  const [lang, setLang] = useState('');
  const [touched, setTouched] = useState(false);
  const [params, setParams] = useState<ResolveParams | null>(null);
  const query = useResolve(params);

  const problem = vinProblem(vin);
  // Wrong characters are flagged while typing; the length once the field is left or a lookup is tried.
  const shown = vin === '' ? (touched ? 'empty' : null) : problem === 'length' ? (touched ? problem : null) : problem;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setTouched(true);
    if (vin === '' || problem) return;
    // The same question again asks the server again (the answer may have changed).
    if (params && params.vin === vin && params.type === type && params.lang === lang) void query.refetch();
    else setParams({ vin, type, lang });
  };

  const failure = query.error ? classifyResolveError(query.error) : null;
  let result;
  if (!params) result = null;
  else if (query.isFetching && !query.data) {
    result = (
      <div role="status">
        <span className="dam-sr-only">{t('knowledge.resolver.loading')}</span>
        <div className="dam-stack" aria-hidden="true">
          <Skeleton height={120} />
          <Skeleton height={160} />
        </div>
      </div>
    );
  } else if (failure) {
    result = (
      <div role="alert" className="dam-knmsg">
        <WarningCircle size={20} weight="bold" aria-hidden="true" />
        <div>
          <p className="dam-knmsg__title">{t(`knowledge.resolver.${failure}.title`, { vin: params.vin })}</p>
          <p className="dam-hint">{t(`knowledge.resolver.${failure}.hint`)}</p>
          {failure === 'failed' && (
            <Button size="sm" variant="secondary" onClick={() => void query.refetch()}>
              {t('knowledge.retry')}
            </Button>
          )}
        </div>
      </div>
    );
  } else if (query.data) result = <Result r={query.data} />;

  return (
    <div className="dam-page dam-kn">
      <PageHeader
        eyebrow={t('knowledge.title')}
        title={t('knowledge.resolver.title')}
        description={t('knowledge.resolver.description')}
      />
      <KnowledgeTabs />
      <form className="dam-knresolve" onSubmit={submit} noValidate role="search" aria-label={t('knowledge.resolver.title')}>
        <Input
          label={t('knowledge.vin.label')}
          value={vin}
          className="dam-knmono"
          maxLength={MAX_VIN_LENGTH + 8}
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          placeholder="ACMLS1AB1N0123456"
          hint={t('knowledge.vin.hint')}
          aria-required="true"
          error={shown ? t(`knowledge.vin.problem.${shown}`, { count: vin.length, max: MAX_VIN_LENGTH }) : undefined}
          onChange={(e) => setVin(normalizeVin(e.target.value))}
          onBlur={() => vin !== '' && setTouched(true)}
        />
        <Select
          label={t('knowledge.field.docType')}
          value={type}
          options={DOC_TYPES.map((d) => ({ value: d, label: t(`knowledge.docType.${d}`) }))}
          onChange={(e) => setType(e.target.value as DocType)}
        />
        <Select
          label={t('knowledge.resolver.language')}
          value={lang}
          options={[
            { value: '', label: t('knowledge.resolver.defaultLanguage') },
            ...languages.map((l) => ({ value: l, label: languageLabel(l, i18n.language) })),
          ]}
          onChange={(e) => setLang(e.target.value)}
        />
        <Button type="submit" variant="primary" loading={!!params && query.isFetching}>
          <MagnifyingGlass size={16} weight="bold" aria-hidden="true" />
          {t('knowledge.resolver.run')}
        </Button>
      </form>
      <div aria-live="polite" aria-busy={query.isFetching} className="dam-knres">
        {result}
      </div>
    </div>
  );
}
