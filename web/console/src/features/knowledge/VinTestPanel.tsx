import { useId, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Card, Input, Table, WarningCircle } from '../../ui';
import { errorReason, useTestVin } from './knowledgeApi';
import { MAX_VIN_LENGTH, normalizeVin, vinProblem } from './knowledgeModel';
import type { VinTestCandidate } from './knowledgeTypes';
import { VehicleFacts } from './VehicleFacts';

/** "Test a VIN": which rule the tenant's rules pick for it, and why every other rule matched or not - so a pattern can be debugged
 * without publishing anything. Needs `knowledge.manage`. */
export function VinTestPanel() {
  const { t } = useTranslation();
  const titleId = useId();
  const test = useTestVin();
  const [vin, setVin] = useState('');
  const [attempted, setAttempted] = useState(false);
  const problem = vinProblem(vin);
  // Wrong characters are shown as they are typed; the length only once the person has tried.
  const shown = problem === 'length' ? (attempted ? problem : null) : problem;
  const result = test.data;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setAttempted(true);
    if (problem) return;
    test.mutate(vin);
  };

  return (
    <Card as="section" padding="md" aria-labelledby={titleId} className="dam-kntest">
      <h2 id={titleId} className="dam-knh2">
        {t('knowledge.test.title')}
      </h2>
      <p className="dam-hint">{t('knowledge.test.hint')}</p>
      <form className="dam-kntest__form" onSubmit={submit} noValidate>
        <Input
          label={t('knowledge.vin.label')}
          value={vin}
          className="dam-knmono"
          maxLength={MAX_VIN_LENGTH + 8}
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          placeholder="ACMLS1AB1N0123456"
          error={shown ? t(`knowledge.vin.problem.${shown}`, { count: vin.length, max: MAX_VIN_LENGTH }) : undefined}
          onChange={(e) => {
            setVin(normalizeVin(e.target.value));
            test.reset();
          }}
        />
        <Button type="submit" variant="secondary" loading={test.isPending}>
          {t('knowledge.test.run')}
        </Button>
      </form>
      <div aria-live="polite" aria-busy={test.isPending} className="dam-kntest__result">
        {test.error ? (
          <p className="dam-error" role="alert">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {t('knowledge.test.failed', { reason: errorReason(test.error) })}
          </p>
        ) : result ? (
          !result.valid ? (
            <p className="dam-error">
              <WarningCircle size={14} weight="bold" aria-hidden="true" />
              {t('knowledge.test.invalid', { vin: result.vin })}
            </p>
          ) : (
            <div className="dam-stack">
              {result.chosen ? (
                <>
                  <p className="dam-kntest__verdict">{t('knowledge.test.chosen', { name: result.chosen.name })}</p>
                  {result.vehicle && <VehicleFacts vehicle={result.vehicle} />}
                </>
              ) : (
                <p className="dam-kntest__verdict">{t('knowledge.test.noRule')}</p>
              )}
              <Table<VinTestCandidate>
                caption={t('knowledge.test.candidates')}
                rows={result.candidates}
                getRowId={(c) => c.ruleId}
                emptyMessage={t('knowledge.test.noCandidates')}
                columns={[
                  {
                    key: 'name',
                    header: t('knowledge.rules.name'),
                    render: (c) => (
                      <span>
                        {c.name}
                        {result.chosen?.ruleId === c.ruleId && (
                          <Badge size="sm" tone="success" className="dam-knchosen">
                            {t('knowledge.test.winner')}
                          </Badge>
                        )}
                      </span>
                    ),
                  },
                  { key: 'priority', header: t('knowledge.rules.priority'), className: 'dam-knnum', render: (c) => c.priority },
                  {
                    key: 'specificity',
                    header: t('knowledge.test.specificity'),
                    className: 'dam-knnum',
                    render: (c) => c.specificity,
                  },
                  {
                    key: 'matched',
                    header: t('knowledge.test.matched'),
                    render: (c) => (
                      <Badge size="sm" tone={c.matched ? 'success' : 'neutral'}>
                        {t(c.matched ? 'knowledge.test.yes' : 'knowledge.test.no')}
                      </Badge>
                    ),
                  },
                  { key: 'reason', header: t('knowledge.test.reason'), render: (c) => c.reason },
                ]}
              />
            </div>
          )
        ) : null}
      </div>
    </Card>
  );
}
