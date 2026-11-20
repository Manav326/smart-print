# Platform tenants module

## Ownership
Owns platform tenant lifecycle changes, including the status-transition use case.

## Invariants
- Routes remain under the platform-admin authorization group.
- Only the existing Active/Suspended/Inactive transitions are accepted.
- Permanently inactive tenants cannot be reactivated.
- A tenant cannot be permanently deactivated while unresolved print jobs, pending customer payments or outstanding refunds for rejected paid work remain.
- Transition and safety checks execute under the existing tenant row lock and one database transaction.
- Successful changes write the same actor/reason/status audit record.
- Repeated requests for the current status remain idempotent and return the existing duplicate-request response.

## Dependencies
Uses the shared tenant status domain rule and existing EF Core entities. This extraction introduces no schema or payment/print workflow changes.
