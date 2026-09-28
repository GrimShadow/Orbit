import type { ComponentType } from 'react';
import { Archive, ArrowUUpLeft, CheckCircle, Clock, Megaphone, PaperPlaneTilt, XCircle, type IconProps } from '../../ui';

/** One icon per lifecycle action (`./lifecycle.ts`'s vocabulary) - shared between the asset detail page's own
 * lifecycle buttons and the library's bulk-edit bar so the same action always reads the same way in both places. */
const ICONS: Record<string, ComponentType<IconProps>> = {
  submit: PaperPlaneTilt,
  approve: CheckCircle,
  reject: XCircle,
  publish: Megaphone,
  withdraw: ArrowUUpLeft,
  expire: Clock,
  archive: Archive,
};

export function ActionIcon({ action, ...rest }: { action: string } & IconProps) {
  const Icon = ICONS[action];
  return Icon ? <Icon {...rest} /> : null;
}
