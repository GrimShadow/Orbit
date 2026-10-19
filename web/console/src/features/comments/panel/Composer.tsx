import {
  useEffect,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ChangeEvent,
  type KeyboardEvent,
  type ReactNode,
  type RefObject,
} from 'react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton, WarningCircle, X } from '../../../ui';
import { commentErrorMessage, useMentionable, type MentionableUser, type NewAnchor } from '../commentsApi';
import { anchorLabel } from './anchorLabel';
import { adjustMentions, activeMention, fromTokens, toTokens, type ComposerMention } from './mentionText';
import { useDebounced } from './useDebounced';

export const MAX_BODY = 4000;
const NEAR_LIMIT = 3600;
const MAX_HEIGHT = 240;

export interface ComposerProps {
  assetId: string;
  /** Accessible name of the text box (it has no visible label of its own). */
  label: string;
  placeholder?: string;
  /** Stored text (with mention tokens) to start from - the comment being edited. */
  initialBody?: string;
  submitLabel: string;
  /** Edit mode: posting the text unchanged is pointless, so it stays disabled until something differs. */
  requireChange?: boolean;
  /** The inline reply / edit look: smaller, no hint line. */
  compact?: boolean;
  /** Move focus into the box when it appears (a reply or edit box the person just asked for). */
  focusOnMount?: boolean;
  /** An anchor drawn on the preview that this comment will carry; shown as a removable chip. */
  anchor?: NewAnchor | null;
  onClearAnchor?: () => void;
  /** Resolves when posted (the field is then cleared); a rejection is shown under the field and the text is kept. */
  onSubmit: (body: string) => Promise<unknown>;
  /** Shows a Cancel button and makes Escape (with no suggestions open) cancel. */
  onCancel?: () => void;
  inputRef?: RefObject<HTMLTextAreaElement>;
}

/** The comment box: a textarea with @mention suggestions. Text holds `@Name`; a parallel list of ranges remembers which
 * of those are real people and turns them into `@[Name](user:id)` tokens on submit (see mentionText.ts). A mirror layer
 * behind the transparent textarea paints the mentions as chips. */
export function Composer(props: ComposerProps) {
  const {
    assetId,
    label,
    placeholder,
    initialBody,
    submitLabel,
    requireChange,
    compact,
    anchor,
    onClearAnchor,
    onSubmit,
    onCancel,
  } = props;
  const { t } = useTranslation();
  const uid = useId();
  const ownRef = useRef<HTMLTextAreaElement>(null);
  const inputRef = props.inputRef ?? ownRef;
  const mirrorRef = useRef<HTMLDivElement>(null);
  const start = useMemo(() => fromTokens(initialBody ?? ''), [initialBody]);
  const [text, setText] = useState(start.text);
  const [mentions, setMentions] = useState<ComposerMention[]>(start.mentions);
  const [caret, setCaret] = useState(start.text.length);
  const [focused, setFocused] = useState(false);
  const [picked, setPicked] = useState<{ key: string | null; i: number }>({ key: null, i: 0 });
  const [dismissed, setDismissed] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pendingCaret = useRef<number | null>(null);

  const trigger = activeMention(text, caret, mentions);
  const triggerKey = trigger ? `${trigger.start}:${trigger.query}` : null;
  const query = useDebounced(trigger?.query ?? '', 150);
  const found = useMentionable(assetId, query);
  // While the next letters load the previous answer is still on screen; keep only the people who match what is typed now.
  const typed = trigger?.query.toLowerCase() ?? '';
  const options: MentionableUser[] =
    typed && query
      ? (found.data ?? []).filter((u) => u.displayName.toLowerCase().includes(typed) || u.email.toLowerCase().includes(typed))
      : [];
  const open = focused && options.length > 0 && dismissed !== triggerKey;
  // The highlighted option starts at the top again whenever what is being typed changes.
  const active = picked.key === triggerKey ? picked.i : 0;
  const setActive = (i: number) => setPicked({ key: triggerKey, i });
  const current = Math.min(active, Math.max(0, options.length - 1));
  const listId = `${uid}-list`;
  const optionId = (i: number) => `${uid}-opt-${i}`;

  const focusOnMount = props.focusOnMount;
  useEffect(() => {
    if (focusOnMount) inputRef.current?.focus();
  }, [focusOnMount, inputRef]);

  // Grow with the text up to a cap; the mirror layer follows the textarea's scroll so the chips stay under their words.
  useLayoutEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    el.style.height = 'auto';
    if (el.scrollHeight > 0) el.style.height = `${Math.min(el.scrollHeight + 2, MAX_HEIGHT)}px`;
    if (mirrorRef.current) mirrorRef.current.scrollTop = el.scrollTop;
  }, [text, inputRef]);
  useLayoutEffect(() => {
    const at = pendingCaret.current;
    if (at == null || !inputRef.current) return;
    pendingCaret.current = null;
    inputRef.current.setSelectionRange(at, at);
    setCaret(at);
  }, [text, inputRef]);

  const body = toTokens(text, mentions);
  const tooLong = body.length > MAX_BODY;
  const unchanged = requireChange && body === (initialBody ?? '').trim();
  const canPost = body.length > 0 && !tooLong && !unchanged && !busy;

  const submit = async () => {
    if (!canPost) return;
    setBusy(true);
    setError(null);
    try {
      await onSubmit(body);
      setText('');
      setMentions([]);
      setCaret(0);
      inputRef.current?.focus();
    } catch (e) {
      setError(commentErrorMessage(e));
    } finally {
      setBusy(false);
    }
  };

  const choose = (user: MentionableUser) => {
    if (!trigger) return;
    const shown = `@${user.displayName}`;
    const insert = `${shown} `;
    const end = caret;
    const shift = insert.length - (end - trigger.start);
    const kept = mentions
      .filter((m) => m.end <= trigger.start || m.start >= end)
      .map((m) => (m.start >= end ? { ...m, start: m.start + shift, end: m.end + shift } : m));
    const added: ComposerMention = {
      start: trigger.start,
      end: trigger.start + shown.length,
      userId: user.id,
      name: user.displayName,
    };
    pendingCaret.current = trigger.start + insert.length;
    setText(text.slice(0, trigger.start) + insert + text.slice(end));
    setMentions([...kept, added].sort((a, b) => a.start - b.start));
    setError(null);
  };

  const onChange = (e: ChangeEvent<HTMLTextAreaElement>) => {
    const next = e.target.value;
    setMentions((m) => adjustMentions(text, next, m));
    setText(next);
    setCaret(e.target.selectionStart);
    setError(null);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.nativeEvent.isComposing) return;
    if (open) {
      const move = (d: number) => {
        e.preventDefault();
        setActive((current + d + options.length) % options.length);
      };
      if (e.key === 'ArrowDown') return move(1);
      if (e.key === 'ArrowUp') return move(-1);
      if ((e.key === 'Enter' && !e.ctrlKey && !e.metaKey) || e.key === 'Tab') {
        e.preventDefault();
        choose(options[current]!);
        return;
      }
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        setDismissed(triggerKey);
        return;
      }
    }
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      void submit();
    } else if (e.key === 'Escape' && onCancel) {
      e.preventDefault();
      onCancel();
    }
  };

  const mirror = () => {
    const parts: ReactNode[] = [];
    let at = 0;
    for (const m of mentions) {
      if (m.start < at || text.slice(m.start, m.end) !== `@${m.name}`) continue;
      if (m.start > at) parts.push(text.slice(at, m.start));
      parts.push(
        <mark key={m.start} className="dam-composer__mention">
          {text.slice(m.start, m.end)}
        </mark>,
      );
      at = m.end;
    }
    parts.push(text.slice(at));
    return parts;
  };

  const counterId = `${uid}-count`;
  const errorId = `${uid}-error`;
  const describedBy = [error && errorId, body.length >= NEAR_LIMIT && counterId].filter(Boolean).join(' ') || undefined;

  return (
    <div className={`dam-composer${compact ? ' dam-composer--compact' : ''}`}>
      {anchor && (
        <div className="dam-composer__anchor">
          <span className="dam-composer__chip" data-testid="draft-anchor">
            {t('comments.composer.attached', { anchor: anchorLabel(anchor, t) })}
          </span>
          <IconButton label={t('comments.composer.removeAnchor')} size="sm" onClick={onClearAnchor}>
            <X size={14} weight="bold" aria-hidden="true" />
          </IconButton>
        </div>
      )}
      <div className="dam-composer__field" data-invalid={error || tooLong ? 'true' : undefined}>
        <div ref={mirrorRef} className="dam-composer__mirror" aria-hidden="true">
          {mirror()}
          {'​'}
        </div>
        <textarea
          ref={inputRef}
          className="dam-composer__input"
          aria-label={label}
          placeholder={placeholder}
          rows={compact ? 2 : 3}
          value={text}
          readOnly={busy}
          aria-busy={busy || undefined}
          aria-invalid={error || tooLong ? true : undefined}
          aria-describedby={describedBy}
          aria-autocomplete="list"
          aria-controls={open ? listId : undefined}
          aria-activedescendant={open ? optionId(current) : undefined}
          aria-keyshortcuts="Control+Enter Meta+Enter"
          onChange={onChange}
          onSelect={(e) => setCaret(e.currentTarget.selectionStart)}
          onScroll={(e) => {
            if (mirrorRef.current) mirrorRef.current.scrollTop = e.currentTarget.scrollTop;
          }}
          onKeyDown={onKeyDown}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
        />
        {open && (
          // Options take the mouse down (not focus), so the text box keeps its caret while one is chosen.
          <ul id={listId} role="listbox" aria-label={t('comments.composer.suggestions')} className="dam-composer__menu">
            {options.map((u, i) => (
              // eslint-disable-next-line jsx-a11y/click-events-have-key-events -- keys are handled on the text box (combobox pattern)
              <li
                key={u.id}
                id={optionId(i)}
                role="option"
                aria-selected={i === current}
                className="dam-composer__option"
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => choose(u)}
                onMouseMove={() => setActive(i)}
              >
                <span className="dam-composer__option-name">{u.displayName}</span>
                <span className="dam-composer__option-mail">{u.email}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
      <div className="dam-sr-only" role="status">
        {open ? t('comments.composer.suggestionCount', { count: options.length }) : ''}
      </div>
      {error && (
        <span id={errorId} className="dam-error dam-composer__error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {error}
        </span>
      )}
      {tooLong && (
        <span className="dam-error dam-composer__error" role="alert">
          <WarningCircle size={14} weight="bold" aria-hidden="true" />
          {t('comments.composer.tooLong', { count: body.length - MAX_BODY })}
        </span>
      )}
      <div className="dam-composer__foot">
        <span className="dam-composer__hint" id={counterId} data-over={tooLong ? 'true' : undefined}>
          {body.length >= NEAR_LIMIT
            ? t('comments.composer.counter', { count: body.length, max: MAX_BODY })
            : compact
              ? null
              : t('comments.composer.hint')}
        </span>
        <div className="dam-composer__actions">
          {onCancel && (
            <Button variant="ghost" size="sm" onClick={onCancel} disabled={busy}>
              {t('comments.composer.cancel')}
            </Button>
          )}
          <Button variant="primary" size="sm" loading={busy} disabled={!canPost} onClick={() => void submit()}>
            {submitLabel}
          </Button>
        </div>
      </div>
    </div>
  );
}
