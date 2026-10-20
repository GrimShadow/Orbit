import { useEffect, useId, useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, CheckboxGroup, Input, Skeleton, X } from '../../../ui';
import type { Assignees } from '../definition';
import { useRoles, useUserNames, useUserSearch, type UserDto } from './designerApi';

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const id = setTimeout(() => setV(value), ms);
    return () => clearTimeout(id);
  }, [value, ms]);
  return v;
}

/** An accessible combobox over the tenant's users: type to search by name or email, arrow keys and Enter to pick. The label
 * and placeholder default to the workflow assignee wording; other pickers (report recipients) pass their own. */
export function PeopleCombobox({
  chosen,
  onPick,
  label,
  placeholder,
  hint,
}: {
  chosen: string[];
  onPick: (u: UserDto) => void;
  label?: string;
  placeholder?: string;
  hint?: string;
}) {
  const { t } = useTranslation();
  const id = useId();
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const term = useDebounced(q, 200);
  const search = useUserSearch(term);
  const results = (search.data ?? []).filter((u) => !chosen.includes(u.id));
  const showList = open && q.trim().length > 0 && results.length > 0;
  const optionId = (i: number) => `${id}-opt-${i}`;

  const pick = (u: UserDto) => {
    onPick(u);
    setQ('');
    setOpen(false);
    setActive(0);
  };
  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown' && results.length > 0) {
      e.preventDefault();
      setOpen(true);
      setActive((a) => (a + 1) % results.length);
    } else if (e.key === 'ArrowUp' && results.length > 0) {
      e.preventDefault();
      setOpen(true);
      setActive((a) => (a - 1 + results.length) % results.length);
    } else if (e.key === 'Enter' && showList) {
      e.preventDefault();
      const u = results[Math.min(active, results.length - 1)];
      if (u) pick(u);
    } else if (e.key === 'Escape' && open) {
      e.stopPropagation();
      setOpen(false);
    }
  };

  return (
    <div className="dam-wfcombo">
      <Input
        label={label ?? t('workflows.picker.people')}
        hint={hint}
        role="combobox"
        aria-expanded={showList}
        aria-controls={showList ? `${id}-list` : undefined}
        aria-autocomplete="list"
        aria-activedescendant={showList ? optionId(Math.min(active, results.length - 1)) : undefined}
        autoComplete="off"
        placeholder={placeholder ?? t('workflows.picker.peoplePlaceholder')}
        value={q}
        onChange={(e) => {
          setQ(e.target.value);
          setOpen(true);
          setActive(0);
        }}
        onKeyDown={onKeyDown}
        onBlur={() => setOpen(false)}
        onFocus={() => setOpen(true)}
      />
      {showList && (
        <ul id={`${id}-list`} role="listbox" aria-label={label ?? t('workflows.picker.people')} className="dam-wfcombo__list">
          {results.map((u, i) => (
            // Options are driven from the input (aria-activedescendant); a mouse press picks without stealing focus.
            <li
              key={u.id}
              id={optionId(i)}
              role="option"
              aria-selected={i === active}
              className="dam-wfcombo__opt"
              onMouseDown={(e) => {
                e.preventDefault();
                pick(u);
              }}
            >
              <span className="dam-wfcombo__name">{u.displayName || u.email}</span>
              <span className="dam-hint">{u.email}</span>
            </li>
          ))}
        </ul>
      )}
      {open && q.trim() && term === q && !search.isFetching && !showList && (
        <p className="dam-hint" role="status">
          {search.isError ? t('workflows.picker.peopleUnavailable') : t('workflows.picker.noMatches')}
        </p>
      )}
    </div>
  );
}

/** Roles as a checkbox list from the tenant's real roles (names must match exactly), plus people found by search. */
export function AssigneePicker({
  label,
  hint,
  value,
  onChange,
  error,
}: {
  label: string;
  hint?: string;
  value: Assignees;
  onChange: (next: Assignees) => void;
  error?: string;
}) {
  const { t } = useTranslation();
  const roles = useRoles();
  const names = useUserNames();
  const [typed, setTyped] = useState('');

  const known = new Set((roles.data ?? []).map((r) => r.name));
  const options = [
    ...(roles.data ?? []).map((r) => ({ value: r.name, label: r.name })),
    // A role the tenant does not have (typed in the JSON view, or since renamed) stays visible so it can be unticked.
    ...value.roles
      .filter((r) => roles.isSuccess && !known.has(r))
      .map((r) => ({ value: r, label: t('workflows.picker.unknownRole', { name: r }) })),
  ];

  const addTyped = () => {
    const name = typed.trim();
    if (name && !value.roles.includes(name)) onChange({ ...value, roles: [...value.roles, name] });
    setTyped('');
  };

  return (
    <fieldset className="dam-wfpicker">
      <legend className="dam-label">{label}</legend>
      {hint && <p className="dam-hint">{hint}</p>}

      {roles.isPending ? (
        <div role="status">
          <span className="dam-sr-only">{t('workflows.picker.rolesLoading')}</span>
          <Skeleton height={56} />
        </div>
      ) : roles.isError ? (
        <div className="dam-wfpicker__fallback">
          <p className="dam-hint" role="status">
            {t('workflows.picker.rolesFailed')}
          </p>
          <ul className="dam-wfchips" aria-label={t('workflows.picker.roles')}>
            {value.roles.map((r) => (
              <li key={r} className="dam-chip">
                <span className="dam-chip__text">{r}</span>
                <button
                  type="button"
                  className="dam-chip__remove"
                  aria-label={t('workflows.picker.removeRole', { name: r })}
                  onClick={() => onChange({ ...value, roles: value.roles.filter((x) => x !== r) })}
                >
                  <X size={12} weight="bold" aria-hidden="true" />
                </button>
              </li>
            ))}
          </ul>
          <div className="dam-wfpicker__add">
            <Input
              label={t('workflows.picker.addRole')}
              value={typed}
              onChange={(e) => setTyped(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  addTyped();
                }
              }}
            />
            <Button size="sm" onClick={addTyped}>
              {t('workflows.picker.addRoleButton')}
            </Button>
          </div>
        </div>
      ) : (
        <CheckboxGroup
          label={t('workflows.picker.roles')}
          options={options}
          value={value.roles}
          onChange={(roles) => onChange({ ...value, roles })}
          columns={2}
        />
      )}

      <PeopleCombobox
        chosen={value.users}
        onPick={(u) => {
          names.remember(u);
          onChange({ ...value, users: [...value.users, u.id] });
        }}
      />
      {value.users.length > 0 && (
        <ul className="dam-wfchips" aria-label={t('workflows.picker.selectedPeople')}>
          {value.users.map((id) => (
            <li key={id} className="dam-chip">
              <span className="dam-chip__text">{names.nameOf(id)}</span>
              <button
                type="button"
                className="dam-chip__remove"
                aria-label={t('workflows.picker.removePerson', { name: names.nameOf(id) })}
                onClick={() => onChange({ ...value, users: value.users.filter((x) => x !== id) })}
              >
                <X size={12} weight="bold" aria-hidden="true" />
              </button>
            </li>
          ))}
        </ul>
      )}
      {error && (
        <span className="dam-error" role="alert">
          {error}
        </span>
      )}
    </fieldset>
  );
}
