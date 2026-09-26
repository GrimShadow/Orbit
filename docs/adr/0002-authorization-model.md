# ADR-0002: Authorization model (RBAC + ABAC)
Status: accepted (2026-09-26), implements spec A7.2 / Step 1.1

## Decision
One pure function, `PolicyEngine.Can(profile, permission, resource?)` (`Dam.Domain/Authorization`), decides everything.
It has no I/O; `IAuthorizationService` loads the caller's `AccessProfile` once per request and calls it.
`PolicyEngine.GetScope` returns the same decision as *clauses* for list filtering (search, collections), and `Can` is
implemented on top of those clauses, so a single check and a filtered list cannot disagree.

### Grants
A user's access is a set of **grants**. Each role and each access rule is one grant, and a grant must cover the permission
*by itself* (grants do not combine to satisfy one check).
- **Role grant**: applies across the tenant. Effective roles = token roles (matched by name) + roles assigned to the user + roles of the user's groups.
- **Access rule grant** (ABAC): applies only to a *scope* (`all`, `folder` subtree, `collection`, `asset_type`) and optionally only to resources matching attributes. Principal = user, group or role.
- Allow-only. There are no deny rules.

### Attributes (region, dealer, brand, channel)
- A resource with no value in a dimension, or tagged `Global`, is visible to everyone in that dimension.
- **Attribute-restricted roles** (Dealer, ApiClient) are constrained by the *user's own* attributes in **every** dimension. A missing user value means "only global resources". They also declare **required attributes** (Dealer: region, ApiClient: channel); without them the role grants nothing (**fail closed**), and `/me` reports it as an inactive role.
- Access rules constrain only the dimensions they name; an empty list means "not constrained".
- Attributes and groups come from the identity provider on every login and are not editable in DAM (admin-editable IdP attributes only), otherwise the next login would overwrite them.

### Status visibility
`assets.read` sees published assets only. Other statuses need `assets.read.unpublished`, or `assets.read.in_review` for review-stage assets (a review-stage role such as a "Reviewer"), or `assets.read.unpublished.own` for the owner's own drafts. Status is checked inside the same grant.

### Scoped-only powers
`assets.approve` and `workflow.tasks.decide` are in **no** built-in role except Admin. They are granted only through access rules, so an Approver approves only in the folders assigned to them. A permission-only check (no resource) is satisfied by role grants only.

### Ownership
`<permission>.own` grants a permission only on resources whose owner is the user (Contributor).

### Tenant and account state
A resource from another tenant is always denied. A disabled user, or a suspended tenant, is denied everything, immediately, even with a still-valid token. The tenant comes only from the token's `tenant` claim; Postgres RLS enforces it again underneath.

## Consequences
- Built-in roles are defined in code and re-synced per tenant (`BuiltInRoleSeeder`); custom roles are data.
- The role x permission matrix is an explicit table in `PolicyMatrixTests`; adding a permission without extending it fails a test.
- Not yet built: SCIM endpoint (optional in the spec), creating tenants over HTTP (tenants are created by startup bootstrap; multi-tenant admin arrives with Step 4.1), Idempotency-Key on POSTs.
