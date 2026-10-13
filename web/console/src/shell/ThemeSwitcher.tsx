import { useTranslation } from 'react-i18next';
import { Desktop, Moon, Sun } from '../ui';
import { useTheme, type ThemePreference } from './theme';

const OPTIONS: { value: ThemePreference; Icon: typeof Sun }[] = [
  { value: 'light', Icon: Sun },
  { value: 'dark', Icon: Moon },
  { value: 'system', Icon: Desktop },
];

/** Light / Dark / System - a small segmented control in the header. Remembered per browser. */
export function ThemeSwitcher() {
  const { t } = useTranslation();
  const [theme, setTheme] = useTheme();
  return (
    <div className="dam-theme" role="group" aria-label={t('theme.label')}>
      {OPTIONS.map(({ value, Icon }) => (
        <button
          key={value}
          type="button"
          className="dam-theme__btn"
          aria-pressed={theme === value}
          aria-label={t(`theme.${value}`)}
          title={t(`theme.${value}`)}
          onClick={() => setTheme(value)}
        >
          <Icon size={16} weight={theme === value ? 'fill' : 'bold'} aria-hidden="true" />
        </button>
      ))}
    </div>
  );
}
