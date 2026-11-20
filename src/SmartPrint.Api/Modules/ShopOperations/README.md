# Shop operations module

## Ownership
Owns read-only shop customer directory/history, print-job list, dashboard summary and print-job event-history queries.

## Invariants
- Every route remains inside the authenticated shop route group and inherits the existing tenant-active filter.
- Resolve tenant scope from the verified tenant claim; never trust caller-supplied tenant identifiers.
- Preserve customer/session ownership joins, private-printing filename masking, status projections, pagination and stable sort order.
- Preserve the existing financial aggregates and output-completion history fields.
- This module performs reads only. Print dispatch, approval/rejection, refunds, payment verification, document access and physical-output confirmation remain separate workflows.

## Dependencies
Uses the shared `TenantIdentity` claim resolver and existing tenant-owned EF Core model. Query projections remain in the endpoint layer because they are read-only, route-specific and require tenant scope to be visible at the boundary.
