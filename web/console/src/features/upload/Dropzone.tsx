import { useId, useState, type DragEvent, type ReactNode } from 'react';
import { Badge } from '../../ui';

interface DropzoneProps {
  label: string;
  hint: string;
  icon?: ReactNode;
  /** What the zone says while files are held over it. */
  activeLabel: string;
  /** Accepted kinds, shown as small chips (large variant only). */
  kinds?: string[];
  /** Limits and other fine print under the hint (large variant only). */
  detail?: string;
  /** Visible stand-in for the file picker button; the real control is the input covering the zone. */
  chooseLabel?: string;
  onFiles: (files: File[]) => void;
  disabled?: boolean;
  /** A slimmer variant for once files are already queued ("add more"). */
  compact?: boolean;
}

/** A native drag-and-drop target plus an ordinary file input, so choosing files works identically with a mouse, a
 * keyboard (Enter/Space activates the hidden input) or a screen reader - drag-and-drop is a bonus, never the only way in. */
export function Dropzone({
  label,
  hint,
  icon,
  activeLabel,
  kinds,
  detail,
  chooseLabel,
  onFiles,
  disabled,
  compact,
}: DropzoneProps) {
  const [dragging, setDragging] = useState(false);
  const inputId = useId();

  const take = (list: FileList | null) => {
    if (list && list.length > 0) onFiles(Array.from(list));
  };

  return (
    <div
      className={`dam-dropzone${dragging ? ' dam-dropzone--active' : ''}${compact ? ' dam-dropzone--compact' : ''}`}
      onDragOver={(e: DragEvent) => {
        e.preventDefault();
        if (!disabled) setDragging(true);
      }}
      onDragLeave={() => setDragging(false)}
      onDrop={(e: DragEvent) => {
        e.preventDefault();
        setDragging(false);
        if (!disabled) take(e.dataTransfer.files);
      }}
    >
      {icon && <span className="dam-dropzone__icon">{icon}</span>}
      <label htmlFor={inputId} className="dam-dropzone__label">
        <strong>{dragging ? activeLabel : label}</strong>
        <span className="dam-hint">{hint}</span>
      </label>
      {!compact && chooseLabel && (
        <span className="dam-btn dam-btn--secondary dam-dropzone__choose" aria-hidden="true">
          {chooseLabel}
        </span>
      )}
      {!compact && kinds && kinds.length > 0 && (
        <ul className="dam-dropzone__kinds" aria-label={kinds.join(', ')}>
          {kinds.map((k) => (
            <li key={k}>
              <Badge size="sm">{k}</Badge>
            </li>
          ))}
        </ul>
      )}
      {!compact && detail && <span className="dam-hint dam-dropzone__detail">{detail}</span>}
      <input
        id={inputId}
        type="file"
        multiple
        disabled={disabled}
        className="dam-dropzone__input"
        onChange={(e) => {
          take(e.target.files);
          e.target.value = ''; // lets the same file be picked again after being removed from the queue
        }}
      />
    </div>
  );
}
