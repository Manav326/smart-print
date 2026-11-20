# Authentication module

## Ownership
Owns Google sign-in, refresh-token creation/rotation and logout revocation for customer/shop authentication surfaces.

## Invariants
- Google login keeps nonce, issuer/audience, account-type and tenant-role checks in the existing Google identity implementation.
- Refresh tokens are stored as SHA-256 hashes; raw tokens are never persisted.
- Refresh is single-use: the old token is atomically claimed/revoked, a replacement is created and linked, and both are committed in one transaction.
- Only eligible shop, global customer and platform-administrator principals can refresh.
- Refresh tokens remain valid for 30 days and public endpoints preserve anonymous access, 401/200/204 responses and the existing token response fields.
- Logout revokes only the matching active refresh-token hash and remains idempotent.

## Dependencies
Uses a module-owned identity abstraction implemented by an ASP.NET Identity infrastructure adapter, the existing `TokenIssuer`, EF Core and shared customer/platform authorization policies.
