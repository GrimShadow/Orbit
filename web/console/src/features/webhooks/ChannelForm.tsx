import { useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Drawer, Input, Select } from '../../ui';
import { EventPicker } from './EventPicker';
import { Switch } from './Switch';
import { errorText, fieldErrors, useSaveChannel, type ChannelDto, type WebhookEventType } from './webhooksApi';

interface ChannelFormProps {
  /** The channel being edited, or `null` to create one. */
  channel: ChannelDto | null;
  events: WebhookEventType[];
  eventsLoading: boolean;
  onClose: () => void;
  onSaved: (channel: ChannelDto) => void;
}

const isHttpUrl = (s: string) => /^https?:\/\/\S+$/i.test(s.trim());

function Form({ channel, events, eventsLoading, onClose, onSaved }: ChannelFormProps) {
  const { t } = useTranslation();
  const save = useSaveChannel();
  const [kind, setKind] = useState(channel?.kind ?? 'slack');
  const [name, setName] = useState(channel?.name ?? '');
  const [url, setUrl] = useState('');
  const [selected, setSelected] = useState<string[]>(channel?.events ?? []);
  const [active, setActive] = useState(channel?.active ?? true);
  const [errors, setErrors] = useState<Partial<Record<'kind' | 'name' | 'url' | 'events', string>>>({});
  const [general, setGeneral] = useState<string | null>(null);

  // Editing a field retires the message about it; the next submit checks again.
  const clear = (field: keyof typeof errors) => setErrors((prev) => ({ ...prev, [field]: undefined }));

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const local: typeof errors = {};
    if (!name.trim()) local.name = t('webhooks.form.nameRequired');
    // Editing: an empty URL means "keep the stored one". Creating: it is required.
    if (!url.trim()) {
      if (!channel) local.url = t('webhooks.form.urlRequired');
    } else if (!isHttpUrl(url)) local.url = t('webhooks.form.urlInvalid');
    if (selected.length === 0) local.events = t('webhooks.form.eventsRequired');
    setErrors(local);
    setGeneral(null);
    if (Object.keys(local).length > 0) return;

    save.mutate(
      { id: channel?.id, body: { kind, name: name.trim(), url: url.trim() || null, events: selected, active } },
      {
        onSuccess: onSaved,
        onError: (err) => {
          const { byField, other } = fieldErrors(err);
          setErrors(byField);
          setGeneral(other ?? errorText(err) ?? t('webhooks.form.saveFailed'));
        },
      },
    );
  };

  return (
    <form className="dam-stack dam-dialogform dam-whform" onSubmit={submit} noValidate>
      {general && (
        <p role="alert" className="dam-whwarn dam-whwarn--danger">
          {general}
        </p>
      )}
      <Select
        label={t('webhooks.channels.kind')}
        value={kind}
        options={[
          { value: 'slack', label: t('webhooks.channels.slack') },
          { value: 'teams', label: t('webhooks.channels.teams') },
        ]}
        onChange={(e) => setKind(e.target.value)}
        error={errors.kind}
      />
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
        placeholder={t('webhooks.channels.namePlaceholder')}
      />
      <Input
        label={t('webhooks.channels.url')}
        type="url"
        inputMode="url"
        value={url}
        onChange={(e) => {
          setUrl(e.target.value);
          clear('url');
        }}
        error={errors.url}
        autoComplete="off"
        spellCheck={false}
        placeholder={
          channel
            ? t('webhooks.channels.urlUnchanged')
            : kind === 'teams'
              ? 'https://…webhook.office.com/…'
              : 'https://hooks.slack.com/services/…'
        }
        hint={
          (channel ? t('webhooks.channels.urlEditHint', { hint: channel.urlHint }) : t('webhooks.channels.urlHint')) +
          ' ' +
          t(kind === 'teams' ? 'webhooks.channels.howTeams' : 'webhooks.channels.howSlack')
        }
      />
      <div className="dam-field">
        <span className="dam-label">{t('webhooks.channels.events')}</span>
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
      <Switch checked={active} onChange={setActive} label={t('webhooks.form.active')} hint={t('webhooks.channels.activeHint')} />
      <div className="dam-dialogform__actions dam-whform__actions">
        <Button variant="ghost" onClick={onClose}>
          {t('webhooks.cancel')}
        </Button>
        <Button type="submit" variant="primary" loading={save.isPending}>
          {channel ? t('webhooks.form.save') : t('webhooks.channels.create')}
        </Button>
      </div>
    </form>
  );
}

export function ChannelForm(props: ChannelFormProps & { open: boolean }) {
  const { t } = useTranslation();
  const { open, ...rest } = props;
  return (
    <Drawer
      open={open}
      title={rest.channel ? t('webhooks.channels.editTitle') : t('webhooks.channels.newTitle')}
      closeLabel={t('webhooks.close')}
      onClose={rest.onClose}
    >
      <Form {...rest} />
    </Drawer>
  );
}
