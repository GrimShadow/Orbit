# ADR-0003: Content structure, and how one product serves many customers
Status: accepted (2026-09-26), implements Step 1.2

## The rule
Orbit is sold to many organisations. **No customer-specific name, taxonomy or rule lives in code.** Everything a customer
owns is *data*: tenant configuration, vocabularies, metadata schemas, folders, roles and access rules. Repeatable starting
points ship as **template packs** (`src/Dam.Application/Templates/*.json`, embedded). A test fails if a pack names a real
customer, and every pack is checked for internal consistency.

- `core` applies to any customer: languages, channels, a `Global` region and generic metadata for every asset type.
- `automotive-sample` is a worked example on a fictional brand ("Acme Motors"): Brand > Model > Variant > Model year,
  campaigns, regions, manual metadata and sample folders. A real customer gets their own pack (a new JSON file, or built
  through the API/console) that `requires` `core`.
- Applying a pack is **additive and idempotent**: it creates what is missing and never renames, overwrites or deletes
  what the tenant has. Schemas are extended with a new version, never edited in place.
- A tenant names packs at setup (`DAM_BOOTSTRAP_TEMPLATES`, or `EnsureTenantCommand`) or applies them later from the API.

## Model
- **Folders**: a tree with a permanent dotted `label` path (`brand.roadster.stills`). Access rules scope to a subtree by
  that path, and folder edits are authorised *per folder*, so a rule scoped to `brand` lets its holder manage only inside
  `brand.*`. Folders carry inherited metadata (deeper folders win, key by key).
- **Vocabularies and terms**: flat lists or hierarchies. Metadata stores the immutable term **code**, not the label or id,
  so relabelling never breaks assets and codes are portable between tenants and environments. Terms are deprecated, not
  deleted, once they may be in use.
- **Metadata schemas**: per asset type, versioned and immutable. A change creates the next version and an asset keeps
  validating against the version it was written with, so a schema change never invalidates existing assets.
- **Validation**: one engine (`MetadataValidator`, pure) plus `MetadataValidationService` (vocabulary membership and the
  languages enabled for the tenant). Errors are field-level, keyed `metadata.<field>`.

## Conventions that connect to access control (ADR-0002)
- Region, dealer, brand and channel attribute values coming from the identity provider are compared, case-insensitively,
  with **term codes**. So a user's `region = North` matches assets tagged with the term code `north`; `global` is the
  "everyone" value.

## Deviations and deferrals
- **Paths are `text`, not the `ltree` type.** Same syntax (enforced by a CHECK constraint), prefix index for subtree
  queries; converting to `ltree` later is one `ALTER COLUMN ... TYPE ltree`. Avoids leaking Npgsql's `LTree` into the domain.
- Deferred to Step 1.3: assets record their schema version; deleting a folder or term checks for assets that use it.
  Deferred to 1.8: console screens. Not yet built: bulk CSV import of terms, per-tenant field label translations.
