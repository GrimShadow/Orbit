import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { setLanguage } from '../i18n';
import { ThemeSwitcher } from './ThemeSwitcher';
import { applyTheme, readTheme, setTheme } from './theme';

beforeEach(() => {
  localStorage.clear();
  setTheme('system');
  setLanguage('en');
});
afterEach(() => {
  cleanup();
  document.documentElement.removeAttribute('data-theme');
});

describe('theme', () => {
  it('defaults to following the system and leaves data-theme off', () => {
    expect(readTheme()).toBe('system');
    applyTheme(readTheme());
    expect(document.documentElement).not.toHaveAttribute('data-theme');
  });

  it('pins light or dark on <html>, remembers the choice, and returns to the system on request', async () => {
    render(<ThemeSwitcher />);
    expect(screen.getByRole('button', { name: 'Match system' })).toHaveAttribute('aria-pressed', 'true');

    await userEvent.click(screen.getByRole('button', { name: 'Dark' }));
    expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
    expect(localStorage.getItem('dam.theme')).toBe('dark');
    expect(screen.getByRole('button', { name: 'Dark' })).toHaveAttribute('aria-pressed', 'true');

    await userEvent.click(screen.getByRole('button', { name: 'Light' }));
    expect(document.documentElement).toHaveAttribute('data-theme', 'light');
    expect(readTheme()).toBe('light');

    await userEvent.click(screen.getByRole('button', { name: 'Match system' }));
    expect(document.documentElement).not.toHaveAttribute('data-theme');
    expect(localStorage.getItem('dam.theme')).toBeNull();
  });

  it('ignores a corrupt stored value', () => {
    localStorage.setItem('dam.theme', 'purple');
    expect(readTheme()).toBe('system');
  });
});
