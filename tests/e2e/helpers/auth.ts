import type { Page } from '@playwright/test';

/** Dev-only Keycloak realm credentials (`deploy/keycloak/realm-dam.json`) - throwaway, local-stack-only, never real. */
export const DEV_ADMIN = { email: 'admin@dam.local', password: 'admin' };

/** Drives the real OIDC redirect flow (console -> Keycloak's hosted login page -> back to /auth/callback) exactly as
 * a person would - there is no test-only shortcut for this since the console's `AuthProvider` always redirects to
 * Keycloak (spec ADR: authorization-code + PKCE, Step 0.7). */
export async function signIn(page: Page, credentials = DEV_ADMIN) {
  await page.goto('/login');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByLabel('Username or email').fill(credentials.email);
  await page.getByLabel('Password', { exact: true }).fill(credentials.password);
  await page.getByRole('button', { name: 'Sign In' }).click();
  await page.waitForURL('**/');
  await page.getByRole('button', { name: 'Account menu' }).waitFor();
}
