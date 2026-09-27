# ADR-0009: Lifecycle transitions, version restore and version diff

Status: accepted (Step 1.6a)

## Context
Assets already carry a `Status` field and every status the spec's lifecycle diagram (A4.2) names, but nothing enforced the
diagram itself: any status could be set to any other status by calling the one generic `Asset.ChangeStatus`. Versions could be
listed and read, but an old one could never be made current again, and there was no way to see what changed between two
versions. This step builds the actual state machine and the two missing versioning features, and splits the rest of spec
Step 1.6 (scheduled publish/expire and retention policies) into Step 1.6b, since combining a manual state machine with a
scheduler and a whole policy engine would have made one step too large to review well.

## Decision
- **The transition table is a pure, static lookup** (`AssetLifecycle.TryGetTarget`), independent of any command or handler, so
  the whole state machine is unit-tested directly with no database. A command handler (`TransitionAssetHandler`) resolves the
  legal target status here first (409 if the action doesn't apply to the current status), then applies its own external
  guards, then calls the same `Asset.ChangeStatus` every other mutation already used — audit logging needed no new code,
  since it already runs off `AssetStatusChanged` via the outbox.
- **Guards run in this order:** permission, required metadata complete (submit, publish — reusing the same
  `AssetMetadataBuilder`/`MetadataValidationService` validation asset create and update already use), then workflow approved
  (publish only). **"Rights valid" is not implemented**: no `Rights` entity exists yet (spec Phase 2.5), so there is nothing to
  check; this is a known gap, not an oversight.
- **Workflow approval is a seam, not a real check.** `IWorkflowGate.IsApprovedAsync` is called before publishing, and its only
  implementation today (`AlwaysApprovedWorkflowGate`) always returns true, since no workflow engine exists yet (spec Phase
  2.3). Publishing's guard will need no code change once one is built.
- **Approve and reject are scoped-only powers**, like `assets.approve` already was by design (ADR-0002): no built-in role
  grants them tenant-wide, only an access rule naming the folders a reviewer may decide in. `Expire` reuses
  `assets.publish` (the inverse of publishing, the same trust level) and `Archive` reuses `retention.manage` (it is a
  retention concern, not a publishing one) rather than inventing new permission constants for two actions.
- **A transition's response is built from the asset just mutated, not read back afterwards.** Approving moves an asset out of
  `in_review`; a reviewer scoped to see only `in_review` content would then get "not found" for an approval that plainly
  succeeded, if the response came from a fresh read. `CreateAssetHandler` already documents exactly this reasoning for
  creation; the same fix applies here.
- **Restoring a version copies bytes, it never rewrites history.** `AssetVersion.Restore` creates a new, immutable version
  (next `VersionNo`) whose content is a storage-level copy (`IObjectStore.CopyAsync`) of an old version's bytes; the old
  version row is untouched. There is no upload behind a restore, so its `UploadId` is a fresh id with no meaning beyond
  satisfying the one-version-per-upload constraint every other version already has.
- **Comparing two versions is a shallow, top-level diff** of size, mime, checksum and each technical-metadata key (compared by
  serialized text, matching how JSON equality is already handled elsewhere in this codebase); a field's own internal shape
  (e.g. individual EXIF tags) is not diffed further. Fields that are identical on both versions are left out of the response
  entirely, so an empty result means "nothing differs."

## Consequences
- `POST /assets/{id}/transitions/{action}`, `POST /assets/{id}/versions/{v}/restore`,
  `GET /assets/{id}/versions/{v}/compare/{other}` are new; nothing existing changed shape.
- Step 1.6b adds `publish_at`/`expire_at` scheduled jobs and retention policies (archive/purge, legal hold) on top of this
  same state machine — those are additional *triggers* for `submit`/`publish`/`expire`/`archive`, not a different mechanism.
