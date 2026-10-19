import { WarningCircle } from '@phosphor-icons/react';
import { useTranslation } from 'react-i18next';
import { Button, Input, Modal } from '../../ui';
import { CodeBlock } from './CodeBlock';
import { CopyButton } from './CopyButton';

const NODE = `import crypto from 'node:crypto';

// rawBody: the request body exactly as received (a Buffer or string), never re-serialised JSON.
export function verify(secret, headers, rawBody) {
  const timestamp = headers['x-dam-timestamp'];
  if (Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return false; // older than 5 minutes
  const expected = 'v1=' + crypto.createHmac('sha256', secret).update(timestamp + '.' + rawBody).digest('hex');
  return headers['x-dam-signature'].split(',').some((s) => {
    const given = Buffer.from(s.trim());
    const want = Buffer.from(expected);
    return given.length === want.length && crypto.timingSafeEqual(given, want);
  });
}`;

const PYTHON = `import hashlib, hmac, time

def verify(secret: str, headers: dict, raw_body: bytes) -> bool:
    timestamp = headers["X-Dam-Timestamp"]
    if abs(time.time() - int(timestamp)) > 300:  # older than 5 minutes
        return False
    digest = hmac.new(secret.encode(), f"{timestamp}.".encode() + raw_body, hashlib.sha256).hexdigest()
    expected = "v1=" + digest
    return any(hmac.compare_digest(s.strip(), expected) for s in headers["X-Dam-Signature"].split(","))`;

/** How a receiver checks a delivery. Shared by the secret dialog and the webhook form's help. */
export function VerifyHelp() {
  const { t } = useTranslation();
  return (
    <details className="dam-whverify">
      <summary>{t('webhooks.verify.title')}</summary>
      <div className="dam-whverify__body">
        <p>{t('webhooks.verify.intro')}</p>
        <dl className="dam-whverify__headers">
          <dt>
            <code>X-Dam-Event</code>
          </dt>
          <dd>{t('webhooks.verify.event')}</dd>
          <dt>
            <code>X-Dam-Delivery</code>
          </dt>
          <dd>{t('webhooks.verify.delivery')}</dd>
          <dt>
            <code>X-Dam-Timestamp</code>
          </dt>
          <dd>{t('webhooks.verify.timestamp')}</dd>
          <dt>
            <code>X-Dam-Signature</code>
          </dt>
          <dd>
            <code>v1=&lt;hex HMAC-SHA256(secret, timestamp + &quot;.&quot; + body)&gt;</code>
          </dd>
        </dl>
        <p>{t('webhooks.verify.rules')}</p>
        <h3 className="dam-whverify__lang">Node.js</h3>
        <CodeBlock label="Node.js">{NODE}</CodeBlock>
        <h3 className="dam-whverify__lang">Python</h3>
        <CodeBlock label="Python">{PYTHON}</CodeBlock>
      </div>
    </details>
  );
}

interface SecretDialogProps {
  /** The secret to show once, or null when the dialog is closed. */
  secret: string | null;
  webhookName: string;
  /** `created` after create, `rotated` after a rotation. */
  reason: 'created' | 'rotated';
  onClose: () => void;
}

/** Shows a webhook's signing secret exactly once. The secret lives only in the caller's state: closing discards it. */
export function SecretDialog({ secret, webhookName, reason, onClose }: SecretDialogProps) {
  const { t } = useTranslation();
  return (
    <Modal
      open={secret !== null}
      title={t(reason === 'created' ? 'webhooks.secret.titleCreated' : 'webhooks.secret.titleRotated')}
      closeLabel={t('webhooks.close')}
      onClose={onClose}
    >
      <div className="dam-stack dam-dialogform">
        <p>{t('webhooks.secret.intro', { name: webhookName })}</p>
        <p className="dam-whwarn" role="note">
          <WarningCircle size={18} weight="fill" aria-hidden="true" />
          <span>{t('webhooks.secret.warning')}</span>
        </p>
        <div className="dam-whsecret">
          <Input
            label={t('webhooks.secret.label')}
            value={secret ?? ''}
            readOnly
            className="dam-whmono"
            onFocus={(e) => e.currentTarget.select()}
          />
          <CopyButton text={secret ?? ''} label={t('webhooks.secret.copy')} size="md" />
        </div>
        <VerifyHelp />
        <div className="dam-dialogform__actions">
          <Button variant="primary" onClick={onClose}>
            {t('webhooks.secret.saved')}
          </Button>
        </div>
      </div>
    </Modal>
  );
}
