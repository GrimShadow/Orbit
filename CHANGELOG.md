# Changelog
## Unreleased
- Step 0.1: repo layout, tooling, CI skeleton.
- Step 0.2: Docker Compose dev stack (Mac-friendly profiles), Keycloak realm, ADR-0001 (SeaweedFS instead of MinIO).
- Step 0.3: domain primitives (Entity, AggregateRoot, UUID v7, Result<T>) and command pipeline (validation, authorization, transaction, outbox).
- Step 0.4: EF Core 9 persistence, tenant query filter + Postgres RLS (forced), outbox, hash-chained append-only audit_log, Testcontainers integration tests; `make migrate`.
- Step 0.5: API host (Keycloak JWT, tenant/user from claims, problem+json, correlation IDs, OpenTelemetry, health, rate limiting, OpenAPI); realm now emits sub/email/tenant (UUID)/roles claims.
- Step 0.6: RabbitMQ topology (per-consumer queues, tiered retry, DLQ), outbox relay (restricted dam_system role), idempotent consumer host, Quartz-backed IJobScheduler, Worker and Scheduler hosts, `POST /api/v1/system/ping`.
- Step 0.7: UI kit (tokens, light/dark, 11 accessible components, Storybook), console shell (Keycloak OIDC+PKCE, TanStack Query, i18n en/hi, tenant switcher, user menu, error boundary), ESLint+Prettier, Keycloak realm: refresh rotation and post-logout redirect.
- Step 0.8: CI (build, test, lint, CodeQL, Semgrep, Trivy fs+image, CycloneDX SBOMs, helm lint), multi-arch GHCR images by git SHA, nightly stack smoke + ZAP, Dependabot, Dockerfiles (non-root), `tools/smoke.sh`.
- CI first-run fixes: actions pinned by commit SHA, Trivy from a digest-pinned container, CodeQL gated in-job (no paid upload), nginx security headers at server level, favicon file, pnpm 10 with release-age/trust safeguards, Dependabot cooldown.
- Step 1.1: tenants, users, groups, roles, access rules; RBAC+ABAC policy engine (ADR-0002) with 388 unit cases; JIT provisioning from Keycloak tokens; admin API (users, groups, roles, access rules, tenant); tenants RLS; realm dev users; console account page; `tools/demo-identity.sh`.
