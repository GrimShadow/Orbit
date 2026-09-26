# ADR-0006: Delivery origin, signed links and renditions

Status: accepted (Step 1.5a)

## Context
Channels (apps, websites, signage) must fetch derived files (thumbnails, web versions, exact crops) quickly, from many places, without
holding a user session, and without the API ever carrying file bytes (ADR-0004). The CDN in front needs predictable cache keys.

## Decision
- **A separate host, `Dam.Delivery`,** serves `GET /d/{token}/{assetId}/{rendition}` and `/d/{token}/{assetId}/t?w=&h=&fit=&fmt=&q=&focal=`.
  It is stateless, scales on its own, and is the only thing exposed to the public network besides the API.
- **The token is the authorisation.** `POST /assets/{id}/links` checks the caller's rights once (`assets.read`; `assets.download.original`
  for the original) and returns HMAC-SHA256 signed links carrying: tenant, asset, scope, expiry, optional client IP and a watermark flag.
  Scope is either a list of named renditions or the exact canonical transform, so changing any parameter, adding one, or reusing a
  rendition link for a transform breaks or refuses the request (403). Expired links answer 410. Tokens last one hour by default, at most
  24 hours. `DAM_DELIVERY_SIGNING_KEY` accepts several comma-separated keys: the first signs, all verify, so keys rotate without breaking
  links already issued.
- **Links follow the asset, not a frozen file.** Delivery serves the asset's *current* version at request time, so a new version reaches
  channels without re-issuing links; ETags and cache keys include the version id, so caches refresh by themselves.
- **Unpublished means watermarked.** If the asset is not `published` when the link is made, or when it is fetched, every image is
  watermarked and marked `Cache-Control: private`. The original of an unpublished asset is refused outright. The watermark is drawn
  from a built-in bitmap font so it needs no system fonts and looks the same in every container.
- **Renditions are deterministic and cached.** Keys are `{tenant}/{asset}/{version}/{profile}.{ext}` (named) or
  `.../x/{hash of the canonical request}.{ext}` (transforms, watermarked variants) in `dam-renditions`. The worker makes the image
  profiles when a version arrives (`RenditionConsumer`); anything missing is made on first request under a per-key lock. The
  renditions bucket is disposable: everything in it can be regenerated. `X-Dam-Cache: hit|miss` lets operators measure the hit ratio.
- **Safety.** All output is stripped of metadata (no camera or GPS details leak), sent with `nosniff` and a sandboxing CSP, and any
  non-media type is forced to download. Transforms are capped (8192 px edge, 200 MB source). SVG is not rasterised.
- **Row-level security still applies:** the origin sets the tenant from the token, so a token can never reach another tenant's row.

## Not in this step (1.5b)
Video (FFmpeg), PDF pages (Poppler) and Office (LibreOffice) renditions, `signage_*` profiles, AVIF in the standard profile set, and HTTP
range requests for video. Those are added as more profile types behind the same service and links.

## Consequences
- Adding a channel needs only an API client that can call `POST /assets/{id}/links`.
- A leaked link is limited by its scope, expiry and (optionally) address; revoking everything at once means rotating the signing key.
