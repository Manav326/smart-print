# Platform administration module

## Ownership
Owns the read-only platform overview, paginated tenant directory and tenant-status audit-history queries.

## Invariants
- Routes remain under the platform-admin authorized route group.
- Preserve tenant search/status filters, pagination bounds, online-agent freshness windows and subscription-state projections.
- Keep tenant status transitions and audit writes in their separate transaction workflow.
- Preserve existing anonymous/authorized route semantics and response field names.

## Dependencies
Queries use the established EF Core model and tenant/subscription domain rules during the staged migration.
