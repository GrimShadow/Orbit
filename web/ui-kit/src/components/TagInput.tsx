import { X } from '@phosphor-icons/react';
import { useId, useRef, useState, type KeyboardEvent } from 'react';

export interface TagInputProps {
  label: string;
  value: string[];
  onChange: (next: string[]) => void;
  hint?: string;
  placeholder?: string;
  /** Offered through a native datalist; already-chosen values are left out. */
  suggestions?: string[];
  /** Accessible name of each chip's remove button; receives the tag. */
  removeLabel?: (tag: string) => string;
  /** Accessible name of the chip list (distinct from the input's label). */
  listLabel?: string;
  disabled?: boolean;
}

const normalise = (s: string) => s.trim().toLowerCase();

/** Chips plus a text input. Enter or comma adds, Backspace on an empty input removes the last chip; values are trimmed,
 * lower-cased and de-duplicated. A real labelled control: the label points at the text input. */
export function TagInput({
  label,
  value,
  onChange,
  hint,
  placeholder,
  suggestions,
  removeLabel = (tag) => `Remove ${tag}`,
  listLabel = `${label} (selected)`,
  disabled,
}: TagInputProps) {
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [draft, setDraft] = useState('');

  const commit = (raw: string) => {
    const parts = raw.split(',').map(normalise).filter(Boolean);
    const next = [...value];
    for (const p of parts) if (!next.includes(p)) next.push(p);
    if (next.length !== value.length) onChange(next);
    setDraft('');
  };
  const remove = (tag: string) => onChange(value.filter((t) => t !== tag));

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' || e.key === ',') {
      if (draft.trim()) {
        e.preventDefault();
        commit(draft);
      } else if (e.key === ',') {
        e.preventDefault();
      }
    } else if (e.key === 'Backspace' && draft === '' && value.length > 0) {
      e.preventDefault();
      remove(value[value.length - 1]!);
    }
  };

  const listId = suggestions ? `${id}-list` : undefined;
  const open = suggestions?.filter((s) => !value.includes(normalise(s))) ?? [];

  return (
    <div className="dam-field">
      <label className="dam-label" htmlFor={id}>
        {label}
      </label>
      {/* The wrapper only forwards pointer focus to the input; keyboard users reach the input directly. */}
      {/* eslint-disable-next-line jsx-a11y/no-static-element-interactions, jsx-a11y/click-events-have-key-events */}
      <div className="dam-tags" onClick={() => input.current?.focus()}>
        {value.length > 0 && (
          <ul className="dam-tags__list" aria-label={listLabel}>
            {value.map((tag) => (
              <li key={tag} className="dam-chip">
                <span className="dam-chip__text">{tag}</span>
                <button
                  type="button"
                  className="dam-chip__remove"
                  aria-label={removeLabel(tag)}
                  disabled={disabled}
                  onClick={(e) => {
                    e.stopPropagation();
                    remove(tag);
                    input.current?.focus();
                  }}
                >
                  <X size={12} weight="bold" aria-hidden="true" />
                </button>
              </li>
            ))}
          </ul>
        )}
        <input
          ref={input}
          id={id}
          className="dam-tags__input"
          type="text"
          autoComplete="off"
          value={draft}
          placeholder={value.length === 0 ? placeholder : undefined}
          disabled={disabled}
          list={listId}
          aria-describedby={hint ? `${id}-hint` : undefined}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={onKeyDown}
          onBlur={() => {
            if (draft.trim()) commit(draft);
          }}
        />
        {listId && (
          <datalist id={listId}>
            {open.map((s) => (
              <option key={s} value={s} />
            ))}
          </datalist>
        )}
      </div>
      {hint && (
        <span id={`${id}-hint`} className="dam-hint">
          {hint}
        </span>
      )}
    </div>
  );
}
