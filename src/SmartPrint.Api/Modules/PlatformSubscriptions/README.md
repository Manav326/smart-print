# Platform subscriptions module

## Ownership
Owns read-only platform subscription listing and per-tenant subscription-history queries.

## Invariants
- Routes remain under the platform-admin authorized route group.
- Preserve pagination bounds, search validation, active/scheduled subscription selection and history ordering.
- Subscription assignment and other state changes remain in the existing legacy endpoint until their transaction workflow is moved into a service.

## Dependencies
Queries use the existing EF Core model and pure entitlement rules while the broader platform-subscription workflow is migrated incrementally.
