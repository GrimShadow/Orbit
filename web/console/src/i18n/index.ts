import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import en from './en.json';
import hi from './hi.json';

/** Feature-level translation fragments (`src/i18n/fragments/<area>.<lang>.json`) are merged over the base files at
 * start-up, so an area can add its own keys without everyone editing one shared JSON file. A fragment's top-level
 * keys are namespaces; a key present in both wins from the fragment. */
type Dict = Record<string, unknown>;
const fragments = import.meta.glob('./fragments/*.json', { eager: true, import: 'default' }) as Record<string, Dict>;
function deepMerge(base: Dict, extra: Dict): Dict {
  const out: Dict = { ...base };
  for (const [k, v] of Object.entries(extra)) {
    const cur = out[k];
    out[k] =
      cur && typeof cur === 'object' && !Array.isArray(cur) && v && typeof v === 'object' && !Array.isArray(v)
        ? deepMerge(cur as Dict, v as Dict)
        : v;
  }
  return out;
}
function withFragments(lang: string, base: Dict): Dict {
  return Object.entries(fragments)
    .filter(([path]) => path.endsWith(`.${lang}.json`))
    .sort(([a], [b]) => a.localeCompare(b))
    .reduce((acc, [, frag]) => deepMerge(acc, frag), base);
}

export const LANGUAGES = [
  { value: 'en', label: 'English' },
  { value: 'hi', label: 'हिन्दी' },
];
const KEY = 'dam.lang';

function initial(): string {
  try {
    const saved = localStorage.getItem(KEY);
    if (saved && LANGUAGES.some((l) => l.value === saved)) return saved;
  } catch {
    /* storage unavailable */
  }
  return 'en';
}

void i18n.use(initReactI18next).init({
  resources: { en: { translation: withFragments('en', en) }, hi: { translation: withFragments('hi', hi) } },
  lng: initial(),
  fallbackLng: 'en',
  interpolation: { escapeValue: false }, // React already escapes
});

export function setLanguage(lang: string) {
  void i18n.changeLanguage(lang);
  document.documentElement.lang = lang;
  try {
    localStorage.setItem(KEY, lang);
  } catch {
    /* ignore */
  }
}
document.documentElement.lang = i18n.language;

export default i18n;
