# Platform plans module

## Ownership
Owns read-only platform subscription-plan catalog queries.

## Invariants
- Routes remain under the platform-admin authorized route group.
- Preserve plan ordering, version ordering and the response contract.
- Plan creation, draft changes and publishing remain separate write workflows until service extraction.

## Dependencies
The query uses the existing EF Core plan model. The write workflows continue to use the established plan-domain policies.
