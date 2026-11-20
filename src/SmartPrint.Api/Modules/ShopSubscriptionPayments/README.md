# Shop subscription-payments module

## Ownership
Owns shop-side subscription payment configuration, initiation and payment status routes.

## Invariants
- Preserve shop tenant scoping and authorization from the parent route group.
- Keep subscription entitlement and payment verification server-authoritative.
- Do not treat a browser callback as proof of payment.
- Preserve idempotency and the existing payment status contract.

## Dependencies
Payment provider operations and subscription rules remain in the established subscription service and domain components; the endpoint module is an HTTP adapter.
