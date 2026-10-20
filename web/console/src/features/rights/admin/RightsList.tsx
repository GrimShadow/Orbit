import { CalendarBlank, Copy, Drop, FileText, Images, PencilSimple } from '@phosphor-icons/react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  Card,
  DotsThree,
  IconButton,
  MagnifyingGlass,
  PopoverMenu,
  Select,
  StatCard,
  Trash,
  type MenuItem,
} from '../../../ui';
import type { Rights, RightsStatus } from '../rightsApi';
import { STATUSES, STATUS_TONE, asStatus, licenceLabel, statusLabel, validityText } from './rightsLabels';

const MAX_CHIPS = 4;

/** Channel or region codes as small chips; with none, the "everything" wording; past four, a "+N" (full list in its tooltip). */
function ScopeChips({ codes, all, label }: { codes: string[]; all: string; label: string }) {
  const { t } = useTranslation();
  if (codes.length === 0) {
    return (
      <p className="dam-rscopeline">
        <span className="dam-rscopeline__label">{label}</span>
        <span className="dam-rchip dam-rchip--all">{all}</span>
      </p>
    );
  }
  const shown = codes.slice(0, MAX_CHIPS);
  const rest = codes.slice(MAX_CHIPS);
  return (
    <div className="dam-rscopeline">
      <span className="dam-rscopeline__label">{label}</span>
      <ul className="dam-rchips" aria-label={label}>
        {shown.map((c) => (
          <li key={c} className="dam-rchip">
            {c}
          </li>
        ))}
        {rest.length > 0 && (
          <li className="dam-rchip dam-rchip--more" title={rest.join(', ')}>
            <span aria-hidden="true">+{rest.length}</span>
            <span className="dam-sr-only">{t('rights.list.moreCodes', { count: rest.length, list: rest.join(', ') })}</span>
          </li>
        )}
      </ul>
    </div>
  );
}

export function StatusChip({ rights }: { rights: Pick<Rights, 'status' | 'daysRemaining'> }) {
  const { t } = useTranslation();
  const status = asStatus(rights.status);
  return (
    <Badge tone={STATUS_TONE[status]} size="sm">
      {statusLabel(t, status, rights.daysRemaining)}
    </Badge>
  );
}

interface CardProps {
  rights: Rights;
  busy: boolean;
  onEdit: () => void;
  onDuplicate: () => void;
  onDelete: () => void;
}

function RightsCard({ rights: r, busy, onEdit, onDuplicate, onDelete }: CardProps) {
  const { t, i18n } = useTranslation();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const items: MenuItem[] = [
    { key: 'edit', label: t('rights.menu.edit'), icon: <PencilSimple size={16} />, onSelect: onEdit },
    { key: 'duplicate', label: t('rights.menu.duplicate'), icon: <Copy size={16} />, disabled: busy, onSelect: onDuplicate },
    {
      key: 'delete',
      label: t('rights.menu.delete'),
      icon: <Trash size={16} />,
      tone: 'danger',
      separatorBefore: true,
      onSelect: onDelete,
    },
  ];
  const menuLabel = t('rights.menu.label', { name: r.name });
  return (
    <li>
      <Card as="article" className="dam-rcard" aria-labelledby={`rights-${r.id}`}>
        <div className="dam-rcard__head">
          <div className="dam-rcard__title">
            <h2 id={`rights-${r.id}`} className="dam-rcard__name">
              <button type="button" className="dam-rcard__link" onClick={onEdit}>
                {r.name}
              </button>
            </h2>
            <p className="dam-rcard__sub">
              <span>{licenceLabel(t, r.licenceType)}</span>
              {r.licensor && (
                <>
                  <span aria-hidden="true"> · </span>
                  <span>{t('rights.list.licensor', { name: r.licensor })}</span>
                </>
              )}
            </p>
          </div>
          <div className="dam-rcard__controls">
            <StatusChip rights={r} />
            <IconButton
              label={menuLabel}
              size="sm"
              aria-haspopup="menu"
              aria-expanded={anchor !== null}
              onClick={(e) => setAnchor(anchor ? null : e.currentTarget)}
            >
              <DotsThree size={20} weight="bold" aria-hidden="true" />
            </IconButton>
            {anchor && <PopoverMenu anchor={anchor} label={menuLabel} items={items} onClose={() => setAnchor(null)} />}
          </div>
        </div>

        <p className="dam-rcard__validity">
          <CalendarBlank size={16} aria-hidden="true" />
          <span>{validityText(t, i18n.language, r.validFrom, r.validTo)}</span>
        </p>

        <div className="dam-rcard__scope">
          <ScopeChips codes={r.channels} all={t('rights.list.allChannels')} label={t('rights.list.channels')} />
          <ScopeChips codes={r.regions} all={t('rights.list.worldwide')} label={t('rights.list.regions')} />
        </div>

        <div className="dam-rcard__foot">
          <span className="dam-rcard__fact">
            <Images size={14} aria-hidden="true" />
            {t('rights.list.assets', { count: r.assetCount })}
          </span>
          <span className="dam-rcard__fact">
            <FileText size={14} aria-hidden="true" />
            {t('rights.list.documents', { count: r.documentCount })}
          </span>
          {r.watermarkRequired && (
            <Badge tone="neutral" size="sm">
              <Drop size={12} aria-hidden="true" /> {t('rights.list.watermark')}
            </Badge>
          )}
        </div>
      </Card>
    </li>
  );
}

export function RightsList(props: {
  rights: Rights[];
  busy: boolean;
  onEdit: (r: Rights) => void;
  onDuplicate: (r: Rights) => void;
  onDelete: (r: Rights) => void;
}) {
  const { t } = useTranslation();
  return (
    <ul className="dam-rlist" aria-label={t('rights.title')}>
      {props.rights.map((r) => (
        <RightsCard
          key={r.id}
          rights={r}
          busy={props.busy}
          onEdit={() => props.onEdit(r)}
          onDuplicate={() => props.onDuplicate(r)}
          onDelete={() => props.onDelete(r)}
        />
      ))}
    </ul>
  );
}

export function Summary({ counts }: { counts: Record<RightsStatus, number> }) {
  const { t } = useTranslation();
  return (
    <ul className="dam-rsummary" aria-label={t('rights.summary.label')}>
      <li>
        <StatCard label={t('rights.summary.valid')} value={counts.valid} tone="success" hint={t('rights.summary.validHint')} />
      </li>
      <li>
        <StatCard
          label={t('rights.summary.expiring')}
          value={counts.expiring}
          tone="warning"
          hint={t('rights.summary.expiringHint')}
        />
      </li>
      <li>
        <StatCard
          label={t('rights.summary.expired')}
          value={counts.expired}
          tone="danger"
          hint={t('rights.summary.expiredHint')}
        />
      </li>
    </ul>
  );
}

export function Toolbar({
  q,
  status,
  onQ,
  onStatus,
}: {
  q: string;
  status: RightsStatus | null;
  onQ: (q: string) => void;
  onStatus: (s: RightsStatus | null) => void;
}) {
  const { t } = useTranslation();
  return (
    <div className="dam-rtoolbar" role="search" aria-label={t('rights.toolbar.label')}>
      <div className="dam-field dam-rtoolbar__search">
        <label className="dam-label" htmlFor="rights-search">
          {t('rights.toolbar.search')}
        </label>
        <div className="dam-rsearch">
          <MagnifyingGlass size={16} aria-hidden="true" className="dam-rsearch__icon" />
          <input
            id="rights-search"
            type="search"
            className="dam-input dam-rsearch__input"
            value={q}
            placeholder={t('rights.toolbar.searchPlaceholder')}
            autoComplete="off"
            onChange={(e) => onQ(e.target.value)}
          />
        </div>
      </div>
      <div className="dam-rtoolbar__status">
        <Select
          label={t('rights.toolbar.status')}
          value={status ?? ''}
          onChange={(e) => onStatus((e.target.value || null) as RightsStatus | null)}
          options={[
            { value: '', label: t('rights.toolbar.allStatuses') },
            ...STATUSES.map((s) => ({ value: s, label: t(`rights.statusFilter.${s}`) })),
          ]}
        />
      </div>
    </div>
  );
}
