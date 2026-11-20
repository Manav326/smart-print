# Shop payments module

## Ownership
Owns read-only shop payment configuration, payment ledger and refund history queries, plus the shop payment-method settings update use case.

## Invariants
- All routes remain inside the authenticated shop route group and inherit the existing tenant-active filter.
- Every profile, payment, batch allocation, print job, document and refund lookup remains scoped to the tenant claim.
- Preserve the existing payment aggregation, batch grouping, amount/refund totals, ordering and 100-row response cap.
- The payment settings response never exposes provider secrets or internal persistence fields.
- Payment settings can only be changed by the existing shop owner/manager roles.
- UPI normalization, ID format/length, payee name and minimum-payment-method validation retain their previous rules and validation messages.
- Payment verification/rejection and refund creation remain outside this module and retain their existing transaction/service boundaries.

## Dependencies
Uses shared `TenantIdentity` and `ShopIdentity` claim resolvers, pure payment-settings rules and the existing EF Core payment/print model.
