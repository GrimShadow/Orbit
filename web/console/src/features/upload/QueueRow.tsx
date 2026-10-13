import { useTranslation } from 'react-i18next';
import { FileTypeIcon, XCircle } from '../../ui';
import { formatBytes } from '../../lib/format';
import { typeChoices, type QueueItem } from './uploadQueue';

/** A file's thumbnail: a local preview for images, otherwise the file-type glyph. */
export function FileThumb({ type, previewUrl, size = 56 }: { type: string; previewUrl?: string; size?: number }) {
  return (
    <span className="dam-queue__thumb" style={{ width: size, height: size }}>
      {previewUrl ? (
        <img src={previewUrl} alt="" />
      ) : (
        <FileTypeIcon type={type} size={size * 0.45} weight="duotone" aria-hidden="true" />
      )}
    </span>
  );
}

interface QueueRowProps {
  item: QueueItem;
  onTitle: (title: string) => void;
  onType: (type: string) => void;
  onRemove: () => void;
}

/** One file waiting to be uploaded: its title and type are editable, and anything the server would refuse is flagged
 * right here instead of after the upload. */
export function QueueRow({ item, onTitle, onType, onRemove }: QueueRowProps) {
  const { t } = useTranslation();
  const name = item.file.name;
  const choices = typeChoices(name);

  return (
    <li className={`dam-queue__row${item.problem ? ' dam-queue__row--problem' : ''}`}>
      <FileThumb type={item.type} previewUrl={item.previewUrl} />
      <div className="dam-queue__main">
        <input
          className="dam-input dam-queue__title"
          aria-label={t('upload.titleFor', { name })}
          value={item.title}
          maxLength={300}
          disabled={item.problem !== null}
          onChange={(e) => onTitle(e.target.value)}
        />
        <div className="dam-queue__meta">
          <span className="dam-queue__filename" title={name}>
            {name}
          </span>
          <span>{formatBytes(item.file.size)}</span>
          {choices.length > 1 ? (
            <select
              className="dam-select dam-queue__type"
              aria-label={t('upload.typeFor', { name })}
              value={item.type}
              onChange={(e) => onType(e.target.value)}
            >
              {choices.map((c) => (
                <option key={c} value={c}>
                  {t(`library.type.${c}`)}
                </option>
              ))}
            </select>
          ) : (
            <span className="dam-badge">{t(`library.type.${item.type}`)}</span>
          )}
        </div>
        {item.problem && (
          <p className="dam-queue__problem" role="alert">
            {t(`upload.problem.${item.problem}`, { type: t(`library.type.${item.type}`) })}
          </p>
        )}
      </div>
      <button type="button" className="dam-queue__remove" aria-label={t('upload.removeFile', { name })} onClick={onRemove}>
        <XCircle size={20} weight="fill" aria-hidden="true" />
      </button>
    </li>
  );
}
