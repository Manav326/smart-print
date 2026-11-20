# Shop payments module

## Ownership
Owns read-only shop payment configuration, payment ledger and refund history queries.

## Invariants
- All routes remain inside the authenticated shop route group and inherit the existing tenant-active filter.
- Every profile, payment, batch allocation, print job, document and refund lookup remains scoped to the tenant claim.
- Preserve the existing payment aggregation, batch grouping, amount/refund totals, ordering and 100-row response cap.
- The payment settings response never exposes provider secrets or internal persistence fields.
- Payment verification/rejection, refund creation and payment-settings updates remain outside this query module and retain their existing transaction/service boundaries.

## Dependencies
Uses the shared `TenantIdentity` claim resolver and the existing EF Core payment/print model. The three handlers are read-only and retain their route-specific query projections.
