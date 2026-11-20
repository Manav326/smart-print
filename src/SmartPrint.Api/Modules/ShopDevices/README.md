# Shop devices module

## Ownership
Owns shop printer creation/activation and QR-station creation, code rotation and enable/disable operations.

## Invariants
- Every mutation is authorized by the existing shop owner/manager role and scoped to the verified tenant claim.
- Printer creation validates the same connection types, capabilities JSON shape/size and metadata lengths.
- Printer and QR-station creation acquire the existing tenant quota lock and keep capacity checks, entity insert and commit in one transaction.
- A printer cannot be disabled while one of its jobs is active in the Windows Agent/spooler; deactivation never pretends to cancel output.
- QR station creation requires the canonical public HTTPS root and an active tenant-owned supported-output printer when one is selected.
- QR public codes remain cryptographically random; only their hash is used for lookup and rotation invalidates the previous code.
- Preserve route paths, response fields, validation keys/messages and quota conflict details.

## Dependencies
Uses shared `ShopIdentity`, `TenantIdentity`, `OpaqueTokenHasher`, the existing tenant quota/entitlement service and EF Core model. No printer, station or quota schema changes are introduced.
