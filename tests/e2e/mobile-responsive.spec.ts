import { expect, test } from '@playwright/test';
import { signIn } from './helpers/auth';

/** Spec Step 1.8 accept criterion: "mobile-responsive at 390 px width" - runs only under the `mobile-390` project
 * (see playwright.config.ts), which sets that exact viewport. The real, automated check ADR-0019 deferred from
 * Step 1.8f's CSS-only audit: every primary screen must fit without forcing the PAGE itself to scroll horizontally
 * (a component scrolling internally, like the library's `Table`, is fine and expected). */
const PAGES = ['/', '/library', '/search', '/collections', '/upload', '/me'];

test('every primary screen fits at 390px wide with no page-level horizontal scroll', async ({ page }) => {
  await signIn(page);

  for (const path of PAGES) {
    await page.goto(path);
    await expect(page.getByRole('main')).toBeVisible();
    const { scrollWidth, clientWidth } = await page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth,
      clientWidth: document.documentElement.clientWidth,
    }));
    expect(scrollWidth, `${path} should not force horizontal page scroll at 390px`).toBeLessThanOrEqual(clientWidth + 1);
  }
});

test('the primary nav and keyboard-shortcuts help stay usable at 390px', async ({ page }) => {
  await signIn(page);
  await page.goto('/library');

  const tabBar = page.getByRole('navigation', { name: 'Sections' });
  await expect(tabBar).toBeVisible();
  await tabBar.getByRole('link', { name: 'Search' }).click();
  await expect(page.getByRole('heading', { name: 'Search' })).toBeVisible();

  await page.getByRole('button', { name: 'Keyboard shortcuts' }).click();
  await expect(page.getByRole('dialog', { name: 'Keyboard shortcuts' })).toBeVisible();
});
