# ADR-0038: Rendition jobs no longer hold the audit-chain lock

Status: accepted. Fixes a defect found while building Step 2.1 (noted in ADR-0035). Changes none of D1-D15.

## Context

While a video transcoded, every audited write for the same tenant (any API command: create a folder, edit an asset, change a setting) waited, for as long as
the transcode took (about 30 s for a short clip on a laptop, minutes for a long one).

Cause, verified (not assumed):

- `audit_log_chain` (the `BEFORE INSERT` trigger on `audit_log`, `SecuritySql.AuditChain`) takes `pg_advisory_xact_lock(hashtextextended(tenant_id, 0))` so that
  `chain_no` is gap-free per tenant. An `xact` advisory lock is released only when the transaction ends.
- `ConsumerHost` opens one transaction per delivery (to record the message and commit the handler's effects exactly once).
- `RenditionConsumer` ran `RenditionService.GenerateEagerAsync` inside it. Every rendition it stores calls `db.SaveChangesAsync` with a `RenditionReady` audit row, and
  for a video, PDF or Office document the slow work (ffmpeg transcodes, LibreOffice conversion, poppler page rasterising) comes after the first of those saves. So
  the lock taken by the poster still's audit row was held through the whole transcode.
- Evidence, from a reproduction (`RenditionLockTests`: the real worker with an ffmpeg that sleeps 8 s on its first transcode, then `POST /folders` while it sleeps):
  `pg_stat_activity` showed the worker's session `idle in transaction` (its last statement a `RELEASE SAVEPOINT` after the poster rendition was saved) and the API's
  `INSERT INTO audit_log` in `wait_event = Lock/advisory`; `pg_locks` showed the same advisory key granted to the worker's pid and not granted to the API's. The
  API call took 9.0 s.

Other consumers do not have this shape: the ingest consumers only add audit rows to the context and flush them once, at commit (a lock held for milliseconds);
the AI enrichment consumer avoids the audit chain altogether (ADR-0035) and tracks progress in transactions of its own. The rendition consumer is the only one that
saves an audited row early and then keeps working. (The delivery origin renders on demand with no ambient transaction at all, so it never had the problem.)

## Decision

1. **A consumer can ask the host not to wrap its handler in a transaction.** `EventConsumer.RunsInHostTransaction` (default `true`, so nothing existing changes).
   When it is `false`, `ConsumerHost` does this instead:
   1. checks `processed_messages` for the (consumer, message) pair and acks without running anything if it is there (a redelivery after the work was finished);
   2. runs the handler with no transaction open, so each `SaveChanges` the handler makes commits by itself and holds the audit-chain lock only for that statement;
   3. in one short transaction records the message (`INSERT ... ON CONFLICT DO NOTHING`), persists the events the handler's aggregates raised, and commits; then
      `AfterCommitAsync`, then ack.

   Failure handling is unchanged: an exception goes to the next retry tier or the DLQ; there is simply no transaction to roll back.
2. **`RenditionConsumer` opts out**, and **`RenditionService.GenerateEagerAsync` is made idempotent**: a profile that already has a ready rendition whose object is in the
   store is not made again (images, video stills and transcodes, PDF stills and pages). The ffmpeg, LibreOffice and poppler work therefore happens with no transaction
   and no lock, each result is stored (object, `renditions` row, `RenditionReady` audit and outbox rows) in one short `SaveChanges`, and a retry or redelivery after a
   failure carries on from where the first attempt stopped instead of repeating finished work or announcing a rendition twice.
3. **Not chosen:** giving the heavy work its own scope and transaction per rendition (the `AiRunTracker` pattern) keeps the host transaction open and idle for the
   whole job, still pinning a connection and a row; writing `RenditionReady` straight to the outbox without an audit row (as the AI consumer does for
   `asset.ai.enriched`) would drop the audit trail of renditions, which is deliberate here. Splitting the audit chain lock itself (per entity, or deferred to commit)
   is a far larger change to a security feature and is not needed once nothing holds it for long.

## Exactly-once, honestly

The default path still gives "effects commit exactly once". The opt-out path gives **at-least-once with an idempotent handler**: a message whose handler finished but
whose processed-message row did not commit (the worker died in between) runs the handler again, and two overlapping deliveries of one message (a redelivery after a
lost connection while the first is still working) can both run it; only the first to finish records the message. That is safe only for a handler that can be repeated,
which is why the flag is opt-in, documented on the property, and why rendition generation is made idempotent in the same change. Renditions are also deterministic by
key (`Rendition.KeyFor`) and the store call overwrites, so a duplicate run writes the same object; the one residual effect of an overlap is a repeated `RenditionReady`
event (an extra audit row and webhook delivery, no change of state).

A consequence visible to users: renditions now appear one by one while a job runs, not all at once when it ends. Nothing relied on the all-at-once behaviour (the
console and the API list whatever is ready; the delivery origin renders on demand anything that is not).

## Consequences

- An audited write for a tenant no longer waits for that tenant's transcodes, conversions or page rasterising. `RenditionLockTests` fails on the old behaviour (9.0 s
  against a 3 s bound) and passes now; `ConsumerTransactionTests` pins the host behaviour (the default still holds the lock, the opt-out does not, a finished message is
  not handled again, a failed attempt is retried and recorded once).
- Any future long-running consumer that saves audited rows early must set `RunsInHostTransaction` to `false` and be idempotent, or do its heavy work before its first
  audited save. The property's comment says so.
- A job killed mid-way leaves some renditions in place (before, the rollback removed the rows, leaving orphan objects). The retry finishes the rest.
