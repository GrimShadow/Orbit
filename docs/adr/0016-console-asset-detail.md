# ADR-0016: Console asset detail - preview, metadata, versions, lifecycle (Step 1.8c)

## Status
Accepted

## Context
Step 1.8c is the third console sub-step (1.8a: typed API client + library tree/grid; 1.8b: drag-drop bulk uploader).
The spec's screen list asks for "asset detail (preview player for image/video/PDF, versions, metadata form generated
from schema, taxonomy pickers, rights, lifecycle actions)". Every backend contract this page needs
(`GET/PATCH /assets/{id}`, `POST /assets/{id}/transitions/{action}`, `GET /assets/{id}/versions`,
`POST /assets/{id}/versions/{versionNo}/restore`, `POST /assets/{id}/links`, `GET /schemas/{assetType}`,
`GET /vocabularies`, `GET /vocabularies/{id}/terms`) already existed before this step; only the console UI was missing.

## Decisions

**"Rights" is scoped out entirely - there is no backend for it.** The spec line groups "rights" in with the other
asset-detail concerns, but nothing in the domain, API, or OpenAPI surface defines a Rights entity, DTO, or endpoint
anywhere in the codebase (confirmed by search, not assumption). Building a rights UI now would mean inventing a
backend contract from the console side, which this step does not do. It is left for whichever future spec step
actually introduces that backend surface; nothing here blocks it.

**The metadata form always sends the full metadata document, because `UpdateAsset` replaces it wholesale.**
`UpdateAssetHandler` treats a non-null `metadata` in `UpdateAssetRequest` as a full replacement, not a merge. The form
therefore seeds its local editable state from the asset's current `metadata` object in full (not just the current
schema's fields) and only ever overwrites the keys its own inputs render - fields belonging to an older schema version
that the current schema no longer declares are carried through unchanged in the saved payload rather than silently
dropped.

**Taxonomy/vocabulary terms are a flat, indented `<select>`, not a real tree widget.** `ListTerms` returns a
`TermNodeDto` tree (self-referential, so the OpenAPI generator can only type children as `unknown[]`). Reusing
`ui-kit`'s `Tree` component (built for folders, which need expand/collapse and drag targets) would be over-built for
picking one or more terms. Terms are flattened depth-first client-side with `—`-repeated indentation prefixes, the same
pragmatic convention `folderOptions` already uses elsewhere in the console - good enough for a picker, not a
general-purpose hierarchy widget.

**Multilingual (`ml_text`) fields always edit a fixed set of five languages, not the console's current UI language.**
`ML_LANGUAGES = ['en', 'hi', 'mr', 'ta', 'te']` mirrors the language set the search index analyzes per-field (ADR-0011)
- metadata content is tenant/asset data independent of whichever language the person editing it happens to be viewing
the console in, so every `ml_text` field always shows all five inputs rather than just the active UI locale's.

**Lifecycle action buttons are a client-side UX convenience, not an authorization decision.** `lifecycle.ts` mirrors
`Dam.Domain.Assets.AssetLifecycle`'s transition table (`draft -> in_review -> approved -> published -> expired ->
archived`, `reject` back to `draft`, `withdraw` from any of the non-draft states back to `draft`) purely to decide
which buttons to show. The server re-validates every transition itself - permission, required metadata, workflow state
- so a button that turns out not to be allowed just surfaces the server's own rejection; nothing here is a security
boundary, and the duplication is small and low-drift-risk (a fixed enum-like state machine, not business logic that
changes per tenant).

**The preview player switches on the asset's declared `type`, not on sniffed content or MIME.** Image and video get
native `<img>`/`<video>` elements; `document`/`manual` (the spec's two PDF-shaped types) get an `<iframe>`; every other
type (audio, 3d, html5, other) falls back to a plain download link rather than a bespoke player, since the spec only
asks for image/video/PDF preview specifically. All three paths request the same short-lived signed delivery link
(`POST /assets/{id}/links`, ADR-0006) rather than any tenant- or storage-specific URL construction - watermarking for
unpublished assets is entirely the delivery origin's concern and the console never needs to branch on it.

## Consequences
- The asset detail route (`/library/assets/:id`) is reachable from both library views: the grid's `ThumbnailGrid`
  `onOpen` and the list view's title column (rendered as a `Link`) both navigate there.
- A future "rights" backend step will need its own ADR when it lands; this one explicitly does not reserve any client
  shape for it.
- The flat term picker is an intentional MVP; if a tenant's vocabulary grows deep enough that flat indentation stops
  being usable, that is a signal to build a proper hierarchical picker in `ui-kit`, not to special-case this screen.
