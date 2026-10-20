# ADR-0039: Knowledge module and VIN resolution (Step 3.1)

Status: accepted. Changes none of D1-D15. Follows the multi-client rule (ADR-0003): no customer's models, VIN prefixes or themes live in code; they are
tenant data (documents and VIN rules). Examples and fixtures use the fictional "Acme".

## Context

Step 3.1 asks for owner manuals and similar documents that a vehicle app can find from a VIN: `KnowledgeDocument` on top of assets, VIN rules (prefix,
manufacturer code and descriptor patterns map to model, variant and model year, plus a theme flag), chapter indexing from PDF bookmarks or HTML headings,
`GET /knowledge/resolve?vin=`, per-language variants, and FAQ and service document types. The model and variant lists will later be synced from the
customer's dealer-management system (Step 5.2), so documents and rules refer to them by stable code, not by database id.

## Decisions

1. **A knowledge document is a mapping row on an asset**, not a new kind of asset. `knowledge_documents` (tenant-scoped, RLS, one row per asset) holds
   `asset_id` (unique), `doc_type` (`owner_manual`, `faq`, `service`, `training`, `product_info`), `model` and optional `variant` (codes: lower-case
   `[a-z0-9._-]`, at most 100 characters; a missing variant means "all variants of the model"), an optional model-year range
   (`model_year_from`, `model_year_to`, both or neither, a single year is from = to), `language` (a language tag, validated like multilingual metadata),
   an optional `theme`, `chapters` (jsonb), `chapters_source` and `chapters_indexed_version_id`. The asset keeps everything else (versions, status, rights,
   access, delivery). Deleting the mapping never touches the asset. The asset must be of type `manual`, `document` or `html5`.
2. **Themes are tenant vocabulary, not code.** A VIN rule and a document may carry a `theme`, a free string of the same code form (the BRD's electric
   versus combustion split is just two themes a tenant defines, such as `ev` and `ice`). The response's theme is the rule's, else the document's, else null.
3. **VIN rules** (`vin_rules`, tenant-scoped): `name` (unique per tenant), `pattern`, `model`, optional `variant`, a model year given either explicitly
   (`model_year`) or read from the VIN's tenth character (`year_from_vin`, default true), optional `theme`, `priority` (integer, higher wins), `enabled`.
   At most 5,000 rules per tenant. **Pattern language** (anchored at the first character, case-insensitive, at most 17 characters): a letter or digit
   matches itself; `?` matches any one character; `[ABC]` or `[A-C]` matches one character from a set or range; a final `*` matches whatever
   follows (without it the pattern must be exactly as long as the VIN). That covers prefixes (`ACM*`), manufacturer-code plus descriptor patterns
   (`ACMRS1????`) and full-length patterns. A VIN is 17 characters of letters and digits other than I, O and Q (upper-cased on input); the check digit is
   **not** enforced, because not every market's VINs carry a valid one. **Rule choice is deterministic:** among enabled rules that match, the highest
   `priority`, then the one with the most literal characters (the most specific), then the smallest id.
4. **Model year from the VIN** follows the usual tenth-character code (letters A-Y without I, O, Q, U, Z, then digits 1-9; the code repeats every 30
   years). The tenth character is ambiguous between two cycles; the latest year that is not more than one year after the current year is chosen. A rule
   with an explicit `model_year` ignores the VIN's character.
5. **Resolution** (`GET /knowledge/resolve?vin=&type=owner_manual&lang=`, any signed-in user with `assets.read`): validate the VIN (422 when malformed),
   choose the rule (404 "No rule matches this VIN" when none), then among knowledge documents of that type for the rule's model that the caller may
   read: the variant must equal the rule's variant or be empty; the year range must contain the vehicle's year or be empty; the language is the one asked
   for, else the tenant's `defaultLanguage`, else `en`, else any. Documents the caller cannot read (RBAC, ABAC and status visibility exactly as for assets)
   are invisible, so a dealer or app only ever gets what its role may see. Candidates are ranked: **published first** (an asset in `in_review` is returned
   only when no published one exists and the caller's role may see it, which is how Ask Assist works), then the most specific variant (exact over
   empty), then the most specific year (a narrower range over a wider one over none), then the most recently updated, then id. The answer is the document,
   the vehicle (model, variant, year, theme), the rule that matched, the chapters, and the other languages available. **No document found is a 404**
   with a plain message; the answer never reveals documents the caller cannot read. Only assets in `published` or `in_review` ever resolve, for every role (a draft,
   approved, expired, archived or deleted manual is reachable through the document list, never through resolve). The language is chosen first (exact tag, then
   the same primary language, for the language asked for, the tenant default and `en` in turn; else the best-ranked document's), then the ranking above picks within
   it; `otherLanguages` holds the best-ranked document of each other language, in language order. "Most recently updated" is the later of the document's and its
   asset's `updated_at` (so a new version counts); a final tie goes to the smallest id. The response's `vehicle.theme` is the rule's, else the chosen document's.

   **Problem answers carry a stable machine code** in an extension member `code`: `knowledge.invalid_vin` (422, malformed VIN, with the usual `errors.vin`),
   `knowledge.no_rule` (404, no rule matches) and `knowledge.no_document` (404, a rule matched but no readable document; this answer also carries `vehicle`
   `{model, variant, modelYear, theme}` so support can see what was matched). The human message stays plain English.
6. **Versions replace silently.** A document refers to the asset, and a delivery always serves the asset's current version, so adding a version needs no
   change to the document or to anything that points at it; the chapters are re-indexed for the new version, and the resolve answer carries the
   current `versionId` and `versionNumber`. Old references (the document id, the asset id) keep resolving because they never named a version.
7. **Chapters** are indexed by a worker consumer (`knowledge-chapters`) on `asset.version.added` and on creating or reindexing a document. A PDF's
   bookmarks (outline) become chapters with title, nesting level, and first page (`pageFrom`); each chapter's `pageTo` is the page before the next chapter
   of the same or a higher level, or the last page. HTML headings (`h1` to `h3`) of an HTML file, or of the entry file of an HTML5 bundle, become
   chapters with the heading's `id` as `anchor` (a generated slug when there is none). A document with neither gets no chapters (`chapters_source =
   none`; the sources are `none`, `pdf`, `html` and `manual`) and a person can enter or correct chapters by hand (`PUT .../chapters`, source `manual`; a later re-index replaces manual chapters only when
   asked with `?force=true`). At most 500 chapters per document; titles are trimmed and limited to 300 characters. PDF parsing uses PdfPig (already a
   dependency), reading the outline from the document's tokens rather than through PdfPig's bookmark API, which silently drops a bookmark with no destination together with
   everything beneath it: a heading that points nowhere stays (taking its first section's page), a named destination is looked up in `/Dests` and the `/Names` tree
   (unresolvable ones leave the chapter without pages), a destination outside the file leaves it without pages, a loop in the outline ends the walk, and the walk is bounded
   (20,000 items, 500 chapters). PdfPig's own reader is the fallback when the walk finds nothing. An HTML5 bundle's entry page is `index.html` (shallowest, else the only HTML
   file); entries whose names could escape the bundle (`..`, absolute, backslashes, control characters) are never candidates, nothing is extracted to disk, more than 10,000
   entries or a page over 5 MB is not read, and nothing is fetched or executed. Reading runs outside any long database transaction (ADR-0038): the consumer opts out of the host
   transaction and is idempotent (a version already indexed is skipped). The consumer listens to `asset.version.added`, `knowledge.document.created` and
   `knowledge.document.reindex_requested` and always reads the asset's *current* version; it does not replace manual chapters unless the request was forced.
   `PUT .../chapters` with an empty list clears them and hands the document back to automatic indexing (it requests a re-index).
8. **Roles and permissions.** New permission `knowledge.manage` (Admin, Librarian) for creating, updating and deleting documents, chapters and VIN rules;
   reading documents and resolving need only `assets.read`. A new built-in role **Ask Assist** is read-only (`assets.read`) and, like the BRD says, sees
   assets in `in_review` and `published`; it reuses the same status-visibility mechanism the Dealer role uses for "published only". Creating or changing a
   document also needs `assets.update` on the asset (so a person cannot map a file they may not edit).
9. **Audit and events.** `knowledge.document.created|updated|deleted`, `knowledge.document.reindex_requested`, `knowledge.chapters.indexed`, `knowledge.vin_rule.created|updated|deleted`, and
   `knowledge.vin_rules.imported`, through the normal domain-event path. Resolve is a read and writes nothing; usage tracking of resolutions is
   left to the QR service and the apps (Step 3.2 and Phase 5).
10. **VIN rule import and testing.** `POST /knowledge/vin-rules/import` takes the CSV **raw as the request body** (`Content-Type: text/csv`; `application/json` with `{"csv": "..."}` is also
    accepted; at most 2 MB; flags are the query parameters `dryRun` and `partial`). Columns are `name,pattern,model,variant,year,theme,priority,enabled` (a `year` of
    `vin` or empty means from the VIN; `enabled` defaults to true). A first row in which at least two cells are column names is a header, and then columns may be
    reordered and optional ones left out. Rules are upserted by name (ignoring case) in one transaction. The answer is `200` with `{created, updated, skipped, errors[{line,
    message}], dryRun}` where `line` is the line in the file (the header is line 1) and `skipped` counts valid lines identical to the stored rule; with any invalid line
    nothing is changed (counts are then 0) unless `?partial=true`; `?dryRun=true` validates only and reports what a real run would do. A file that cannot be read at all (empty, unknown
    or missing header columns, an unclosed quote, over 2 MB, over the 5,000-rule limit) is `422` on `csv`. One `knowledge.vin_rules.imported` event is audited for an import, not
    one per rule. `POST /knowledge/vin-rules/test` takes a VIN and returns the chosen rule and every
    candidate with why it matched or not, so an administrator can debug a pattern without publishing anything. A malformed VIN is `200` with `valid:false` and a `reason`.

## Contract (`/api/v1`)

| Route | Permission |
|---|---|
| `GET /knowledge/documents?assetId&type&model&variant&year&lang&q&cursor&limit` | `assets.read` (rows the caller can read; `{items, nextCursor}`; `assetId` finds one asset's mapping) |
| `POST /knowledge/documents`, `PUT /knowledge/documents/{id}`, `DELETE /knowledge/documents/{id}` | `knowledge.manage` + `assets.update` on the asset |
| `GET /knowledge/documents/{id}` , `GET /knowledge/documents/{id}/chapters` | `assets.read` on the asset |
| `PUT /knowledge/documents/{id}/chapters` (body `{chapters: ChapterDto[]}`), `POST /knowledge/documents/{id}/reindex?force` (202 at once; the worker finishes later) | `knowledge.manage` |
| `GET /knowledge/resolve?vin&type&lang` | `assets.read` |
| `GET /knowledge/vin-rules?q&model&cursor&limit` (`{items, nextCursor}`, by name, default limit 100, at most 500) | `knowledge.manage` or `assets.update`, so editors can see the rules |
| `POST /knowledge/vin-rules`, `PUT/DELETE /knowledge/vin-rules/{id}`, `POST /knowledge/vin-rules/test`, `POST /knowledge/vin-rules/import?dryRun&partial` | `knowledge.manage` |

DTOs: `KnowledgeDocumentDto {id, assetId, title, assetType, assetStatus, docType, model, variant, modelYearFrom, modelYearTo, language, theme,
chapterCount, chaptersSource, chaptersIndexedAt, currentVersionId, createdAt, updatedAt}`; `KnowledgeDocumentDetailDto` adds `chapters[]`;
`ChapterDto {title, anchor, level, pageFrom, pageTo}`; `VinRuleDto {id, name, pattern, model, variant, modelYear, yearFromVin, theme, priority,
enabled, createdAt, updatedAt}`; `ResolveDto {vin, rule{id, name, priority}, vehicle{model, variant, modelYear, theme}, document
(KnowledgeDocumentDetailDto + versionId and versionNumber, both null while the asset has no file), otherLanguages[{language, documentId, assetId}]}`; `VinTestDto {vin, valid, reason|null, chosen{ruleId, name}|null,
vehicle|null, candidates[{ruleId, name, priority, specificity, matched, reason}]}`; `VinImportResultDto {created, updated, skipped, errors[{line,
message}], dryRun}`.

## Fixtures and tests

A generated, committed fixture set (`tests/Dam.IntegrationTests/Fixtures/vin-fixtures.csv`, 200 fictional VINs across two themes, several models,
variants and years, with the expected model, variant, year, theme and document) and its generator (`tools/fixtures/make-vin-fixtures.py`) back the
acceptance test: every VIN resolves to the expected manual and theme.

## Limits

5,000 VIN rules per tenant (422 on `name` when creating, 422 on `csv` when an import would exceed it); a pattern describes at most 17 positions (`[ABC]` and `[A-C]` count one each, a
trailing `*` none, so `ACM*` is 3; 100 characters of text); 500 chapters per document, titles up to 300 characters, anchors up to 200, levels 1 to 10; codes up to 100 characters;
import files up to 2 MB; model years 1980 to 2100; priorities -10,000 to 10,000. One document per asset (409). No new `DAM_*` environment variable is introduced.

## Consequences

- One migration adds `knowledge_documents` and `vin_rules` (RLS, grants), the `Ask Assist` role is synced at API start, and the Keycloak dev realm gets
  an Ask Assist user for fresh installs.
- The VIN rule set is small and read on every resolve; a per-tenant cache is a later optimisation, not part of this step.
- DMS sync (Step 5.2) will create and update documents' model and variant codes and VIN rules through these same endpoints.

## Console

- A **Knowledge** sidebar entry (anyone with `assets.read`) opens three pages joined by a row of links: Documents (`/knowledge`: a filterable, paged
  list with "Map a document", edit and unmap for `knowledge.manage`), VIN rules (`/knowledge/vin-rules`: the rule table with a pattern chip, an
  enabled switch, add and edit with live pattern checking, "Test a VIN" showing the chosen rule and why every candidate matched or not, and CSV
  import with a dry-run preview and per-line errors) and the VIN resolver (`/knowledge/resolver`: what an app would be shown for a VIN: the matched
  rule, the vehicle, the document with its version, the chapters as a nested list that link to the page, and the other languages).
- The resolver tells the three failures apart by the server's stable `code` (`knowledge.invalid_vin`, `knowledge.no_rule`, `knowledge.no_document`),
  not by its wording.
- Assets of type manual, document or html5 get a **Knowledge** card on the asset page (mapping facts, where the chapters came from, a collapsible
  chapter list, and for `knowledge.manage`: edit, unmap, re-index, and a manual chapter editor). The asset page also reads `?page=N` for a published
  PDF, so a chapter link opens the right page.
- The API returns a stored value in the server's own spelling where the contract says so; the console's types follow the generated schema. The
  chapter source values are `pdf`, `html`, `manual` and `none`.
- Known limit: Ask Assist exists as a built-in role and as a dev user in the Keycloak realm file for fresh installs, but an already-running dev Keycloak
  keeps its old realm until it is re-imported, so that user does not exist there yet.
