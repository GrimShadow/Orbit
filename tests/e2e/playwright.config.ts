import { defineConfig, devices } from '@playwright/test';

/** Playwright e2e against the REAL dev stack (spec Step 1.8: "Playwright e2e ... at repo-root tests/e2e/, not under
 * web/") - `docker compose` (Postgres/Keycloak/SeaweedFS/RabbitMQ/OpenSearch), the API, Worker and console dev server
 * must already be running (see tests/e2e/README.md); this suite does not start them, since CI and local runs bring
 * them up differently. */
export default defineConfig({
  testDir: '.',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false, // shares one tenant's data; parallel specs would race each other's asset counts/searches
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: process.env.DAM_CONSOLE_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'desktop', testMatch: /library-workflow\.spec\.ts/, use: { ...devices['Desktop Chrome'] } },
    {
      name: 'mobile-390',
      testMatch: /mobile-responsive\.spec\.ts/,
      use: { ...devices['Desktop Chrome'], viewport: { width: 390, height: 844 } },
    },
    // Opt-in: only does anything when DEMO_URL, DEMO_USER and DEMO_PASSWORD are set (deploy/demo/README.md).
    { name: 'demo', testMatch: /demo\.spec\.ts/, use: { ...devices['Desktop Chrome'], baseURL: process.env.DEMO_URL ?? 'http://demo.localhost:8088' } },
  ],
});
