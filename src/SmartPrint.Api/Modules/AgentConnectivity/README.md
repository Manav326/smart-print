# Agent connectivity module

## Ownership
Owns authenticated Agent heartbeats and Windows printer inventory synchronization. The existing Agent route-group authentication, tenant-active gate and per-device rate limiter remain in the composition root and are applied before these endpoints execute.

## Invariants
- An endpoint may use only the verified Agent device placed in `HttpContext.Items` by the existing route-group authentication filter; it must not trust Agent IDs supplied by a request.
- Heartbeats normalize local endpoints, require valid RSA SubjectPublicKeyInfo keys in the RSA-2048 through RSA-4096 range, clear a stale encryption key when the Agent cannot provide one, and zero temporary key bytes.
- Local storage snapshots are persisted only when all counters are non-negative and their measurement time is within the existing one-hour-past / one-minute-future window.
- Printer discovery accepts at most 100 queue records and isolates malformed queue metadata so one bad driver does not hide valid installed queues.
- Discovery priority remains PHYSICAL, PDF_EXPORT, then unsupported virtual; ready/busy and Windows-default queues win ties.
- Every valid discovered queue remains visible regardless of plan capacity. Only supported, ready/busy queues may be auto-selected into unused plan slots.
- Rescans never override the owner's selection. Known driver capabilities survive metadata-only syncs and are cleared when the driver changes without a new capability snapshot.
- Partial/malformed inventories never mark unreported queues unavailable. Tenant quota locking, tenant scoping and the existing response fields remain unchanged.

## Dependencies
Uses the existing authenticated `AgentDevice` context, shared EF model, `TenantEntitlementService`, and Agent connectivity rules. No schema or migration changes are introduced.
