# ADR-0036: Translation, glossary and document summaries (Step 2.2)

Status: accepted. Builds on ADR-0034/0035 (AI provider layer). Does not change any of D1-D15.

## Context

Step 2.2 asks for: a glossary whose terms are never translated; `POST /assets/{id}/translate` producing suggestions that wait for review;
translation work landing in a reviewer's inbox; summaries of PDFs longer than two pages that are stored and searchable; and a side-by-side
multilingual metadata editor. `IAiProvider.TranslateAsync` / `SummariseAsync` already exist (declared in 2.1, implemented by the
OpenAI-compatible provider, `Translate: false, Summarise: false` for self-hosted).

## Decisions

1. **The glossary is enforced by us, not by the model.** A tenant glossary (`glossary_terms`) holds terms that are either `locked` (kept exactly as
   written in every language) or `mapped` (a fixed rendering per target language). Before a text goes to a provider, `GlossaryProtector` swaps each
   matching term for an opaque token (`⟦G1⟧`), the provider translates around the tokens, and the tokens are swapped back (for `mapped`, to the target
   language's rendering; for `locked`, to the original). If any token is missing, duplicated or altered in the reply, the translation is **not** stored:
   that field/language is reported `failed` with "the provider changed a protected term". The prompt also tells the model to copy tokens unchanged, but
   the check is what makes "glossary terms never translated" true for every provider, including a future machine-translation sidecar. Matching is
   ordinal, case-sensitive, longest term first, with a boundary check only where the term starts/ends with an ASCII letter or digit (Indic scripts have
   no reliable word boundary).
2. **Translations are suggestions (spec A11).** `ai_suggestions` gains kind `translation` with `field`, `language`, `source_language` and `source_hash`
   columns (and `value` becomes `text`; a check keeps tags and languages at 200 characters). A new request for the same (asset, field, language) supersedes a
   still-pending suggestion in place (a partial unique index allows only one pending); a rejected or accepted one never blocks a deliberate new request.
   The suggestion records the asset's current version for information only: metadata does not belong to a file, so a translation is listed (and can be accepted)
   whichever file is current, and an asset with no file yet can be translated. Accepting merges the text into the asset's multilingual field under that language key, leaving every other language untouched, through
   the normal metadata validation, audit and index path. A reviewer may edit the text while accepting. If the target language already has text, accepting
   needs `replace: true` (otherwise 409), and a translate request skips such languages unless `overwrite: true`.
3. **Translating is asynchronous.** `POST /assets/{id}/translate` records a `translation_jobs` row and publishes `asset.translation.requested`; a worker
   consumer (`ai-translate`) runs it field by field, language by language, and records a per-item outcome (`ok`, `skipped` with reason, `failed` with
   reason). When it finishes it publishes `asset.translation.ready`, which the notification planner turns into an in-app notification for the requester.
4. **The reviewer inbox is a review queue, not a workflow task.** Workflow tasks belong to an approval instance (ADR-0030); a translation review has
   no instance. `GET /ai/reviews` lists assets with pending translation suggestions that the caller may edit (RBAC `assets.update` plus the ABAC filter),
   and the console's Tasks page shows them in a "Translations" tab whose count joins the task badge. Naming reviewer roles per request is deferred.
5. **Summaries are derived data, like OCR text and transcripts (ADR-0035), not suggestions.** They are labelled AI-generated in the console, stored on
   `asset_extracts` (`summary`, `summary_language`, model key `summary`) and indexed for search. The enrichment run gets a `summary` step after OCR for
   `document` assets with a page count over 2 (feature flag `summaries`, default on), and `POST /assets/{id}/ai/summarise` does the same on demand for
   any asset that has extracted text. Source text is capped (first 20,000 characters).
6. **Self-hosted summaries are extractive; self-hosted translation is deferred.** `SelfHostedProvider` implements `SummariseAsync` locally (sentence
   scoring, no model, `model = "extractive-v1"`, handles the Devanagari danda), so the default deployment gets summaries offline. It still reports
   `Translate: false`: the candidate models are licence-constrained (NLLB-200 is CC-BY-NC, unusable in a product that is sold; IndicTrans2 is MIT but needs
   a custom tokenizer and a larger image). Translation therefore needs the OpenAI-compatible provider (OpenAI, Azure, or a self-hosted vLLM). A
   machine-translation sidecar gets its own ADR when we choose a model. The console says so plainly when the provider cannot translate.

## Contract (`/api/v1`)

| Route | Permission | Notes |
|---|---|---|
| `GET /glossary?q&cursor&limit` | `assets.read` | `Page<GlossaryTermDto>` |
| `POST /glossary`, `PUT /glossary/{id}`, `DELETE /glossary/{id}` | `glossary.manage` (new, Admin) | audit `glossary.term.created/updated/deleted`; max 5,000 terms per tenant |
| `POST /assets/{id}/translate` | `assets.update` + ABAC | body `{fields[], targetLanguages[], sourceLanguage?, overwrite?}` -> 202 `TranslationJobDto`; 409 when AI is off or the provider cannot translate |
| `GET /assets/{id}/translations` | `assets.read` | `{jobs: TranslationJobDto[] (latest 10)}`; the suggestions themselves are in `GET /assets/{id}/ai` |
| `POST /assets/{id}/ai/suggestions/{sid}/accept` | `assets.update` | optional body `{text?, replace?}` (only meaningful for translations); 409 when the language has text and `replace` is false; 422 on `text` (blank) or on the metadata checks |
| `POST /assets/{id}/ai/suggestions/accept-all?kind=translation` | `assets.update` | applies every pending translation it can; one whose language already has text stays pending and is named in `notes` |
| `POST /assets/{id}/ai/summarise` | `assets.update` | 202; 409 when AI off, provider cannot summarise, or no extracted text |
| `GET /ai/reviews?kind=translation&cursor&limit` | `assets.update` rows only | `Page<AiReviewItemDto>` |
| `GET /ai/reviews/summary` | any signed-in user | `{translation: int}`: the number of assets in the caller's queue |

DTOs: `GlossaryTermDto {id, term, mode, translations: {lang: text}, note, createdAt, updatedAt}` (`mode` is `locked` or `mapped`; `mapped` needs at least
one translation); `TranslationJobDto {id, assetId, status, fields[], targetLanguages[], sourceLanguage, overwrite, requestedBy, createdAt, finishedAt,
items: [{field, language, status, reason?}]}` (job status `pending|running|succeeded|partial|failed`);
`AiReviewItemDto {assetId, title, assetType, pending, languages[], fields[], requestedBy?, oldestAt}`. `AiSuggestionDto` gains `field`, `language`,
`sourceLanguage`, `sourceStale` (true while pending and the source text has changed since); `AssetAiDto` gains `summary {text, language, model, createdAt}`;
`AiCapabilitiesDto` gains `translate` and `summarise`; `AiFeaturesDto`/`AiSettingsInput` gain `summaries` (optional when saving: default on). Limits: at most
20 fields and 10 target languages per request; each source text at most the field's `maxLength`, and never more than 20,000 characters (a text the metadata
validator accepts is at most 5,000 characters unless the field states a larger `maxLength`).

Additions made while building (all additive): `TranslationJobDto.error` (why the whole job could not run; the per-item `reason` covers the rest); `kind=translation`
for accept-all; `GET /ai/reviews` is oldest first with a keyset `nextCursor`; the `Page<T>` of `GET /glossary` is alphabetical and its `nextCursor` is the last term.
Where the contract says "ABAC-filtered", an asset is listed when the caller holds `assets.read` **and** `assets.update` on it through roles and access rules,
exactly as for editing it.

**Summaries on demand.** `POST /assets/{id}/ai/summarise` raises the internal `asset.ai.summarise.requested`; the worker's `ai-enrich` consumer rewrites the summary
from the text already extracted from the current version and records the outcome as the run's `summary` step (the run's own status is not changed). The summary is in
`GET /assets/{id}/ai` when it is done, so a client polls that until `summary.createdAt` changes.

**Events.** `asset.translation.requested` (internal, audited: who asked for what), `asset.translation.ready` (public: webhooks, chat channels and the requester's
notification; it carries `jobId`, `status`, `translated`, `skipped`, `failed`, never any text; written to the outbox by the worker, outside the audit chain, like
`asset.ai.enriched`), `asset.ai.summarise.requested` (internal, audited), `glossary.term.created|updated|deleted` (internal, audited). A new notification category
`translation` ("Translations ready", in-app on, email off) tells only the requester, and only while they can still read the asset; its link is the asset page.

**Search.** The summary is indexed as `summary_text` (analysed with the generic analyzer, like `ocr_text`), searched with the other text fields, highlighted, and reported
as `matchedBy: "summary"`. The field is added to a live index by the same put-mapping step that added `ocr_text`, so **no reindex is needed**; existing assets simply have no summary
until they are enriched again (`POST /ai/backfill` with `onlyMissing: false`, or `POST /assets/{id}/ai/summarise`).

## Configuration
| Variable | Default | Meaning |
|---|---|---|
| `DAM_AI_SUMMARY_WORDS` | `120` | length of a summary in words (20 to 500): the budget of the extractive summariser and the limit asked of a model |
| `DAM_AI_CHAT_TIMEOUT_SECONDS` | `90` | timeout of one chat call of the OpenAI-compatible provider (translations, summaries); raise it for slow self-run models |

No other new variable: the glossary and the `summaries` switch are tenant data and settings. Existing settings of tenants that predate the switch read as on.
The translation consumer shares `DAM_AI_CONCURRENCY` with enrichment, and its provider calls use the same retries, cool-down and redaction as every other call.

## Consequences

- Translation quality and cost are the provider's; the glossary guarantee holds regardless of provider.
- A glossary change does not retroactively touch stored text or pending suggestions.
- Accepting a translation writes metadata exactly like a manual edit, so versioning, validation, audit and search behave the same.
- The Hindi/Marathi/Tamil/Telugu analyzers already index multilingual fields; no index mapping change is needed for translations. The summary adds one
  indexed text field.
- Migration `20261020143410_TranslationAndGlossary`: `glossary_terms` and `translation_jobs` (tenant-scoped, row-level security like every table), the new `ai_suggestions`
  columns, `asset_extracts.summary*`. Rolling it back deletes translation suggestions.
- A glossary term must be matched exactly: a plural or an inflected form (which many Indic languages produce) is not the term. Add the forms that matter.
  A mapped term with no rendering for a language is kept as written, like a locked one.
- Translating is one provider call per field and language, one at a time per worker; a big request is slow, not expensive in memory. An answer the model had to cut
  off is refused, never stored.
- Marker characters (`⟦ ⟧`) cannot be part of a glossary term or rendering, and if the source text itself contains `⟦G` the tokens use a longer prefix.
