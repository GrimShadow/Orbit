# Orbit console e2e (Playwright)

Spec Step 1.8's Playwright suite, run against the **real** dev stack - not mocked, not a Testcontainers-style
ephemeral environment. Unlike every other test suite in this repo, this one needs live services already running
before you start it.

## Bring the stack up

```bash
make up-search       # Postgres, Keycloak, SeaweedFS, RabbitMQ, Redis, OpenSearch
make up-scan         # + ClamAV - the ingest pipeline's scan step needs it, or every upload hangs at "hashed"
make migrate          # idempotent - safe to run even if already up to date
make run-api          # in its own terminal
make run-worker       # in its own terminal (ingest pipeline + search indexing)
pnpm --filter @dam/console dev   # in its own terminal, port 5173
```

The API, Worker and console dev server all need to keep running for the whole test run. `make run-delivery` and
`make run-scheduler` are not required - nothing in this suite previews a rendition or waits on a scheduled job.

**If the API or Worker have been running for a long time already** (a person's own long-lived dev session, not a
fresh start for this suite), restart both before trusting a failure - a long-lived `dotnet run` process only has
whatever consumers/routes existed in the code when IT started, not what's in the checkout now. Symptoms this suite
hit once: `POST /search` 404ing (a stale Api), or every upload sitting at "hashed" forever because the Worker's
ingest-pipeline consumers (scan/sniff/extract/promote/search-index) were never registered in that instance (check
`docker compose -f deploy/docker-compose.yml exec rabbitmq rabbitmqctl list_queues` or the management UI at
`:25672` - `dam.q.search-index` and friends should exist with a consumer attached, not just `dam.q.hash-upload`).

**If OpenSearch refuses to create its index** (`x-error` in `dam.q.search-index.dlq`, or `create_index_exception:
[FORBIDDEN/10/cluster create-index blocked (api)]`), the cluster has a stale `create_index` block from an earlier
disk-watermark trip - clear it with `curl -X PUT localhost:19200/_cluster/settings -d
'{"persistent":{"cluster.blocks.create_index":null}}'` after freeing space (`docker system df` /
`docker image prune -a` / `docker builder prune` - never `docker volume prune`, that risks real data).

## Run it

```bash
pnpm exec playwright install chromium   # once, downloads the browser binary
pnpm e2e
```

Signs in as the realm's dev Admin user (`deploy/keycloak/realm-dam.json` - throwaway, local-stack-only credentials,
never real ones; see `tests/e2e/helpers/auth.ts`).

## What it covers

- **`library-workflow.spec.ts`** (desktop viewport): the spec's own accept criterion, end to end -
  upload → edit metadata → publish → appears in search with the type/status facets the console actually renders
  (ADR-0018); then a 50-asset bulk edit (ADR-0017), timing only the bulk mutation's round-trip (< 5s).
- **`mobile-responsive.spec.ts`** (390px viewport): every primary screen loads with no page-level horizontal scroll,
  and the nav / keyboard-shortcuts help (ADR-0019) both still work at that width.

## Notes on data

Every run creates its own uniquely-named test assets (`e2e-<timestamp>...`) rather than assuming or resetting any
existing tenant data, so it is safe to run repeatedly against a persistent dev stack without cleanup - it never reads
or depends on anything another run (or a person's own manual testing) left behind. A CI job that provisions a fresh
stack per run does not need to do anything differently.
