# Shop account module

## Ownership
Owns the authenticated shop account update endpoint for owner name and sign-in email.

## Invariants
- The existing shop owner/manager role and verified tenant claim gate remain at the HTTP boundary.
- The account user id comes only from the verified subject claim; a caller-supplied id is never accepted.
- Owner-name/email normalization and length validation reuse `ShopOwnerAccountRules`.
- Changing email still requires the current password, honors Identity lockout, rejects another account's email and updates both Email and UserName.
- Display-name/email persistence remains one Identity update inside a database transaction.
- Preserve the authentication rate limit and all existing validation, locked-out and conflict responses.

## Dependencies
Uses the existing ASP.NET Identity stores, shared `ShopIdentity`/`TenantIdentity` claim resolvers and the current API DbContext; no user/tenant schema changes.
