import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Input } from '../../ui';
import {
  fromLocalInput,
  isGuid,
  presetRange,
  rangeIsReversed,
  toLocalInput,
  type AuditFilters,
  type FilterKey,
  type Preset,
} from './auditFilters';

/** The entity types the platform audits. Suggestions only: any value can be typed. */
export const ENTITY_TYPES = [
  'access_rule',
  'asset',
  'collection',
  'folder',
  'metadata_schema',
  'notification_channel',
  'term',
  'tenant',
  'upload',
  'user',
  'webhook',
  'workflow',
  'workflow_binding',
  'workflow_task',
];

const PRESETS: Preset[] = ['hour', 'day', 'week', 'month'];
const DEBOUNCE_MS = 400;

type TextKey = 'actor' | 'action' | 'entityType' | 'entityId' | 'q';
type Draft = Record<TextKey, string>;
const draftOf = (f: AuditFilters): Draft => ({
  actor: f.actor ?? '',
  action: f.action ?? '',
  entityType: f.entityType ?? '',
  entityId: f.entityId ?? '',
  q: f.q ?? '',
});

/** The filter controls. Dates apply as soon as they are valid; text fields apply after a short pause (or on Enter).
 * The URL is the source of truth: the drafts re-sync whenever it changes (chips, Clear, back/forward). */
export function AuditFiltersForm({
  filters,
  update,
  actions,
  actors,
}: {
  filters: AuditFilters;
  update: (patch: Partial<Record<FilterKey, string | undefined>>, opts?: { replace?: boolean }) => void;
  /** Suggestions for the action field (from `/audit/actions`). */
  actions: string[];
  /** Actors seen in the loaded rows: id -> name, offered as suggestions for the actor field. */
  actors: Map<string, string>;
}) {
  const { t } = useTranslation();
  const uid = useId();
  const [draft, setDraft] = useState<Draft>(() => draftOf(filters));
  const key = JSON.stringify(draftOf(filters));
  const lastKey = useRef(key);

  // The URL changed from somewhere else (a chip, Clear, back/forward, a click-through): adopt the fields that changed
  // and leave the ones the person may be typing into alone.
  useEffect(() => {
    if (key === lastKey.current) return;
    const prev = JSON.parse(lastKey.current) as Draft;
    const next = JSON.parse(key) as Draft;
    lastKey.current = key;
    setDraft((d) => {
      const merged = { ...d };
      for (const k of Object.keys(next) as TextKey[]) if (next[k] !== prev[k]) merged[k] = next[k];
      return merged;
    });
  }, [key]);

  const entityIdInvalid = draft.entityId.trim() !== '' && !isGuid(draft.entityId);

  const commit = (next: Draft, replace = true) => {
    const patch: Partial<Record<FilterKey, string | undefined>> = {};
    const expected: Draft = { ...draftOf(filters) };
    for (const k of Object.keys(next) as TextKey[]) {
      // A half-typed entity id is held back until it is a valid UUID.
      if (k === 'entityId' && next.entityId.trim() !== '' && !isGuid(next.entityId)) continue;
      patch[k] = next[k].trim() || undefined;
      expected[k] = k === 'entityId' ? next[k].trim().toLowerCase() : next[k].trim();
    }
    // What the URL will say once this lands, so the sync effect below does not mistake our own write for a foreign one.
    lastKey.current = JSON.stringify(expected);
    update(patch, { replace });
  };

  // Debounced apply of the text drafts.
  useEffect(() => {
    if (JSON.stringify(draft) === key) return;
    const id = setTimeout(() => commit(draft), DEBOUNCE_MS);
    return () => clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft]);

  const set = (k: TextKey) => (e: React.ChangeEvent<HTMLInputElement>) => setDraft((d) => ({ ...d, [k]: e.target.value }));
  const reversed = rangeIsReversed(filters);
  const actorOptions = [...actors.entries()];

  return (
    <form
      className="dam-auditfilters"
      role="search"
      aria-label={t('audit.filters.label')}
      onSubmit={(e) => {
        e.preventDefault();
        commit(draft, false);
      }}
    >
      <div className="dam-auditfilters__grid">
        <Input
          type="datetime-local"
          label={t('audit.filters.from')}
          value={toLocalInput(filters.from)}
          onChange={(e) => update({ from: fromLocalInput(e.target.value, 'start') })}
        />
        <Input
          type="datetime-local"
          label={t('audit.filters.to')}
          value={toLocalInput(filters.to)}
          error={reversed ? t('audit.filters.reversed') : undefined}
          onChange={(e) => update({ to: fromLocalInput(e.target.value, 'end') })}
        />
        <Input
          label={t('audit.filters.action')}
          hint={t('audit.filters.actionHint')}
          value={draft.action}
          onChange={set('action')}
          list={`${uid}-actions`}
          autoComplete="off"
          spellCheck={false}
          placeholder="asset."
        />
        <Input
          label={t('audit.filters.actor')}
          hint={t('audit.filters.actorHint')}
          value={draft.actor}
          onChange={set('actor')}
          list={`${uid}-actors`}
          autoComplete="off"
          spellCheck={false}
        />
        <Input
          label={t('audit.filters.entityType')}
          value={draft.entityType}
          onChange={set('entityType')}
          list={`${uid}-types`}
          autoComplete="off"
          spellCheck={false}
          placeholder="asset"
        />
        <Input
          label={t('audit.filters.entityId')}
          value={draft.entityId}
          onChange={set('entityId')}
          error={entityIdInvalid ? t('audit.filters.entityIdInvalid') : undefined}
          autoComplete="off"
          spellCheck={false}
          placeholder="UUID"
        />
        <div className="dam-auditfilters__q">
          <Input
            type="search"
            label={t('audit.filters.q')}
            hint={t('audit.filters.qHint')}
            value={draft.q}
            onChange={set('q')}
            autoComplete="off"
          />
        </div>
      </div>

      <datalist id={`${uid}-actions`}>
        {actions.map((a) => (
          <option key={a} value={a} />
        ))}
      </datalist>
      <datalist id={`${uid}-types`}>
        {ENTITY_TYPES.map((x) => (
          <option key={x} value={x} />
        ))}
      </datalist>
      <datalist id={`${uid}-actors`}>
        {actorOptions.map(([id, name]) => (
          <option key={id} value={id} label={name} />
        ))}
      </datalist>

      <div className="dam-auditfilters__presets" role="group" aria-label={t('audit.filters.presets')}>
        <span className="dam-auditfilters__presetlabel" aria-hidden="true">
          {t('audit.filters.quick')}
        </span>
        {PRESETS.map((p) => (
          <Button key={p} variant="secondary" size="sm" onClick={() => update(presetRange(p, new Date()))}>
            {t(`audit.filters.preset.${p}`)}
          </Button>
        ))}
        <button type="submit" className="dam-sr-only">
          {t('audit.filters.apply')}
        </button>
      </div>
    </form>
  );
}
