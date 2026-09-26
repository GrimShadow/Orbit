# Orbit (repo: pads4-dam)

Read `docs/DAM-Build-Spec.md` (Part A) at the start of every session. Build steps are in Part B; implement one step at a time.
Key decisions D1–D15 are fixed; changing one needs an ADR in `docs/adr/`.

## Conventions
- C#: .NET 9, nullable on, warnings as errors, file-scoped namespaces, `dotnet format` before commit.
- TS: strict, ESLint, Prettier; pnpm workspaces (`web/*`).
- Commits: Conventional Commits. Branch `feat/<phase>-<step>-<slug>`.
- Writes = command -> validator -> handler -> domain event -> outbox. No file bytes through dam-api.
- Config via env vars only (`DAM_*`, see spec Appendix A). No secrets in repo.

## Commands
- Build: `make build`   Test: `make test`   Stack: `make up` / `make down` / `make reset`

## Definition of done (every step)
Tests green, `docker compose up` healthy, `docs/api/openapi.yaml` regenerated, `CHANGELOG.md` updated.
