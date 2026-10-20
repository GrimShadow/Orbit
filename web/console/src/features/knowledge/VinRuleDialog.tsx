import { ApiError } from '@dam/sdk-js';
import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Modal, WarningCircle } from '../../ui';
import { isLimitError } from '../glossary/glossaryApi';
import { Switch } from '../webhooks/Switch';
import { PatternHelp } from './PatternHelp';
import {
  emptyRuleDraft,
  ruleDraftToInput,
  ruleToDraft,
  validateRuleDraft,
  type RuleDraft,
  type RuleErrors,
} from './knowledgeDraft';
import { errorReason, useSaveVinRule } from './knowledgeApi';
import { MAX_VIN_LENGTH, checkPattern } from './knowledgeModel';
import { VIN_RULE_LIMIT, type VinRuleDto } from './knowledgeTypes';

interface Props {
  open: boolean;
  rule: VinRuleDto | null;
  onClose: () => void;
  onSaved: (rule: VinRuleDto, created: boolean) => void;
}

/** What the pattern means, live: its problem, or how many characters it fixes - and a warning when it cannot match a 17-character
 * VIN (no trailing `*` and not 17 long). */
function usePatternFeedback(pattern: string) {
  const { t } = useTranslation();
  const trimmed = pattern.trim();
  if (trimmed === '') return { error: undefined, note: undefined, warning: undefined };
  const check = checkPattern(trimmed);
  if (!check.ok) {
    return {
      error: t(`knowledge.pattern.error.${check.problem}`, { char: check.detail, max: MAX_VIN_LENGTH }),
      note: undefined,
      warning: undefined,
    };
  }
  const note = check.open
    ? t('knowledge.pattern.matchesOpen', { count: check.positions })
    : t('knowledge.pattern.matchesExact', { count: check.positions });
  const warning =
    !check.open && check.positions !== MAX_VIN_LENGTH
      ? t('knowledge.pattern.neverMatches', { count: check.positions })
      : undefined;
  return { error: undefined, note, warning };
}

function Body({ rule, onClose, onSaved }: Omit<Props, 'open'>) {
  const { t } = useTranslation();
  const save = useSaveVinRule();
  const [draft, setDraft] = useState<RuleDraft>(() => (rule ? ruleToDraft(rule) : emptyRuleDraft()));
  const [attempted, setAttempted] = useState(false);
  const [serverError, setServerError] = useState<string | null>(null);

  const errors: RuleErrors = attempted ? validateRuleDraft(draft) : {};
  const msg = (code: string | undefined) => (code ? t(`knowledge.errors.${code}`) : undefined);
  const set = <K extends keyof RuleDraft>(key: K, value: RuleDraft[K]) => setDraft((d) => ({ ...d, [key]: value }));
  const feedback = usePatternFeedback(draft.pattern);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setAttempted(true);
    setServerError(null);
    if (Object.keys(validateRuleDraft(draft)).length > 0) return;
    save.mutate(
      { id: rule?.id, body: ruleDraftToInput(draft) },
      {
        onSuccess: (saved) => onSaved(saved, !rule),
        onError: (err) => {
          if (isLimitError(err)) setServerError(t('knowledge.rules.limitReached', { max: VIN_RULE_LIMIT.toLocaleString() }));
          else if (err instanceof ApiError && err.status === 409)
            setServerError(t('knowledge.rules.duplicate', { name: draft.name.trim() }));
          else setServerError(t('knowledge.rules.saveFailed', { reason: errorReason(err) }));
        },
      },
    );
  };

  return (
    <form className="dam-stack dam-dialogform dam-knform" onSubmit={submit} noValidate>
      <Input
        label={t('knowledge.rules.name')}
        hint={t('knowledge.rules.nameHint')}
        value={draft.name}
        autoComplete="off"
        aria-required="true"
        error={msg(errors.name)}
        onChange={(e) => set('name', e.target.value)}
      />
      <div className="dam-stack">
        <Input
          label={t('knowledge.rules.pattern')}
          value={draft.pattern}
          className="dam-knmono"
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          aria-required="true"
          placeholder="ACMLS1*"
          error={feedback.error ?? (errors.pattern === 'empty' ? t('knowledge.pattern.error.empty') : undefined)}
          onChange={(e) => set('pattern', e.target.value.replace(/\s+/g, '').toUpperCase())}
        />
        <div aria-live="polite" className="dam-knform__feedback">
          {feedback.note && <p className="dam-hint">{feedback.note}</p>}
          {feedback.warning && (
            <p className="dam-knwarn">
              <WarningCircle size={14} weight="bold" aria-hidden="true" />
              {feedback.warning}
            </p>
          )}
        </div>
        <PatternHelp />
      </div>
      <div className="dam-knform__pair">
        <Input
          label={t('knowledge.field.model')}
          hint={t('knowledge.field.modelHint')}
          value={draft.model}
          autoComplete="off"
          spellCheck={false}
          aria-required="true"
          error={msg(errors.model)}
          onChange={(e) => set('model', e.target.value)}
        />
        <Input
          label={t('knowledge.field.variant')}
          hint={t('knowledge.field.variantHint')}
          value={draft.variant}
          autoComplete="off"
          spellCheck={false}
          error={msg(errors.variant)}
          onChange={(e) => set('variant', e.target.value)}
        />
      </div>
      <fieldset className="dam-knform__years">
        <legend className="dam-label">{t('knowledge.rules.yearSource')}</legend>
        <ul className="dam-knradios">
          {(['vin', 'fixed'] as const).map((m) => (
            <li key={m}>
              <label className="dam-knradio">
                <input
                  type="radio"
                  name="year-source"
                  value={m}
                  checked={draft.yearMode === m}
                  onChange={() => set('yearMode', m)}
                />
                <span>{t(`knowledge.rules.yearMode.${m}`)}</span>
              </label>
            </li>
          ))}
        </ul>
        {draft.yearMode === 'fixed' && (
          <Input
            label={t('knowledge.rules.modelYear')}
            value={draft.modelYear}
            inputMode="numeric"
            maxLength={4}
            autoComplete="off"
            placeholder="2024"
            error={msg(errors.modelYear)}
            onChange={(e) => set('modelYear', e.target.value)}
          />
        )}
      </fieldset>
      <div className="dam-knform__pair">
        <Input
          label={t('knowledge.field.theme')}
          hint={t('knowledge.field.themeHint')}
          value={draft.theme}
          autoComplete="off"
          spellCheck={false}
          error={msg(errors.theme)}
          onChange={(e) => set('theme', e.target.value)}
        />
        <Input
          label={t('knowledge.rules.priority')}
          hint={t('knowledge.rules.priorityHint')}
          value={draft.priority}
          inputMode="numeric"
          autoComplete="off"
          error={msg(errors.priority)}
          onChange={(e) => set('priority', e.target.value)}
        />
      </div>
      <Switch
        checked={draft.enabled}
        onChange={(v) => set('enabled', v)}
        label={t(draft.enabled ? 'knowledge.rules.enabled' : 'knowledge.rules.disabled')}
        ariaLabel={t('knowledge.rules.enableThis')}
      />
      {serverError && (
        <p className="dam-error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {serverError}
        </p>
      )}
      <div className="dam-dialogform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('knowledge.cancel')}
        </Button>
        <Button type="submit" variant="primary" loading={save.isPending}>
          {t(rule ? 'knowledge.rules.save' : 'knowledge.rules.add')}
        </Button>
      </div>
    </form>
  );
}

/** Add or edit one VIN rule. The pattern is checked as it is typed. The form restarts from the rule it is opened for. */
export function VinRuleDialog({ open, rule, onClose, onSaved }: Props) {
  const { t } = useTranslation();
  return (
    <Modal
      open={open}
      title={t(rule ? 'knowledge.rules.editTitle' : 'knowledge.rules.addTitle')}
      closeLabel={t('knowledge.close')}
      onClose={onClose}
    >
      <Body rule={rule} onClose={onClose} onSaved={onSaved} />
    </Modal>
  );
}
