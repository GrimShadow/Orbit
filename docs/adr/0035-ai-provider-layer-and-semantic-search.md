# ADR-0035: AI provider layer, enrichment and semantic search

## Status
Accepted (Step 2.1, backend)

## Context
Search so far finds what people typed into a field. Step 2.1 lets the system look at what is *in* a file: suggest tags, read the text in a
picture or a scanned page, transcribe speech, tell the language, and turn pictures and words into vectors so that a search for "red SUV on
beach" finds the right photograph although nobody wrote those words anywhere. It must work in every hosting model (decision D10): models running
inside the installation, or a cloud service, chosen by configuration; and it must be safe by default, because a cloud provider means customer
pictures and audio leave the building. It builds on the ingest pipeline and renditions (ADR-0005/0006/0007), the search index and query API
(ADR-0011/0012, whose k-NN placeholder fields this step fills), the consumer and event patterns of ADR-0031, and the structure of ADR-0032/0033.
All names in code and tests are generic ("Acme").

## The provider abstraction
`IAiProvider` (`Dam.Application/Abstractions/AiPorts.cs`, spec A11) with `TagImageAsync`, `OcrAsync`, `TranscribeAsync`, `EmbedTextAsync` (and
`EmbedTextsAsync`, a batch of up to 64), `EmbedImageAsync`, `DetectLanguageAsync`, `TranslateAsync`, `SummariseAsync`, plus `Kind`, `Capabilities`
and `CheckHealthAsync`. Deviations from the sketch in the spec, all for the same reason (extensible, and the provider needs facts a bare stream does
not carry): OCR, transcription and text embedding take small option records instead of loose parameters (`OcrOptions`, `TranscribeOptions` with the
language, a length cap and the file name a cloud service needs to pick the audio format, `TextEmbedOptions` with the *space* and the *role*), and the
results are records that carry the model name and a confidence rather than bare strings and arrays.

- **Two text spaces.** `TextEmbeddingKind.Text` is a text-only space (multilingual-e5, 384-d) for finding words by meaning; `TextEmbeddingKind.Clip` is
  the CLIP text tower, which lands in the *same* 512-d space as image vectors, so a phrase can be compared with a picture. Only the second makes
  cross-modal search possible.
- **Capabilities** (`Tags, Ocr, Transcribe, TextEmbeddings, ImageEmbeddings, DetectLanguage, Translate, Summarise`) tell callers what to expect. A
  step a provider cannot do is *skipped and recorded*, never failed; a call to it throws the typed `AiNotSupportedException`.
- **Errors are typed**: `AiUnavailableException` (unreachable, busy, timed out, cooling down: worth trying later), `AiRequestException` (it answered and
  refused this input, or answered nonsense: retrying will not help; carries the status), `AiNotSupportedException`. Anything else is a bug.
- **Translate and summarise** are Step 2.2. They are declared, implemented by the OpenAI-compatible provider through chat completions with simple prompts,
  and `SelfHostedProvider` throws `AiNotSupportedException` for them. No UI, workflow or endpoint uses them yet.

### Providers
**`SelfHostedProvider`** talks to the AI sidecar (`deploy/ai-sidecar`, ADR-0034: CLIP via ONNX Runtime, multilingual-e5-small, faster-whisper, an OCR engine) over
the contract below. Base URL `DAM_AI_SIDECAR_URL`, optional bearer `DAM_AI_SIDECAR_TOKEN`.

| Call | Request | Answer |
|---|---|---|
| `GET /healthz` | | `{status: ok\|loading\|degraded, models: {clip, text, whisper, ocr: {name, dim?, loaded}}, version}` |
| `POST /v1/embed/image` | multipart `file` | `{model, dim: 512, embedding}` |
| `POST /v1/embed/text` | `{texts (<=64), kind: "clip"\|"text", role?: "query"\|"passage"}` | `{model, dim, embeddings}` |
| `POST /v1/tag` | multipart `file`, optional `labels` (JSON array), `top_k`, `threshold` | `{model, tags: [{label, score}]}` |
| `POST /v1/ocr` | multipart `file`, optional `lang` | `{model, language, text, lines: [{text, score, box}]}` |
| `POST /v1/transcribe` | multipart `file`, optional `lang`, `max_seconds` | `{model, language, duration, segments: [{startMs, endMs, text}]}` |
| `POST /v1/detect-language` | `{text}` | `{language, confidence}` |

Uploads are streamed (`StreamContent` over the caller's stream; nothing is buffered), up to 25 MB. A busy sidecar answers `503` with `Retry-After`; errors
are `{error, message}`. Vectors are L2-normalised. Resilience, shared with the other provider: per-kind timeouts (embeddings 30 s, tags 60 s, OCR 120 s,
transcription 20 min, health 5 s); **retries only for a busy or unreachable service** (`502/503/504/429`, connection errors, timeouts; `Retry-After` honoured up
to 10 s, else exponential backoff with jitter, `DAM_AI_RETRIES` attempts, default 3) and only when the request can be sent again (a seekable stream is rewound,
anything else is tried once); **a cool-down breaker**: after `DAM_AI_FAILURE_THRESHOLD` (3) consecutive transport failures further calls fail at once for
`DAM_AI_COOLDOWN_SECONDS` (30), then one trial call goes through. A refusal (`4xx`) is never retried and does not trip the breaker. Content is never logged.

**`OpenAiCompatibleProvider`** speaks the OpenAI REST API: OpenAI, Azure OpenAI, India-region or other sovereign endpoints, vLLM, Ollama.
- *Tags and OCR*: a vision chat completion (`DAM_AI_VISION_MODEL`) with the picture inline as a data URI and a system prompt demanding strict JSON
  (`response_format: json_object`, temperature 0; `DAM_AI_JSON_MODE=false` for servers that reject it). The reply is parsed tolerantly (a code fence or a
  sentence around the JSON is fine). OCR returns text and language; line confidences do not exist, so `Confidence` is null.
- *Text embeddings*: `/embeddings` (`DAM_AI_EMBEDDING_MODEL`). `text-embedding-3-*` models are asked for exactly `DAM_AI_TEXT_DIM` dimensions
  (`DAM_AI_SEND_DIMENSIONS` overrides); the vectors are normalised here; a size that does not match is an `AiRequestException` that names the setting to change.
- *Transcripts*: `/audio/transcriptions` with `response_format=verbose_json` and `timestamp_granularities[]=segment`; seconds become milliseconds, a language
  *name* ("english") becomes a code ("en").
- *Language, translation, summaries*: chat completions (`DAM_AI_CHAT_MODEL`). Translation keeps glossary terms verbatim.
- **Image embeddings do not exist in the standard OpenAI API.** `EmbedImageAsync` and CLIP-space text embeddings throw `AiNotSupportedException`
  and `Capabilities.ImageEmbeddings` is false. With this provider, enrichment records the image-embedding step as skipped, and semantic search runs on
  the text vectors only: "red SUV on beach" is then matched against each asset's *words* (title, tags, OCR text, transcript), not its pixels. This
  is a real functional difference and the reason a deployment that wants picture search should use the self-hosted provider (or a vLLM/CLIP service behind
  the sidecar contract).
- Azure: `DAM_AI_BASE_URL` may contain `{model}`, replaced by each feature's model (deployment) name; `DAM_AI_AUTH_STYLE=api-key` sends the key in the `api-key`
  header; `DAM_AI_API_VERSION` is appended as `?api-version=`.
- **The API key** (`DAM_AI_API_KEY`, environment only) travels only in a header. Any text that goes into an exception or a log (a provider's error
  message, which some services fill with the key they were given) is scrubbed of the configured key and of anything shaped like one.

**`NullProvider`** (`none`) supports nothing. It is also what a tenant gets while AI is off for it (below).

### Choosing the provider
`DAM_AI_PROVIDER` (`self-hosted | openai-compatible | none`, default `none`) is the deployment default. A tenant may override it in `ai_settings.provider`
(null follows the default). **Switching provider is a configuration or settings change and nothing else**; an integration test runs the same enrichment through
both providers (the OpenAI-compatible one against an in-process stub server) and switches a tenant between them.

### Privacy: off until an administrator turns it on
`ai_settings.enabled` defaults to **false** for every tenant, whatever the deployment default is. `IAiResolver` returns a `NullProvider` when the tenant has not
enabled AI (or the chosen provider is not configured), so no code path can send content out. A provider counts as *off-site* when it is OpenAI-compatible and its
base URL is not loopback, a private-network address or a single-label/`.internal`/`.local` host. Enabling AI with an off-site provider requires
`acknowledgeOffSite: true` in the request (422 on that field otherwise), asking the administrator to confirm they are entitled to send the tenant's pictures and audio to
that service (consent, India's DPDP Act 2023, contractual and sector rules). The acknowledgement is asked again after AI has been switched off and on. The
administrator's *test* (`POST /ai/test`) sends only a tiny built-in picture and a phrase, so it works before AI is enabled.

## Data model (migration `AiEnrichment`)
All tables are tenant-scoped with forced row-level security like the others; `asset_id` references `assets` with `ON DELETE CASCADE`, so nothing is orphaned if an asset
row is ever removed. The application never hard-deletes assets (retention "purge" is a soft delete); the purge job additionally deletes an asset's embeddings, extracts,
suggestions and runs, since the vectors are large.

| Table | Purpose |
|---|---|
| `ai_settings(tenant_id PK, enabled, provider?, features jsonb {tags, ocr, transcribe, embeddings}, ocr_language?, updated_by, updated_at)` | the tenant's switchboard |
| `ai_suggestions(id, tenant_id, asset_id, version_id, kind tag\|language, value, confidence real, model, status pending\|accepted\|rejected, decided_by, decided_at, created_at)` | unique per (asset, version, kind, value) |
| `asset_extracts(asset_id, version_id, ocr_text, ocr_language, transcript_language, transcript_segments jsonb, transcript_text, duration_ms, model jsonb, ...)` | extracted text |
| `asset_embeddings(asset_id, version_id, kind image\|text, dim, model, vector bytea, created_at)` | float32 little-endian, `octet_length = dim * 4` enforced; a reindex never recomputes |
| `ai_runs(id, tenant_id, asset_id, version_id, status pending\|running\|succeeded\|failed\|skipped, steps jsonb, error, attempts, started_at, finished_at, created_at)` | unique per (asset, version); visibility |

No pgvector: vectors are stored as bytes and live in OpenSearch for search.

### A deliberate deviation from the spec: what needs a person
The spec says all AI output is a *suggestion* until an editor accepts it (stored in `metadata.ai_suggestions`). We keep that for what would change the
asset's description: **tags and language are suggestions** (`ai_suggestions`, pending until accepted or rejected) and are never written into the asset's metadata
or tags by the system. **Extracted text (OCR, transcript) and embeddings are derived data**: they are stored in their own tables, indexed and shown as they are, and never
written into editorial metadata. Nobody "accepts" a transcript any more than they accept a thumbnail; requiring it would make the feature useless for search. A
suggestion table beside the asset, rather than a key inside `metadata`, keeps decided state, confidence, model and history queryable and out of the schema validation.

## Enrichment
`EnrichConsumer` (queue `ai-enrich`, in the worker) handles `asset.version.added` (automatic) and `asset.ai.requested` (a re-run or a backfill, carrying `Force`). It has
its own queue, so **a slow, missing or failing AI service can never hold up or fail the ingest pipeline**.

**Chaining with renditions.** The consumer listens to `asset.version.added`, like the rendition consumer, and takes the stored rendition it needs. It does not race the
rendition consumer: it **waits** (polling the `renditions` table, up to `DAM_AI_RENDITION_WAIT_SECONDS`, default 60) for the profile to appear, and only if it never does (that
consumer is not running, or a PDF has fewer pages than the cap) makes the rendition itself on demand, through a scope and transaction of its own. Two workers storing the same
rendition at once is the one thing the renditions code tolerates badly (it clears the change tracker when the loser hits the primary key, which silently threw away this
run's own record in an early version of this step), and a rendition job holds the audit chain's per-tenant lock for as long as its transaction lasts, so the enrichment
transaction never touches the audit chain: `asset.ai.enriched` is written straight to the outbox (not audited; the `ai_runs` row is the record), and the run's progress ("running",
"waiting to retry") is written in transactions of their own. It never reads an original picture: tags and image vectors use the **1,024 px `preview`**, image OCR the
**1,920 px `web`** rendition (small print needs the pixels), videos the **`poster`**, PDFs and Office files **`pdf_pages-N`** (1,600 px). Audio for transcription is
extracted with ffmpeg (already in the worker image) reading the original through a short-lived signed address (not copied to disk first), as **mono 16 kHz MP3**
capped at `DAM_AI_MAX_AUDIO_MINUTES` (default 60) and encoded at 32 kbps (24 kbps beyond 100 minutes, and cut to fit beyond ~133) so it stays under the providers'
25 MB upload limit (`AudioPlan`). A video without an audio track is a skipped step, not a failure.

| Type | Steps (in order) |
|---|---|
| image | `tags`, `ocr`, `image-embedding`, `language`, `text-embedding` |
| video | poster `tags`, poster `image-embedding`, `transcript`, `language`, `text-embedding` |
| audio | `transcript`, `language`, `text-embedding` |
| pdf / office | page 1 `tags`, page 1 `image-embedding`, `text-layer` (PDF only), `ocr` (if the PDF has no text layer, and for Office), `language`, `text-embedding` |
| anything else (svg, zip, 3d...) | run recorded as skipped |

- **Documents**: a PDF's own text layer (read with PdfPig) is used when it has one: free and better than OCR. Scans and Office documents are OCR'd page by page, up to
  `DAM_AI_MAX_OCR_PAGES` (default 5).
- **Tags**: the provider's candidates are normalised (lower-case, trimmed, at most 50 characters), limited to `DAM_AI_TAG_TOP_K` (12) and, only if `DAM_AI_TAG_THRESHOLD` is set, cut at that confidence (unset: each provider applies its own, since a zero-shot classifier's softmax scores over a long vocabulary are small by nature while a language model's stated confidence is not),
  and **tags the asset already has, or that were already suggested for the version in any state, are not proposed again** (so a re-run never resurrects a rejection).
- **Language**: detected from the OCR text or transcript (at least 20 characters) and proposed as a `language` suggestion unless the asset already has that
  language; a provider without a detector falls back to the language its OCR or transcription reported.
- **One text vector per asset.** The vector is the unit-length *mean* of the embeddings of (a) the title and tags (one piece) and (b) the first
  `DAM_AI_MAX_EMBED_CHUNKS - 1` (default 15) chunks of about `1,000` characters of the text read from it or spoken in it, cut on sentence or segment boundaries.
  Averaging keeps one vector per asset (a transcript is not one passage), and the title/tags piece keeps a short OCR text anchored to what the asset is.
  The vector is derived data: renaming the asset or editing tags later does not change it until the asset is enriched again.
- **Idempotent per (asset, version)**: a version whose run has `succeeded` is not redone unless forced; a redelivered message is skipped by the host's
  processed-messages ledger; re-runs update vectors and the extract in place and add only new suggestions. A newer version supersedes: work for a version that is
  no longer current is dropped.
- **Skip-and-record**: AI off for the tenant, provider not configured, unsupported type, or every step off: a `skipped` run with the reason, and no external call.
- **Failure model**: an unreachable provider aborts the *attempt*; the transaction rolls back and the host retries with its tiered backoff
  (`DAM_MQ_MAX_ATTEMPTS`, `DAM_MQ_RETRY_BASE_MS`; defaults 5 attempts at 2, 4, 8, 16 s) and then parks the message in the DLQ, at which point the run shows `failed`
  ("Gave up after N attempts"). Between attempts the run is visible as `pending` with the reason, written in a transaction of its own so it survives the rollback;
  `running` is likewise committed as soon as the work starts. A provider that refuses *one input* (4xx, wrong vector size) fails only that step; the run is
  `succeeded` if any step succeeded, `failed` if every attempted step failed, `skipped` if nothing was attempted. The steps with timings, models and details are in `ai_runs.steps`.
- **Concurrency**: at most `DAM_AI_CONCURRENCY` (default 2) enrichments make provider calls at once per worker process, however many messages the host has in flight.
- After success the pipeline publishes the internal `asset.ai.enriched`, which `IndexConsumer` handles by re-indexing the asset with its vectors and extracted text.
- Tag-only edits and renames made through `PATCH` raise no event today (a pre-existing gap: only a metadata change does), so accepting a suggestion uses a new
  `Asset.AddTags` that does raise `asset.metadata.updated`: audited, delivered to webhooks and re-indexed.

### Backfill
`POST /ai/backfill` queues enrichment for existing assets: all of the default types (image, video, audio, document, manual) or a filter (types, folder with
subfolders), at most 5,000 per call (default 500). Each queued asset's run is set `pending` and an `asset.ai.requested` event goes through the outbox in the same
transaction; the worker processes them at its own pace (the outbox relay hands over about 200 a second, `DAM_AI_CONCURRENCY` sets the real rate), so a large
backfill never floods the provider. `onlyMissing` (default true) skips assets that are done, queued or running, so repeating the call continues where it stopped.
The same logic is the scheduler's `ai.backfill` job: run on demand for one tenant (`IJobScheduler.ScheduleOnceAsync(..., data: {tenantId})`) or, with
`DAM_AI_BACKFILL_CRON` set, on a schedule for every tenant that has AI on, as a catch-up for files that arrived while AI was off or down.

## Search
### Index
The mapping gains `ocr_text` and `transcript_text` (analysed), `transcript_segments` (**nested**: `start_ms`, `end_ms`, `text`, so a text match can say *when*), and the two
`knn_vector` fields, now with **configurable dimensions** (`DAM_AI_TEXT_DIM`, default **384**, replacing the Step 1.7a placeholder of 1,024; `DAM_AI_IMAGE_DIM`, default
**512**) on the **Lucene** engine (HNSW, `m` 16, `ef_construction` 128, cosine similarity) instead of nmslib. Lucene because it applies a filter *while* it walks the
graph: the access filter and the user's filters are part of the k-NN query itself, so a result never has to be thrown away afterwards. (Verified on OpenSearch 2.17.1:
Lucene's cosine score is `(1 + cos) / 2`.)

**A change of dimension needs a reindex**; a live index cannot change the size of a vector field. Until then:
- indexing an asset into an index built for other sizes still works: its text and everything else are indexed, the vectors are left out (logged);
- vectors stored with another size than configured are left out of the document too;
- `GET /ai/status` reports `vectorIndex.needsReindex`, and `POST /ai/reindex` asks the worker (event `search.reindex.requested`, consumed by `SearchReindexConsumer`) to run the
  existing zero-downtime reindex (build a new index, swap the alias, drop the old one). It reads the vectors from `asset_embeddings`: **no provider call**. A tenant's existing
  dev index has empty vector fields, so one reindex converts it; the existing `SearchReindexJob` does the same for every tenant.

Every hit's `_source` is now limited to the fields the DTO needs: vectors and transcripts never travel back.

### Lexical query (all searches with text)
The text query is a `bool` of named `should` clauses: title and metadata (`text`), `ocr_text` (`ocr`), `transcript_text` and the nested transcript segments (both `transcript`,
the nested clause with `inner_hits`, top 3). So a plain search also finds words read from pictures and spoken in recordings, and each hit reports **`matchedBy`** and
**`transcriptMatches: [{startMs, text}]`** (time order) for "jump to this moment". Highlighting also covers `ocr_text` and `transcript_text`.

### Hybrid search (`POST /search` with `semantic: true`)
Three legs, each carrying the **same access filter and the same user filters inside the query** (so an asset the caller cannot read can never enter a leg, however good its vector):

1. **lexical**: the query above, top *N*;
2. **image**: k-NN on `vector_image` with the query's *CLIP* text embedding (cross-modal), top *N*;
3. **meaning**: k-NN on `vector_text` with the query's text embedding (`role=query`), top *N*.

The query is embedded by the tenant's provider (both embeddings in parallel; recent answers are cached in memory for 10 minutes so paging does not call the provider again). A vector
leg keeps only hits close enough to count: cosine similarity at least a floor and within `DAM_AI_KNN_MARGIN` (0.04) of that leg's own best hit, because k-NN always returns its *k*
nearest, however far; without this a query nothing resembles would still "find" things. The floors depend on the models, because similarity scales do. Measured on the sidecar's models: CLIP
scores a relevant picture about 0.25 to 0.33 against a query and an unrelated one 0.10 to 0.20, so `DAM_AI_IMAGE_MIN_COSINE` defaults to **0.22**; multilingual-e5 compresses
everything towards 1 (unrelated text 0.72 to 0.78, relevant 0.82 to 0.90), so `DAM_AI_TEXT_MIN_COSINE` defaults to **0.80** for the self-hosted provider and **0.30** for OpenAI-style
embeddings (unrelated near 0, relevant above 0.35). They are heuristics: measure before changing a model, and tune per deployment.

The legs are fused client-side with **Reciprocal Rank Fusion**, `score = sum over legs of 1 / (k + rank)`, `k` = `DAM_AI_RRF_K` (default 60), because the dev OpenSearch (2.17.1)
lacks the native hybrid/RRF search pipeline (it arrives in 2.19). Ties break by best rank, then id. An item good in two legs beats one great in one. The result is `matchedBy`
from the lexical clauses (`text`, `ocr`, `transcript`) plus `image` and, for the meaning leg, `text`.

**Paging.** The top `DAM_AI_SEMANTIC_DEPTH` (default 200) of each leg are fused and the fused list is paged by offset: `nextCursor` is an opaque cursor that encodes the offset. Every
page re-runs the legs, so **semantic results are not live-cursor-stable**: an asset indexed between two pages can shift the list. `total` is the size of the fused list. **The sort is ignored**
when `semantic` is true (the order *is* the fused rank). Facets are computed over the fused set. Highlights come from the lexical leg.

**Degrading gracefully.** If the tenant has AI off, the provider is not configured, the embeddings feature is off, the provider is unavailable, there is no query text, or `similarTo` is
used, the search runs as an ordinary text search and the answer says why in `semanticFallback` (`ai_disabled`, `provider_not_configured`, `embeddings_disabled`,
`embeddings_unsupported`, `provider_unavailable`, `empty_query`, `similar_to`, `vectors_not_indexed`). `semantic` (bool) says whether a hybrid ran, and `semanticLegs` which
parts (`text`, `image`, `meaning`). A provider without image embeddings runs `["text", "meaning"]`; if one vector field cannot answer (a size mismatch) the other legs still run.

## API
Tag **AI**. DTOs are in `Dam.Shared/Ai/AiDtos.cs`; all are camelCase JSON.

| Method and path | Permission | Request | Answer |
|---|---|---|---|
| `GET /assets/{id}/ai` | `assets.read` on the asset | | `AssetAiDto {status none\|pending\|running\|ready\|failed\|disabled, run?, suggestions[], language?, ocrText? (cut at 20,000 chars), ocrTextTruncated, ocrLanguage?, transcript? {language, durationMs, model, segments[{startMs,endMs,text}]}, embeddingsReady}` |
| `POST /assets/{id}/ai/enrich` | `assets.update` | | `202` `AssetAiDto` (pending); 409 if AI is off, no file, or a run is going |
| `POST /assets/{id}/ai/suggestions/{sid}/accept` | `assets.update` | | `AiDecisionDto {suggestions[], tags[], changed, notes[]}` |
| `POST /assets/{id}/ai/suggestions/{sid}/reject` | `assets.update` | | `AiDecisionDto` |
| `POST /assets/{id}/ai/suggestions/accept-all?kind=tag` | `assets.update` | | `AiDecisionDto` (tags past the 50-tag limit stay pending, with a note) |
| `GET /ai/settings` | `ai.manage` | | `AiSettingsDto {enabled, provider?, defaultProvider, effectiveProvider, sendsDataOffSite, features, ocrLanguage?, providers[{kind, configured, sendsDataOffSite, description}], updatedBy?, updatedAt?}` |
| `PUT /ai/settings` | `ai.manage` | `{enabled, provider?, features? {tags, ocr, transcribe, embeddings}, ocrLanguage?, acknowledgeOffSite?}` | `AiSettingsDto`; 422 on `provider` (unknown, none, not configured), `ocrLanguage`, `acknowledgeOffSite` |
| `GET /ai/status` | `ai.manage` | | `AiStatusDto {enabled, provider, providerConfigured, health {status, detail, latencyMs, models}, capabilities, counts {eligible, enriched, pending, running, failed, skipped, notStarted}, queueDepth, vectorIndex {textDim, imageDim, liveTextDim?, liveImageDim?, needsReindex}}` |
| `POST /ai/backfill` | `ai.manage` | `{types?, folderId?, includeSubfolders?, onlyMissing?, limit?}` | `{matched, enqueued, remaining}`; 409 if AI is off |
| `POST /ai/test` | `ai.manage` | | `{provider, ok, totalMs, steps[{name, ok, durationMs, detail}]}` (health, embed-image, embed-text, embed-text-for-images; sizes checked against the configuration) |
| `POST /ai/reindex` | `ai.manage` | | `202` `{accepted, message}` |
| `GET /ai/capabilities` | any signed-in user | | `AiCapabilitiesDto {enabled, semanticSearch, enrichment, textVectors, imageVectors, provider, sendsDataOffSite}` |

`POST /search` gained `semantic` (bool) in the request; hits gained `matchedBy` and `transcriptMatches`; the answer gained `semantic`, `semanticLegs`, `semanticFallback`.
`GET /ai/capabilities` is for the console, for anyone signed in: whether to offer Smart search and show AI panels. `enabled` means AI is on for the tenant and its provider is configured; `semanticSearch` also needs embeddings on, a provider with at least one kind of vector, and a search index built for the configured vector sizes (not awaiting a reindex); `enrichment` needs at least one feature on; `textVectors` / `imageVectors` say which comparisons exist (the OpenAI-compatible provider has no image vectors); `sendsDataOffSite` is true only while AI is on with an off-site provider. It makes no provider call, carries no URLs, keys or model names, and the index-readiness check is remembered for 5 seconds per tenant. (The provider's own capability flags in `GET /ai/status` are `AiProviderCapabilitiesDto`.)

**Queue depth** is derived from the database (runs pending or running): the API does not talk to RabbitMQ.

Accepting a **tag** adds it through the asset's normal path (`Asset.AddTags`: normalised, at most 50, `asset.metadata.updated` raised, so audit, webhooks and the index follow). Accepting a
**language** sets the asset's `language` metadata field *only if its current schema has one that accepts the value* (the core pack's multi-valued vocabulary field does); otherwise the
acceptance is recorded and `notes` says why nothing was applied. Each decision raises the internal `asset.ai.suggestion.decided` (audit trail). Only suggestions of the asset's *current* version can be
decided (409 otherwise). Settings changes raise `ai.settings.changed` (audit; never contains a secret). None of these events is in the webhook catalogue.

**Permission.** A new `ai.manage` (Admin only, since Admin holds every permission; added to `Permissions.All` and so to the policy matrix tests). Reading results is `assets.read`; deciding or re-running
is `assets.update`, so anyone who can edit an asset can curate its AI suggestions, and an Editor cannot change which provider receives the tenant's files.

## Configuration
| Variable | Default | Meaning |
|---|---|---|
| `DAM_AI_PROVIDER` | `none` | deployment default: `self-hosted`, `openai-compatible`, `none` |
| `DAM_AI_SIDECAR_URL`, `DAM_AI_SIDECAR_TOKEN` | | self-hosted sidecar address and optional bearer token |
| `DAM_AI_BASE_URL`, `DAM_AI_API_KEY` | | OpenAI-compatible base URL (may contain `{model}`) and key (environment only, never logged or returned) |
| `DAM_AI_AUTH_STYLE`, `DAM_AI_API_VERSION` | `bearer` | `api-key` for Azure; appended `?api-version=` |
| `DAM_AI_VISION_MODEL`, `DAM_AI_CHAT_MODEL` | `gpt-4o-mini` | tags + OCR; language, translation, summaries (naming only one sets both) |
| `DAM_AI_EMBEDDING_MODEL`, `DAM_AI_TRANSCRIPTION_MODEL` | `text-embedding-3-small`, `whisper-1` | |
| `DAM_AI_SEND_DIMENSIONS`, `DAM_AI_JSON_MODE` | auto, `true` | request `dimensions` from the embeddings endpoint; request `json_object` from chat |
| `DAM_AI_TEXT_DIM`, `DAM_AI_IMAGE_DIM` | `384`, `512` | vector sizes. **Changing them needs a reindex.** |
| `DAM_AI_MAX_AUDIO_MINUTES`, `DAM_AI_MAX_OCR_PAGES` | `60`, `5` | |
| `DAM_AI_CONCURRENCY` | `2` | simultaneous enrichments per worker process |
| `DAM_AI_TAG_TOP_K`, `DAM_AI_TAG_THRESHOLD` | `12`, unset | unset: the sidecar applies its own default threshold, the OpenAI-compatible provider drops tags below 0.3 |
| `DAM_AI_MAX_EMBED_CHUNKS` | `16` | pieces averaged into the text vector (title/tags + 15 chunks) |
| `DAM_AI_SEMANTIC_DEPTH`, `DAM_AI_RRF_K` | `200`, `60` | |
| `DAM_AI_IMAGE_MIN_COSINE`, `DAM_AI_TEXT_MIN_COSINE`, `DAM_AI_KNN_MARGIN` | `0.22`, `0.80` (self-hosted) / `0.30` (OpenAI-compatible), `0.04` | vector-leg relevance floors and margin |
| `DAM_AI_RETRIES`, `DAM_AI_RETRY_BASE_MS`, `DAM_AI_FAILURE_THRESHOLD`, `DAM_AI_COOLDOWN_SECONDS` | `3`, `400`, `3`, `30` | provider resilience |
| `DAM_AI_EMBED_TIMEOUT_SECONDS`, `_TAG_`, `_OCR_`, `_TRANSCRIBE_` | `30`, `60`, `120`, `1200` | per-call timeouts |
| `DAM_AI_RENDITION_WAIT_SECONDS` | `60` | how long enrichment waits for the rendition consumer's output before making the rendition itself |
| `DAM_AI_BACKFILL_CHUNK`, `DAM_AI_BACKFILL_CRON` | `200`, unset | backfill job chunk; catch-up schedule (Quartz cron) |

In development `appsettings.Development.json` sets `DAM_AI_PROVIDER=self-hosted` and `DAM_AI_SIDECAR_URL=http://localhost:18090`. A tenant's `enabled` stays false until turned on.

## Consequences
- Photographs, scans, recordings and documents become findable by what they show and say, with a self-hosted default that keeps every byte in the installation.
- The text vector changed from 1,024 to 384 dimensions and the vector engine from nmslib to Lucene: every deployment reindexes once (`POST /ai/reindex`).
- Semantic image search needs image embeddings, which only the self-hosted provider (or a sidecar-contract service) offers; the OpenAI-compatible provider searches by the meaning of words only.
- Vector-leg relevance floors are heuristics and need tuning against a deployment's real content.
- Semantic result sets are paged from a re-computed list and are not stable across pages while content changes.
- Tag-only `PATCH` edits and renames still raise no event, so they are not re-indexed until another change; fixing that touches the audit counts of unrelated commands and is left for its own change.
- Translation and summarisation (Step 2.2) can use the declared interface methods without further plumbing.

## Tests
Unit: RRF math (ties, missing legs, `k`), tag filtering, chunking, vector mean and normalisation, embedding (de)serialisation, audio capping, language codes, run states, `AddTags`, options parsing and
off-site detection, provider capability flags and selection, the cool-down breaker, mapping dimensions and document building. Provider stubs (in-process servers, no containers): both providers' request and response mapping,
retries on a busy service, the cool-down, non-seekable streams, Azure style, and that the API key never appears in a log or an error even when the service echoes it. Integration (real Postgres, RabbitMQ, SeaweedFS, OpenSearch):
images, videos, recordings and PDFs enriched through the real ingest pipeline and indexed; idempotency; AI off sends nothing; a provider outage retries, fails the run and leaves ingest untouched, then a re-run recovers; one refused
step fails only itself; the same enrichment through both providers chosen per tenant, and switching a tenant over; backfill in chunks; accept, reject and accept-all with audit; semantic search with deterministic embeddings (a query
that matches no tag or text finds the right picture; fusion puts the item good in two legs first; the access filter holds in every leg; graceful fallback; offset paging; reindex restores vectors from the database; a size mismatch
degrades instead of failing); settings, permissions, off-site acknowledgement, and row-level security and tenant isolation of every new table.

## Console
- **Capabilities** (`GET /ai/capabilities`, any signed-in user): drives whether Smart search and the AI panel are offered, with no provider URLs, keys or model names.
- **Asset page, AI panel** (Details tab, after Tags): suggested tags with confidence, per-tag accept/dismiss and "Add all", a language suggestion, the transcript
  (timestamped lines, filter, copy, click-to-seek and playhead highlight on a playable video, plain timestamps otherwise), extracted text with copy, run details and a
  privacy line ("analysed on your own servers" vs the provider's name). A `?t=<seconds>` link seeks the video and highlights the line. A freshly uploaded file
  re-checks for two minutes so the panel fills in by itself. Readers see the data but not the decision or re-run controls.
- **Search page, Smart search**: a switch (shown only when `semanticSearch` is true, default on, in the URL as `?smart=` and remembered), "why it matched" chips
  (Text, Visual match, Text in image, Spoken in recording), spoken moments as `?t=` links, quiet notes for each `semanticFallback`, a grid/list switch and example
  queries. ThumbnailGrid gained an optional `footer`. Saved searches do not store the Smart setting yet (the collection endpoints ignore it).
- **Admin page** (`/admin/ai`, `ai.manage`): status and health, vector-index rebuild callout, provider and privacy controls (an off-site provider needs the explicit
  acknowledgement), feature switches, OCR language, connection test and a backfill card with an estimate and a confirm step.
- Known gaps: audio has no player stage (transcript timestamps are not seekable), audio cards show a broken thumbnail (the batch thumbnail link 404s for audio -
  pre-existing), Hindi strings fall back to English.
