import { expect, test, type Page } from '@playwright/test';
import { uniquePng } from './helpers/fixtures';

/**
 * Opt-in check of the shared-demo kit (deploy/demo/README.md), run against a demo that is already up, normally in --local mode:
 *
 *   DEMO_URL=http://demo.localhost:8088 DEMO_USER=anna.reviewer DEMO_PASSWORD=... pnpm exec playwright test --config tests/e2e/playwright.config.ts --project demo
 *
 * It signs in through the demo's own Keycloak realm, so it exercises the whole same-origin route: console, API, sign-in, presigned uploads to storage
 * and signed delivery links. Without DEMO_URL, DEMO_USER and DEMO_PASSWORD every test here is skipped. Optional: DEV_USER / DEV_PASSWORD (a user of
 * the developer realm) to prove such a user is refused on the demo address.
 */
const { DEMO_URL, DEMO_USER, DEMO_PASSWORD, DEV_USER, DEV_PASSWORD } = process.env;
test.skip(!DEMO_URL || !DEMO_USER || !DEMO_PASSWORD, 'set DEMO_URL, DEMO_USER and DEMO_PASSWORD to run the demo checks');

async function signInAs(page: Page, user: string, password: string) {
  await page.goto('/login');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('Username or email').fill(user);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign In' }).click();
}

test('signs in on the demo address and uses the library end to end', async ({ page, baseURL }) => {
  await signInAs(page, DEMO_USER!, DEMO_PASSWORD!);
  await page.waitForURL(`${baseURL}/`);
  await page.getByRole('button', { name: 'Account menu' }).waitFor();
  expect(await page.evaluate(() => window.isSecureContext)).toBe(true);

  // Dashboard: usage charts are drawn from the seeded usage events.
  await expect(page.getByText('Library at a glance')).toBeVisible();
  await page.waitForLoadState('networkidle');
  await expect(page.getByText(/\d+ uses/).first()).toBeVisible({ timeout: 20_000 });
  await page.screenshot({ path: 'test-results/demo-dashboard.png', fullPage: true });

  // Library: sample assets are there and their thumbnails (signed delivery links) really load.
  await page.goto('/library');
  await expect(page.getByText('Roadster GT on the evening road').first()).toBeVisible({ timeout: 30_000 });
  await expect(async () => {
    const loaded = await page.evaluate(() => [...document.images].filter((i) => i.src.includes('/d/') && i.naturalWidth > 0).length);
    expect(loaded).toBeGreaterThan(3);
  }).toPass({ timeout: 30_000 });
  await page.screenshot({ path: 'test-results/demo-library.png' });

  // Upload a small image: the bytes go straight to storage through a presigned URL on the same address.
  const stamp = Date.now();
  const title = `demo-check-${stamp}`;
  await page.goto('/upload');
  await page.locator('input[type="file"]').setInputFiles({ name: `${title}.png`, mimeType: 'image/png', buffer: uniquePng(stamp) });
  await page.getByRole('button', { name: /^Upload \d+ files?$/ }).click();
  await expect(page.getByRole('link', { name: 'View in library' })).toBeVisible({ timeout: 60_000 });

  // Search finds it (the index is near-real-time, so retry like a person would), and its detail page shows a preview and a working download.
  await expect(async () => {
    await page.goto(`/search?q=${encodeURIComponent(title)}`);
    await expect(page.getByText(title, { exact: true }).first()).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 40_000 });
  await page.getByText(title, { exact: true }).first().click();
  await expect(page.getByRole('heading', { name: title })).toBeVisible();
  await expect(async () => {
    const loaded = await page.evaluate(() => [...document.images].filter((i) => i.src.includes('/d/') && i.naturalWidth > 0).length);
    expect(loaded).toBeGreaterThan(0);
  }).toPass({ timeout: 30_000 });
  await page.screenshot({ path: 'test-results/demo-asset.png' });

  // Previews and downloads of a published sample asset: the image's "open original" link and the audio clip's download button.
  await page.goto('/search?q=Roadster GT on the evening road');
  await page.getByText('Roadster GT on the evening road', { exact: true }).first().click();
  const open = page.getByRole('link', { name: /open in new tab/i }).first();
  await expect(open).toBeVisible({ timeout: 30_000 });
  const originalImage = await page.request.get((await open.getAttribute('href'))!);
  expect(originalImage.status()).toBe(200);
  expect(originalImage.headers()['content-type']).toContain('image/png');
  expect((await originalImage.body()).length).toBeGreaterThan(10_000);

  await page.goto('/search?q=Showroom ambient jingle');
  await page.getByText('Showroom ambient jingle', { exact: true }).first().click();
  const [download] = await Promise.all([page.waitForEvent('download', { timeout: 30_000 }), page.getByRole('link', { name: /download original/i }).first().click()]);
  const path = await download.path();
  expect((await import('node:fs')).statSync(path).size).toBeGreaterThan(10_000);

  // Searching seeded content works too.
  await page.goto('/search?q=Lumen');
  await expect(page.getByText(/Lumen/).first()).toBeVisible({ timeout: 20_000 });

  // Sign out returns to the login page of this same address.
  await page.getByRole('button', { name: 'Account menu' }).click();
  await page.getByText('Sign out', { exact: true }).click();
  await page.waitForURL(`${baseURL}/login`);
});

test('a developer-realm user cannot sign in on the demo address', async ({ page }) => {
  test.skip(!DEV_USER || !DEV_PASSWORD, 'set DEV_USER and DEV_PASSWORD to check this');
  await signInAs(page, DEV_USER!, DEV_PASSWORD!);
  await expect(page.getByText(/invalid username or password/i)).toBeVisible();
});

test('what must not be reachable is not', async ({ request }) => {
  for (const path of [
    '/admin/master/console/', '/admin/realms/master', '/realms/master/.well-known/openid-configuration', '/realms/dam/.well-known/openid-configuration',
    '/healthz', '/readyz', '/metrics', '/openapi/v1.json', '/swagger', '/dam-originals/', '/dam-quarantine', '/wp-login.php',
  ]) {
    expect((await request.get(path)).status(), path).toBe(404);
  }
  expect((await request.get('/api/v1/me')).status()).toBe(401);
  expect((await request.get('/')).status()).toBe(200);
});
