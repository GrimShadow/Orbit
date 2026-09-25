import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import en from './en.json';
import hi from './hi.json';

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
  resources: { en: { translation: en }, hi: { translation: hi } },
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
