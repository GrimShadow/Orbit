import { useMe } from '../../api/me';
import { useAiCapabilities } from '../ai/aiApi';

/** Why translating with AI is or is not on offer right now. */
export type TranslateState =
  /** Capabilities are still loading. */
  | 'loading'
  /** Organisation-level AI is off, or the capabilities could not be read. */
  | 'off'
  /** AI is on but the provider cannot translate (the self-hosted provider cannot; ADR-0036). */
  | 'unsupported'
  | 'ready';

export interface TranslateSupport {
  state: TranslateState;
  /** May start a translation: `assets.update`. */
  canEdit: boolean;
  /** May open the AI settings (`ai.manage`), so the hint can link there. */
  canManageAi: boolean;
}

export function useTranslateSupport(): TranslateSupport {
  const caps = useAiCapabilities();
  const permissions = useMe().data?.permissions ?? [];
  let state: TranslateState;
  if (caps.isPending) state = 'loading';
  else if (!caps.data?.enabled) state = 'off';
  else if (!caps.data.translate) state = 'unsupported';
  else state = 'ready';
  return { state, canEdit: permissions.includes('assets.update'), canManageAi: permissions.includes('ai.manage') };
}
