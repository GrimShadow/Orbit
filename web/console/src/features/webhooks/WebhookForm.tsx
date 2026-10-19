import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Drawer, Input } from '../../ui';
import { EventPicker } from './EventPicker';
import { Switch } from './Switch';
import {
  errorText,
  fieldErrors,
  useCreateWebhook,
  useUpdateWebhook,
  type WebhookDto,
  type WebhookEventType,
  type WebhookInput,
} from './webhooksApi';

interface WebhookFormProps {
  /** The webhook being edited, or `null` to create one. */
  webhook: WebhookDto | null;
  events: WebhookEventType[];
  eventsLoading: boolean;
  onClose: () => void;
  /** Called after a create with the one-time secret, or after an edit with `null`. */
  onSaved: (result: { webhook: WebhookDto; secret: string | null }) => void;
}

const isHttpUrl = (s: string) => /^https?:\/\/\S+$/i.test(s.trim());

function Form({ webhook, events, eventsLoading, onClose, onSaved }: WebhookFormProps) {
  const { t } = useTranslation();
  const create = useCreateWebhook();
  const update = useUpdateWebhook();
  const [name, setName] = useState(webhook?.name ?? '');
  const [url, setUrl] = useState(webhook?.url ?? '');
  const [selected, setSelected] = useState<string[]>(webhook?.events ?? []);
  const [active, setActive] = useState(webhook?.active ?? true);
  const [errors, setErrors] = useState<Partial<Record<'name' | 'url' | 'events', string>>>({});
  const [general, setGeneral] = useState<string | null>(null);
  const busy = create.isPending || update.isPending;

  // Editing a field retires the message about it; the next submit checks again.
  const clear = (field: keyof typeof errors) => setErrors((prev) => ({ ...prev, [field]: undefined }));

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const local: typeof errors = {};
    if (!name.trim()) local.name = t('webhooks.form.nameRequired');
    if (!url.trim()) local.url = t('webhooks.form.urlRequired');
    else if (!isHttpUrl(url)) local.url = t('webhooks.form.urlInvalid');
    if (selected.length === 0) local.events = t('webhooks.form.eventsRequired');
    setErrors(local);
    setGeneral(null);
    if (Object.keys(local).length > 0) return;

    const body: WebhookInput = { name: name.trim(), url: url.trim(), events: selected, active };
    const onError = (err: unknown) => {
      const { byField, other } = fieldErrors(err);
      setErrors(byField);
      setGeneral(other ?? errorText(err) ?? t('webhooks.form.saveFailed'));
    };
    if (webhook) {
      update.mutate({ id: webhook.id, body }, { onSuccess: (w) => onSaved({ webhook: w, secret: null }), onError });
    } else {
      create.mutate(body, { onSuccess: (c) => onSaved({ webhook: c.webhook, secret: c.secret }), onError });
    }
  };

  return (
    <form className="dam-stack dam-dialogform dam-whform" onSubmit={submit} noValidate>
      {general && (
        <p role="alert" className="dam-whwarn dam-whwarn--danger">
          {general}
        </p>
      )}
      <Input
        label={t('webhooks.form.name')}
        value={name}
        onChange={(e) => {
          setName(e.target.value);
          clear('name');
        }}
        error={errors.name}
        maxLength={200}
        autoComplete="off"
        placeholder={t('webhooks.form.namePlaceholder')}
      />
      <Input
        label={t('webhooks.form.url')}
        type="url"
        inputMode="url"
        value={url}
        onChange={(e) => {
          setUrl(e.target.value);
          clear('url');
        }}
        error={errors.url}
        hint={t('webhooks.form.urlHint')}
        autoComplete="off"
        spellCheck={false}
        placeholder="https://example.com/hooks/orbit"
      />
      <div className="dam-field">
        <span className="dam-label">{t('webhooks.form.events')}</span>
        {eventsLoading ? (
          <p className="dam-hint">{t('webhooks.form.eventsLoading')}</p>
        ) : events.length === 0 ? (
          <p className="dam-hint">{t('webhooks.form.eventsUnavailable')}</p>
        ) : (
          <EventPicker
            events={events}
            value={selected}
            onChange={(next) => {
              setSelected(next);
              clear('events');
            }}
            error={errors.events}
          />
        )}
      </div>
      <Switch checked={active} onChange={setActive} label={t('webhooks.form.active')} hint={t('webhooks.form.activeHint')} />
      {!webhook && <p className="dam-hint">{t('webhooks.form.secretNote')}</p>}
      <div className="dam-dialogform__actions dam-whform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('webhooks.cancel')}
        </Button>
        <Button type="submit" variant="primary" loading={busy}>
          {webhook ? t('webhooks.form.save') : t('webhooks.form.create')}
        </Button>
      </div>
    </form>
  );
}

/** The create/edit side panel. The form mounts fresh each time it opens, so it never shows a previous webhook's values. */
export function WebhookForm(props: WebhookFormProps & { open: boolean }) {
  const { t } = useTranslation();
  const { open, ...rest } = props;
  return (
    <Drawer
      open={open}
      title={rest.webhook ? t('webhooks.form.editTitle') : t('webhooks.form.newTitle')}
      closeLabel={t('webhooks.close')}
      onClose={rest.onClose}
    >
      <Form {...rest} />
    </Drawer>
  );
}
