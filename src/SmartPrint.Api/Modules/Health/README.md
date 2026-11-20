# Health module

## Ownership
Owns the API liveness, readiness and root-discovery HTTP routes.

## Invariants
- Preserve the existing `/health/live`, `/health/ready` and `/` paths and response shapes.
- Liveness reports process availability; readiness checks database connectivity.
- These endpoints remain anonymous and must not expose tenant or customer data.
- No database writes or business workflow orchestration belong here.

## Dependencies
The readiness endpoint reads the existing API DbContext. This is a deliberate legacy persistence dependency while the module migration is incremental.
