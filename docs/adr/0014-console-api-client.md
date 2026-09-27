# ADR-0014: Console API client, and the library shell (Step 1.8a)

## Status
Accepted

## Context
Step 1.8 ("Console: library, upload, asset detail, metadata editing") is the largest step so far and spans several very
different concerns - split, like every other large step, into sub-steps: 1.8a (this ADR) is the foundation - a real API
client and the first two screens ("folder tree + grid/list") - followed by 1.8b (upload), 1.8c (asset detail), 1.8d
(bulk edit, collections, saved searches UI) and 1.8e (keyboard shortcuts, the mobile-responsive pass, Playwright e2e).

Before this step, the console (Step 0.7) had a shell (auth, i18n, routing, error boundary) and exactly one API call
(`GET /me`) through a hand-rolled `apiFetch` wrapper. `@dam/sdk-js` (spec A9/A10: "TS client for web/mobile teams") was
an empty stub. Every business screen was greenfield.

## Decisions

**`@dam/sdk-js` becomes the real typed API client, generated from `docs/api/openapi.json`.** `openapi-typescript`
generates `schema.ts` (the `paths`/`components` types) and `openapi-fetch` (from the same maintainers) provides the
runtime client - `client.GET('/api/v1/folders', ...)` is fully typed against the live backend contract with zero
hand-written per-endpoint wrapper functions. This fulfils the SDK package's own stated purpose instead of building a
parallel, redundant client inside the console. `pnpm --filter @dam/sdk-js generate` regenerates `schema.ts`; this
should be run (and its diff committed, exactly like `docs/api/openapi.json` itself already is) whenever the backend's
API surface changes - a natural addition to the existing `make openapi` step in a later cleanup, not done here to keep
this step's diff scoped to the frontend.

**The generated client needs an absolute `baseUrl`.** `openapi-fetch` builds a real `Request` object internally
(`new Request(url, ...)`), and `Request`'s URL parsing - unlike a bare `fetch(url)` call in a browser - does not fall
back to `document.baseURI` for a relative path the way one might expect. `createApiClient` defaults to
`window.location.origin`, which is correct in every environment this runs in (Vite's dev-server proxy and the
production nginx ingress are both same-origin deployments) and sidesteps the issue entirely rather than special-casing
it per environment.

**`ApiError` and the "throw on non-2xx" contract are preserved across the transport change.** `openapi-fetch` returns
`{ data, error, response }` instead of throwing; `unwrap()` converts that back into the exact `ApiError` shape
`apiFetch` used to throw, so `useMe()` (and every future query hook) keeps the same TanStack Query integration and
error-rendering code the console already had - only the transport underneath changed. The console's own
`api/client.ts` still exists and still exports `ApiError`, now re-exported from `@dam/sdk-js`, so no import path at any
existing call site needed to change.

**The folder tree is built client-side from the flat list, not from `?tree=true`.** `GET /folders?tree=true` returns
`FolderNodeDto[]` at runtime, but its OpenAPI annotation only documents the flat `FolderDto[]` shape (a pre-existing
backend documentation gap - flagged as a follow-up task, not fixed here to keep this step's diff frontend-only).
Fetching the flat, correctly-typed list and building the ui-kit `TreeNode[]` tree from `parentId` client-side
sidesteps the mismatch entirely, and is barely more code than consuming a tree shape directly would have been.

**The library screen browses folders through `GET /assets`, not `POST /search`.** Step 1.7's search API has no
folder-prefix filter - only exact-field `terms` matching - so "everything in this folder or below" has no equivalent
query today; `GET /assets?folderId=&includeSubfolders=true` (the very listing ADR-0011/0012 called "interim" and left
in place for exactly this reason) still does. `POST /search` is reserved for the global search bar 1.8d/e will add.
This is a deliberate, temporary division of labour between the two APIs, not a rejection of search - folder-scoped
browsing can move to search once it grows a folder filter, and nothing about the screen's own code depends on which
endpoint it calls.

**Real image thumbnails are deferred.** `ThumbnailGrid`'s `imageUrl` is left unset for now (rendering ui-kit's own
placeholder box) rather than requesting a signed delivery link (`POST /assets/{id}/links`) per visible asset - fetching
one link per grid item is real, unavoidable N+1 cost against the delivery origin (ADR-0006) that deserves a considered
batching or caching strategy, not a rushed one bolted onto the first screen. Grid rows already carry title/type/status
today; wiring real thumbnails is a fast-follow, not a gap that blocks anything else in 1.8.

## A test-writing note, not a design decision
Building the folder-selection test surfaced a genuine ARIA gotcha worth recording: an expanded `role="treeitem"`'s
*computed accessible name* recursively includes its descendants' text (accessible-name computation is not scoped to
one row), so a parent node's name can literally contain a child's label once expanded (`"Brand"` becomes
`"Brand Roadster"`). A substring/regex `name` match in a `getByRole` query can then match both the parent and the
child. Queries need an exact string (or to match on the element's own rendered text, e.g. `getByText`) once a tree is
expanded - `Tree.tsx` itself needed no change; the fix was entirely in the test.

## Consequences
- Every future console screen fetches through `useApiClient()`/`unwrap()` rather than raw `fetch`.
- A backend documentation gap (`GET /folders?tree=true`'s response type) is flagged for a follow-up fix rather than
  fixed here, to keep this step's diff frontend-only; the console does not depend on it being fixed.
- The two remaining spec Accept criteria for the *whole* of Step 1.8 - the upload → edit → publish → search Playwright
  flow, bulk-edit-50-assets timing, and mobile-responsive at 390px - are unaddressed by this sub-step; they land with
  1.8b-e as each screen they depend on is built.
