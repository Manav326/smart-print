# Customer module

## Ownership
Owns signed-in customer profile, job count/history, job status and order/batch tracking queries.

## Invariants
- All routes remain authenticated.
- Resolve customer identity from verified claims; never trust a customer or tenant id supplied by the caller.
- Every job, batch, timeline, document metadata and payment query remains scoped to the owning customer identity, including ownership through the originating customer session.
- Preserve zero-based page/size semantics, invalid-page responses and existing history ordering.
- Mask private-printing source filenames as `Private document`.
- This module is read-only; payment verification, refunds and print execution remain outside customer query handlers.

## Dependencies
The module reuses the established CustomerIdentity policy and EF Core model. Read-only queries remain close to the HTTP adapter where their ownership scope is explicit.
