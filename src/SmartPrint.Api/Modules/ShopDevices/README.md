# Shop devices module

## Ownership
Owns shop printer and paired-Agent lifecycle, QR-station creation, code rotation and enable/disable operations.

## Invariants
- Every shop mutation is authorized by the existing shop owner/manager role and scoped to the verified tenant claim.
- Agent enrollment codes remain cryptographically random, expire after ten minutes, and are stored only as a one-way hash.
- Agent listing remains tenant-scoped; online status uses the existing two-minute heartbeat window and reports revoked devices distinctly.
- Revoking an Agent is idempotent and marks its linked printer queues as `AgentRevoked`; it does not delete the Agent or its printer history.
- Printer creation validates the same connection types, capabilities JSON shape/size and metadata lengths.
- Manual printer entries remain visible placeholders and do not consume a selected-printer slot until a supported Windows queue is linked and selected.
- Printer selection/replacement requires a fresh, ready Agent and Windows queue; swaps preserve QR-station defaults and block active/uncertain output, local-only files tied to another computer, and partially completed batches.
- Printer and QR-station mutations keep their existing tenant quota locks and capacity checks.
- QR station creation requires the canonical public HTTPS root and an active tenant-owned supported-output printer when one is selected.
- QR public codes remain cryptographically random; only their hash is used for lookup and rotation invalidates the previous code.
- Preserve route paths, response fields, validation keys/messages and quota conflict details.

## Dependencies
Uses shared `ShopIdentity`, `TenantIdentity`, `Base64UrlTokenEncoder`, `OpaqueTokenHasher`, the existing tenant quota/entitlement service and EF Core model. No printer, Agent or station schema changes are introduced.
