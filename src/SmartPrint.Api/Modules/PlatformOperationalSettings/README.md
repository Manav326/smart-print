# Platform operational-settings module

## Ownership
Owns platform-wide operational quota, retention and feature-policy settings, including audit history.

## Invariants
- Access is restricted by the platform-admin authorization on the parent route group.
- Updates are validated before persistence and serialized by a row lock.
- Every successful update records its actor, reason and before/after snapshot in the audit table.
- Missing singleton settings fail closed with HTTP 503.
- No settings or audit schema changes are part of this extraction.

## Dependencies
The service uses the existing EF Core DbContext and legacy operational-settings entity. Validation rules are pure and live in the module Domain layer.
