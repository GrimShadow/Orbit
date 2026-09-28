# ADR-0020: Console Playwright e2e (Step 1.8g)

## Status
Accepted

## Context
Step 1.8f split "Playwright e2e against the real stack" out of the console-only "keyboard shortcuts + mobile-responsive"
work (ADR-0019), since it needs the full `docker compose` stack, the API, the Worker and the console dev server
actually running - a materially different kind of work than every other Step 1.8 sub-step. This step is that suite:
`tests/e2e/` at the repo root (per the spec's own placement, not under `web/`), covering the spec's exact accept
criterion - "upload → edit metadata → publish → appears in search with correct facets; bulk edit of 50 assets < 5s
round-trip; mobile-responsive at 390 px width."

Running this suite for real (not just writing it) surfaced three genuine, pre-existing bugs unrelated to Step 1.8's
own frontend work - this ADR documents both the suite's own design and those fixes together, since the suite is what
found them.

## Decisions

**Every fixture image has genuinely unique bytes, not just a unique filename.** `helpers/fixtures.ts` builds a real,
minimal 1x1 PNG per call (manual IHDR/IDAT/IEND chunks, `zlib.deflateSync` for the pixel data) whose single pixel
encodes a seed - the naive first attempt (one shared hardcoded buffer, many filenames) surfaced immediately: the
ingest pipeline's content-hash duplicate detection (spec Step 1.4) correctly folded every upload after the first into
a `duplicate`, which silently broke a "bulk edit 50 assets" test into "bulk edit 1 real asset + 49 duplicates."

**Assets are found via search, not the library grid's default view.** The first draft of the single-asset test opened
its upload from `/library`'s default (unfiltered, paginated) grid - flaky and wrong in a tenant that already has more
than a page's worth of content, exactly the persistent dev stack this suite runs against. Every lookup goes through
`/search` by a unique run-id query instead, wrapped in `expect(...).toPass()` to absorb OpenSearch's near-real-time
refresh (~1s) as a real retry loop, the same way a person hitting refresh would.

**Data is additive and self-isolating, never assumed-empty or reset.** Every run generates its own uniquely-seeded,
uniquely-named batch (`e2e-<timestamp>...`) and filters by exactly that when it needs to select "its own" 50 assets
(type=image + status=draft, immediately after uploading them) - it never assumes the tenant starts empty and never
deletes anything. This makes the suite safe to run repeatedly against a shared, persistent dev stack (this session's
own verification run did so many times over) without any cleanup step, which a fresh-per-run CI stack would not need
either.

## Bugs found by actually running this suite (not by inspection)

**`box-sizing: border-box` was missing from the entire design system.** Nothing in `ui-kit`'s `styles.css` ever set
it, so any element combining a computed width with padding rendered wider than its container in the default
content-box model - the console's own `.dam-header` overflowed the viewport by exactly its own padding at 390px
width. Added as a universal `*, *::before, *::after` rule; this is the kind of gap a CSS-only audit (ADR-0019) is
prone to miss and an actual rendered-viewport check catches immediately.

**`.dam-nav` had no `flex-wrap`.** `.dam-header` itself already wrapped as a whole (ADR-0019's audit had confirmed
that), but the nav link list inside it was still a single non-wrapping flex line - invisible until Step 1.8e's two
new links (Search, Collections) pushed six items past 390px width in one row. Fixed alongside the `box-sizing` gap.

**The long-running local `Dam.Api` and `Dam.Worker` dev processes were stale relative to the checked-out code** - `POST
/search` 404'd entirely, and the Worker had never registered the ingest pipeline's later consumers (scan, sniff,
extract, promote, search-index; only `hash-upload` and `system-ping` existed as real RabbitMQ queues). This is not a
code bug - restarting both (`make run-api`, `make run-worker`) fixed it immediately - but it is worth recording:
`tests/e2e/README.md` says explicitly that these processes must be running the current build, not just "running",
since a long-lived dev process silently drifting behind the checkout it's supposed to be serving is exactly this
suite's job to catch (and exactly what it caught).

**OpenSearch had a stale `cluster.blocks.create_index` flag.** The Docker Desktop VM's disk had filled to 96% (a
recurrence of the same issue noted after Step 0.8 - `docker image prune`/`docker builder prune` reclaimed the space),
which had at some point tripped OpenSearch's automatic index-creation block; it does not clear itself once disk space
is freed and had to be reset explicitly (`PUT _cluster/settings`). Environment-only, not a code fix - not something
this ADR's code changes address, but worth the same record for whoever hits it next.

## Consequences
- Step 1.8 (the console) is now fully complete: 1.8a through 1.8g, spec accept criterion included.
- `tests/e2e/README.md` documents the exact bring-up sequence and what "keep the stack current" means in light of the
  staleness bug above - a future Step 1.8 contributor's e2e failures should check that first, not assume a new code
  regression.
- The `box-sizing` and `.dam-nav` fixes apply retroactively to every console screen, not just the ones this suite
  directly asserts on.
