// Kept as the console's own import path (`../api/client`) so existing call sites don't need to change just because the
// transport moved to the generated `@dam/sdk-js` client - see `apiClient.ts` for the actual client.
export { ApiError } from '@dam/sdk-js';
