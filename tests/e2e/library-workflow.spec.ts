import { expect, type Page } from '@playwright/test';
import { test } from '@playwright/test';
import { signIn } from './helpers/auth';
import { uniquePng } from './helpers/fixtures';

/** Finds an asset by a unique text query and opens its detail page - used instead of the library grid's default
 * (unfiltered, paginated) view, which is not a reliable way to locate one specific asset once a tenant has more than
 * a page's worth of content. OpenSearch's near-real-time refresh (~1s) means the very first query after a write can
 * still miss it - `toPass` retries the whole search, not just the final assertion, exactly like a person hitting
 * refresh. */
async function openFromSearch(page: Page, query: string, title: string) {
  await expect(async () => {
    await page.goto(`/search?q=${encodeURIComponent(query)}`);
    await expect(page.getByText(title, { exact: true })).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 20_000 });
  await page.getByText(title, { exact: true }).click();
  await expect(page.getByRole('heading', { name: title })).toBeVisible();
}

/** Spec Step 1.8 accept criterion: "upload → edit metadata → publish → appears in search with correct facets". Runs
 * against the real dev stack (see README.md) - a genuine end-to-end pass through the OIDC login redirect, the direct-
 * to-storage upload, the ingest pipeline, and the search index, not a mocked one. */
test('uploads an asset, edits its metadata, publishes it, and it appears in search', async ({ page }) => {
  await signIn(page);

  const seed = Date.now();
  const runId = `e2e-${seed}`;
  const fileName = `${runId}.png`;
  const title = runId; // the asset's title is the filename minus its extension (Uploads.cs: Path.GetFileNameWithoutExtension)

  await page.goto('/upload');
  await page.locator('input[type="file"]').setInputFiles({ name: fileName, mimeType: 'image/png', buffer: uniquePng(seed) });
  await expect(page.getByText(fileName)).toBeVisible();
  await page.getByRole('button', { name: /^Upload \d+ files?$/ }).click();
  await expect(page.getByRole('link', { name: 'View in library' })).toBeVisible({ timeout: 30_000 });

  // Find the freshly-uploaded asset via search and confirm it landed as a draft image asset before editing it.
  await openFromSearch(page, runId, title);
  await expect(page.locator('.dam-badge', { hasText: 'Draft' }).first()).toBeVisible();
  await expect(page.locator('.dam-badge', { hasText: 'Image' }).first()).toBeVisible();

  // Edit metadata: the "core" template's `credit` field on the image schema (a plain text field, spec Step 1.8:
  // "metadata form generated from schema").
  const credit = `Credit ${runId}`;
  await page.getByRole('tab', { name: 'Metadata' }).click();
  await page.getByLabel('Credit').fill(credit);
  await page.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByText('Changes saved')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save changes' })).toBeHidden(); // the unsaved-changes bar goes away

  // Walk the lifecycle to published (spec Step 1.6's state machine: draft -> in_review -> approved -> published).
  await page.getByRole('button', { name: 'Submit for review' }).click();
  await expect(page.locator('.dam-badge', { hasText: 'In review' }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Approve' }).click();
  await expect(page.locator('.dam-badge', { hasText: 'Approved' }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Publish' }).click();
  await expect(page.locator('.dam-badge', { hasText: 'Published' }).first()).toBeVisible();

  // Appears in search (POST /search, spec Step 1.7) with the type/status facets the console actually renders
  // (ADR-0018), now reflecting the published status.
  await expect(async () => {
    await page.goto(`/search?q=${encodeURIComponent(runId)}`);
    await expect(page.getByText(title, { exact: true })).toBeVisible({ timeout: 2_000 });
    await expect(page.getByRole('group', { name: 'Status' })).toContainText('Published', { timeout: 2_000 });
  }).toPass({ timeout: 20_000 });

  await expect(page.getByRole('group', { name: 'Type' })).toContainText('Image');
  // click + web-first assertion (not `.check()`): the facet list re-renders as the filtered results arrive.
  await page.getByRole('checkbox', { name: /Image/ }).click();
  await expect(page.getByRole('checkbox', { name: /Image/ })).toBeChecked();
  await expect(page.getByText(title, { exact: true })).toBeVisible();
});

/** Spec Step 1.8 accept criterion: "bulk edit of 50 assets < 5 s round-trip". Uploads 50 uniquely-content-ed images of
 * its own (so each one hashes differently and is genuinely ingested rather than folded into "duplicate" by spec Step
 * 1.4's content-hash dedup, and so the count is exact regardless of whatever else already exists in this tenant),
 * selects them by filtering to type=image/status=draft (spec 1.8d's bulk edit bar), and times only the bulk mutation
 * itself - selecting rows in the UI is not part of the "round-trip" the spec means. */
test('bulk edits 50 assets in under 5 seconds', async ({ page }) => {
  test.setTimeout(120_000);
  await signIn(page);

  const batchSeed = Date.now();
  const runId = `e2e-bulk-${batchSeed}`;
  const count = 50;
  const files = Array.from({ length: count }, (_, i) => ({
    name: `${runId}-${i}.png`,
    mimeType: 'image/png',
    buffer: uniquePng(batchSeed + i),
  }));

  await page.goto('/upload');
  await page.locator('input[type="file"]').setInputFiles(files);
  await page.getByRole('region', { name: 'Destination' }).getByRole('textbox', { name: 'Tags' }).fill(runId);
  await page.keyboard.press('Enter'); // a chip, not free text
  await page.getByRole('button', { name: /^Upload \d+ files?$/ }).click();
  await expect(page.getByRole('link', { name: 'View in library' })).toBeVisible({ timeout: 60_000 });

  await page.goto('/library?type=image&status=draft');
  const selectAll = page.getByRole('checkbox', { name: `Select all ${count} assets on this page` });
  await expect(selectAll).toBeVisible({ timeout: 15_000 });
  await selectAll.check();
  await expect(page.getByRole('region', { name: `${count} assets selected` })).toBeVisible();

  await page.getByRole('button', { name: 'Tags' }).click(); // opens the tag panel inside the floating bulk bar
  await page.getByRole('textbox', { name: 'Tags' }).fill('bulk-edited');
  await page.keyboard.press('Enter');
  const start = Date.now();
  await page.getByRole('button', { name: 'Add tags' }).click();
  await expect(page.getByText(`${count} assets updated`)).toBeVisible({ timeout: 10_000 });
  const elapsedMs = Date.now() - start;

  expect(elapsedMs).toBeLessThan(5_000);
});
