import { Lock, ListMagnifyingGlass } from '@phosphor-icons/react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMe } from '../../api/me';
import {
  Badge,
  Button,
  EmptyState,
  Input,
  Modal,
  PageHeader,
  PencilSimple,
  Plus,
  Skeleton,
  Table,
  Trash,
  WarningCircle,
  useToast,
} from '../../ui';
import { useDocumentTitle } from '../library/useDocumentTitle';
import { Switch } from '../webhooks/Switch';
import { KnowledgeTabs } from './KnowledgeTabs';
import { VinImportDialog } from './VinImportDialog';
import { VinRuleDialog } from './VinRuleDialog';
import { VinTestPanel } from './VinTestPanel';
import { errorReason, isForbidden, useDeleteVinRule, useSaveVinRule, useVinRules } from './knowledgeApi';
import { ruleToInput } from './knowledgeDraft';
import { VIN_RULE_LIMIT, type VinRuleDto } from './knowledgeTypes';
import './knowledge.css';

const STEP = 100;

/** Highest priority first, then name - the order the rules are weighed in. */
const byPriority = (a: VinRuleDto, b: VinRuleDto) => b.priority - a.priority || a.name.localeCompare(b.name);

/** `/knowledge/vin-rules`: the patterns that turn a VIN into a model, variant, model year and theme (ADR-0039). People who may edit
 * assets can read them; only `knowledge.manage` can change them, import them, or run the VIN test. */
export function VinRulesPage() {
  const { t, i18n } = useTranslation();
  const { push } = useToast();
  useDocumentTitle(t('knowledge.rules.title'));
  const me = useMe();
  const permissions = me.data?.permissions ?? [];
  const canManage = permissions.includes('knowledge.manage');
  const canView = canManage || permissions.includes('assets.update');
  const query = useVinRules(canView);
  const toggle = useSaveVinRule();
  const remove = useDeleteVinRule();
  const [filter, setFilter] = useState('');
  const [shown, setShown] = useState(STEP);
  const [form, setForm] = useState<{ rule: VinRuleDto | null; key: number } | null>(null);
  const [importing, setImporting] = useState(false);
  const [toDelete, setToDelete] = useState<VinRuleDto | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [toggling, setToggling] = useState<string | null>(null);

  const all = useMemo(() => [...(query.data ?? [])].sort(byPriority), [query.data]);
  const needle = filter.trim().toLowerCase();
  const matching = useMemo(
    () =>
      needle === ''
        ? all
        : all.filter((r) =>
            [r.name, r.pattern, r.model, r.variant ?? '', r.theme ?? ''].some((v) => v.toLowerCase().includes(needle)),
          ),
    [all, needle],
  );
  const rows = matching.slice(0, shown);
  const atLimit = all.length >= VIN_RULE_LIMIT;
  const empty = !query.isPending && !query.error && all.length === 0;
  const fmt = (n: number) => n.toLocaleString(i18n.language);

  const addRule = (
    <Button variant="primary" disabled={atLimit} onClick={() => setForm((f) => ({ rule: null, key: (f?.key ?? 0) + 1 }))}>
      <Plus size={16} weight="bold" aria-hidden="true" />
      {t('knowledge.rules.add')}
    </Button>
  );
  const importRules = (
    <Button variant="secondary" onClick={() => setImporting(true)}>
      {t('knowledge.import.open')}
    </Button>
  );
  const actions = canManage ? (
    <div className="dam-knactions">
      {importRules}
      {addRule}
    </div>
  ) : undefined;

  const closeDelete = () => {
    setToDelete(null);
    setDeleteError(null);
  };

  const flip = (rule: VinRuleDto, enabled: boolean) => {
    setToggling(rule.id);
    toggle.mutate(
      { id: rule.id, body: ruleToInput(rule, enabled) },
      {
        onSuccess: () =>
          push({
            title: t(enabled ? 'knowledge.rules.nowEnabled' : 'knowledge.rules.nowDisabled', { name: rule.name }),
            tone: 'success',
          }),
        onError: (e) => push({ title: t('knowledge.rules.saveFailed', { reason: errorReason(e) }), tone: 'error' }),
        onSettled: () => setToggling(null),
      },
    );
  };

  let body;
  if (me.isPending) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('knowledge.rules.loading')}</span>
        <div className="dam-stack dam-stack--fill" aria-hidden="true">
          <Skeleton height={40} />
          <Skeleton height={200} />
        </div>
      </div>
    );
  } else if (!canView || isForbidden(query.error)) {
    body = (
      <EmptyState
        icon={<Lock size={28} />}
        title={t('knowledge.rules.forbidden.title')}
        description={t('knowledge.rules.forbidden.description')}
      />
    );
  } else if (query.error) {
    body = (
      <div role="alert">
        <EmptyState
          icon={<WarningCircle size={28} />}
          title={t('knowledge.rules.loadFailed')}
          description={t('knowledge.rules.loadFailedHint')}
          action={<Button onClick={() => void query.refetch()}>{t('knowledge.retry')}</Button>}
        />
      </div>
    );
  } else if (empty) {
    body = (
      <>
        {canManage && <VinTestPanel />}
        <EmptyState
          icon={<ListMagnifyingGlass size={28} />}
          title={t('knowledge.rules.empty.title')}
          description={t(canManage ? 'knowledge.rules.empty.manage' : 'knowledge.rules.empty.read')}
          action={canManage ? actions : undefined}
        />
      </>
    );
  } else if (query.isPending) {
    body = (
      <div role="status" aria-live="polite">
        <span className="dam-sr-only">{t('knowledge.rules.loading')}</span>
        <div className="dam-stack dam-stack--fill" aria-hidden="true">
          <Skeleton height={40} />
          <Skeleton height={200} />
        </div>
      </div>
    );
  } else {
    body = (
      <>
        {canManage && <VinTestPanel />}
        <Input
          type="search"
          label={t('knowledge.rules.search')}
          placeholder={t('knowledge.rules.searchPlaceholder')}
          value={filter}
          autoComplete="off"
          onChange={(e) => {
            setFilter(e.target.value);
            setShown(STEP);
          }}
        />
        <Table<VinRuleDto>
          caption={t('knowledge.rules.table')}
          rows={rows}
          getRowId={(r) => r.id}
          emptyMessage={t('knowledge.rules.noMatch', { q: filter.trim() })}
          columns={[
            { key: 'name', header: t('knowledge.rules.name'), render: (r) => <strong className="dam-knname">{r.name}</strong> },
            {
              key: 'pattern',
              header: t('knowledge.rules.pattern'),
              render: (r) => <code className="dam-chip-code">{r.pattern}</code>,
            },
            { key: 'model', header: t('knowledge.field.model'), render: (r) => <span className="dam-kncode">{r.model}</span> },
            {
              key: 'variant',
              header: t('knowledge.field.variant'),
              className: 'dam-col--hide-sm',
              render: (r) =>
                r.variant ? (
                  <span className="dam-kncode">{r.variant}</span>
                ) : (
                  <span className="dam-hint">{t('knowledge.none')}</span>
                ),
            },
            {
              key: 'year',
              header: t('knowledge.result.modelYear'),
              render: (r) =>
                r.modelYear != null && !r.yearFromVin ? (
                  r.modelYear
                ) : (
                  <span className="dam-hint">{t('knowledge.rules.fromVin')}</span>
                ),
            },
            {
              key: 'theme',
              header: t('knowledge.field.theme'),
              className: 'dam-col--hide-sm',
              render: (r) =>
                r.theme ? (
                  <Badge size="sm" tone="brand">
                    {r.theme}
                  </Badge>
                ) : (
                  <span className="dam-hint">{t('knowledge.none')}</span>
                ),
            },
            { key: 'priority', header: t('knowledge.rules.priority'), className: 'dam-knnum', render: (r) => r.priority },
            {
              key: 'enabled',
              header: t('knowledge.rules.enabledCol'),
              render: (r) =>
                canManage ? (
                  <Switch
                    checked={r.enabled}
                    disabled={toggling === r.id}
                    onChange={(v) => flip(r, v)}
                    label={t(r.enabled ? 'knowledge.rules.enabled' : 'knowledge.rules.disabled')}
                    ariaLabel={t('knowledge.rules.enableRule', { name: r.name })}
                  />
                ) : (
                  <Badge size="sm" tone={r.enabled ? 'success' : 'neutral'}>
                    {t(r.enabled ? 'knowledge.rules.enabled' : 'knowledge.rules.disabled')}
                  </Badge>
                ),
            },
            ...(canManage
              ? [
                  {
                    key: 'actions',
                    header: <span className="dam-sr-only">{t('knowledge.col.actions')}</span>,
                    render: (r: VinRuleDto) => (
                      <span className="dam-knrowactions">
                        <button
                          type="button"
                          className="dam-btn dam-btn--ghost dam-btn--sm dam-btn--icon"
                          aria-label={t('knowledge.rules.edit', { name: r.name })}
                          title={t('knowledge.rules.edit', { name: r.name })}
                          onClick={() => setForm((f) => ({ rule: r, key: (f?.key ?? 0) + 1 }))}
                        >
                          <PencilSimple size={16} aria-hidden="true" />
                        </button>
                        <button
                          type="button"
                          className="dam-btn dam-btn--ghost dam-btn--sm dam-btn--icon"
                          aria-label={t('knowledge.rules.delete', { name: r.name })}
                          title={t('knowledge.rules.delete', { name: r.name })}
                          onClick={() => setToDelete(r)}
                        >
                          <Trash size={16} aria-hidden="true" />
                        </button>
                      </span>
                    ),
                  },
                ]
              : []),
          ]}
        />
        {matching.length > rows.length && (
          <div className="dam-knmore">
            <Button variant="secondary" onClick={() => setShown((n) => n + STEP)}>
              {t('knowledge.rules.showMore', { count: Math.min(STEP, matching.length - rows.length) })}
            </Button>
          </div>
        )}
        <p className="dam-hint" role="status">
          {t('knowledge.rules.count', { count: rows.length, total: fmt(matching.length) })}{' '}
          {t('knowledge.rules.limitNote', { max: fmt(VIN_RULE_LIMIT) })}
        </p>
        {atLimit && (
          <p className="dam-knwarn" role="status">
            <WarningCircle size={14} weight="bold" aria-hidden="true" />
            {t('knowledge.rules.limitReached', { max: fmt(VIN_RULE_LIMIT) })}
          </p>
        )}
      </>
    );
  }

  return (
    <div className="dam-page dam-kn">
      <PageHeader
        eyebrow={t('knowledge.title')}
        title={t('knowledge.rules.title')}
        description={t('knowledge.rules.description')}
        actions={canManage && canView && !!query.data && !empty ? actions : undefined}
      />
      <KnowledgeTabs />
      {body}

      {form && (
        <VinRuleDialog
          key={form.key}
          open
          rule={form.rule}
          onClose={() => setForm(null)}
          onSaved={(saved, created) => {
            setForm(null);
            push({
              title: t(created ? 'knowledge.rules.created' : 'knowledge.rules.saved', { name: saved.name }),
              tone: 'success',
            });
          }}
        />
      )}
      {importing && <VinImportDialog open onClose={() => setImporting(false)} />}

      <Modal
        open={toDelete !== null}
        title={t('knowledge.rules.deleteTitle')}
        closeLabel={t('knowledge.close')}
        onClose={closeDelete}
      >
        <div className="dam-stack dam-dialogform">
          <p>{t('knowledge.rules.deleteBody', { name: toDelete?.name })}</p>
          <p className="dam-hint">{t('knowledge.rules.deleteNote')}</p>
          {deleteError && (
            <p className="dam-error" role="alert">
              {deleteError}
            </p>
          )}
          <div className="dam-dialogform__actions">
            <Button variant="ghost" onClick={closeDelete}>
              {t('knowledge.cancel')}
            </Button>
            <Button
              variant="danger"
              loading={remove.isPending}
              onClick={() => {
                const gone = toDelete!;
                remove.mutate(gone.id, {
                  onSuccess: () => {
                    closeDelete();
                    push({ title: t('knowledge.rules.deleted', { name: gone.name }), tone: 'success' });
                  },
                  onError: (e) => setDeleteError(t('knowledge.rules.deleteFailed', { reason: errorReason(e) })),
                });
              }}
            >
              {t('knowledge.rules.deleteConfirm')}
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
}
