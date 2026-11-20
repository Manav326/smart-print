# Shop workspace module

## Ownership
Owns read-only shop workspace queries for the current subscription/entitlements, shop profile, printer inventory, QR-station inventory and effective pricing.

## Invariants
- All routes remain inside the authenticated shop route group and inherit its tenant-active filter.
- Resolve tenant scope from the verified `tenant_id` claim, never from caller-supplied tenant identifiers.
- Preserve subscription entitlement calculations, the active-owner profile check, printer/QR station ordering and pricing response shape.
- This module performs reads only. Printer activation, QR-code rotation, profile/pricing updates and print-job/payment state changes remain separate write workflows.

## Dependencies
Uses the shared `TenantIdentity` claim resolver, existing EF Core entities and the authoritative `TenantEntitlementService`.
