import { createContext, useContext, useMemo, type ReactNode } from 'react';

export interface TranslationScopeValue {
  assetId: string;
  assetType: string;
  /** The saved metadata document: translating works from what is saved, so the editor compares it with the draft. */
  saved: Record<string, unknown>;
}

const Scope = createContext<TranslationScopeValue | null>(null);

/** Tells the multilingual field editors which asset they belong to, so they can offer AI translation and show its
 * suggestions. Without it (the upload form, which has no asset yet) the editors are plain side-by-side text boxes. */
export function TranslationScope({ assetId, assetType, saved, children }: TranslationScopeValue & { children: ReactNode }) {
  const value = useMemo(() => ({ assetId, assetType, saved }), [assetId, assetType, saved]);
  return <Scope.Provider value={value}>{children}</Scope.Provider>;
}

export const useTranslationScope = () => useContext(Scope);
