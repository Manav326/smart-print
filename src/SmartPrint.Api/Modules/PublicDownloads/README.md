# Public downloads module

## Ownership
Owns public release availability and versioned/unversioned download HTTP routes.

## Invariants
- Keep the same `/downloads/status`, `/downloads/{platform}` and `/downloads/{version}/{assetName}` routes.
- Never expose upstream redirect locations to the browser.
- Preserve no-store status responses, bounded error mapping and client-cancellation handling.
- Preserve stream transfer and version pinning semantics.
- All download proxying remains implemented by the existing `PublicDownloadService`; this module must not create a parallel download pipeline.

## Dependencies
The module is an HTTP adapter over the established `PublicDownloadService`.
