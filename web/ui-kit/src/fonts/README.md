# Vendored fonts

`Geist-Variable.woff2` and `GeistMono-Variable.woff2` are variable-weight builds of
[Geist](https://vercel.com/font) (Vercel, in collaboration with Basement Studio), SIL Open Font
License 1.1 (`LICENSE.txt`). Vendored directly rather than depending on the `geist` npm package at
runtime - that package's JS wrapper is built for `next/font` (Next.js-specific) and unusable from a
plain Vite app; only the static font files are needed here. Self-hosted (not loaded from a font CDN)
so the console keeps working offline/air-gapped, matching the spec's own on-prem deployment story
(Phase 4's offline bundle).

Extracted from `geist@1.7.2` (`node_modules/geist/dist/fonts/{geist-sans,geist-mono}/*-Variable.woff2`).
To update: `pnpm --filter @dam/ui-kit add -D geist@latest --config.minimumReleaseAge=0`, copy the two
`*-Variable.woff2` files and `LICENSE.txt` here, then remove the npm dependency again
(`pnpm --filter @dam/ui-kit remove geist`) - it's a source for these two files, not a runtime dependency.
