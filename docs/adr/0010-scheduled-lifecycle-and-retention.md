# ADR-0010: Scheduled publish/expire and retention policies

Status: accepted (Step 1.6b)

## Context
ADR-0009 (Step 1.6a) built the manual side of the asset lifecycle. The spec's remaining Step 1.6 work is what makes two of
its parts happen *without* a person clicking a button: an asset publishing or expiring itself at a chosen time, and a
retention policy eventually moving expired content to cold storage and, later, purging it — unless it is under legal hold.

## Decision
- **Scheduling is a dedicated action, not a PATCH field.** `POST /assets/{id}/schedule` always sets both `publishAt` and
  `expireAt` to exactly what is given, including `null` to cancel one. Folding this into the general asset update, whose
  convention is "a missing field means leave it alone", would have made a schedule impossible to cancel through that
  endpoint. Setting a timer never changes status by itself: the scheduler only acts once the asset is actually in the
  status the timer applies to (`approved` for publish, `published` for expire), so scheduling early is harmless.
- **Retention policy assignment and legal hold are their own actions**, gated by `retention.manage` rather than
  `assets.update`: assigning a policy or placing a hold is a retention decision, not ordinary content editing.
- **The purge step reuses the existing lifecycle table.** `archived --purge--> deleted` was added to `AssetLifecycle`
  alongside submit/approve/publish/etc. (spec A4.2's diagram already showed it); the retention job simply calls the same
  transition machinery a manual "purge" action would.
- **Two scheduled jobs, cross-tenant by design.** `PublishExpireJob` polls every 15 seconds by default
  (`DAM_SCHEDULE_POLL_CRON`), comfortably inside the spec's "fires within 60s of publish_at". `RetentionJob` runs hourly
  (`DAM_RETENTION_POLL_CRON`): archiving and purging have no tight SLA, and every check joins against a policy. Both find
  candidates through the same read-only, cross-tenant pattern the outbox relay already uses (`dam_system`, RLS still on,
  with one additional read-only policy per table) rather than iterating tenants one at a time, then apply each change
  through an ordinary tenant-scoped scope — so the mutation goes through exactly the same RLS, tenant filter, and
  audit/outbox pipeline a user's own request would.
- **A legal hold blocks purge; a policy can override its own hold.** `Asset.LegalHold` is the flag that blocks purge.
  `RetentionPolicy.LegalHoldExempt` is checked *in addition* to the discovery SQL's own filter, inside the same
  transaction that would perform the purge — a hold placed between when the candidate was found and when it is actually
  processed must still block it. Getting this second check to also honour the exemption (not just re-block on any hold)
  was the one real bug this step's tests caught: an early version of the guard would have silently defeated
  `legalHoldExempt` the moment a hold existed, which is exactly the case that flag exists for.
- **Purged files are deleted after the status change commits, never before.** If deleting the underlying objects fails
  (logged, not fatal), the result is an orphaned object behind an asset that is already `deleted` — a safe, cleanable
  failure mode. Deleting first and having the status-change commit fail afterwards would risk the opposite: a record that
  still claims to have content it no longer has.

## Consequences
- `retention_policies` is a new tenant-scoped table; `assets` gains `legal_hold` (the two scheduling and one policy
  reference columns already existed, unused, since Step 1.3).
- `Dam.Scheduler` now carries the same tenant-scoped data and storage access a worker does, plus the read-only system
  connection for cross-tenant discovery — it no longer holds only Quartz wiring.
- Publishing, expiring, archiving and purging automatically all land in the audit log exactly like their manual
  counterparts, since they go through the same `Asset.ChangeStatus`.
