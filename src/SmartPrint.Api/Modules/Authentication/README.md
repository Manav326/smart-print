# Authentication module

## Ownership
Owns Google challenge/configuration and shop/customer Google sign-in HTTP routes, plus the Google-session creation workflow.

## Invariants
- Preserve nonce issuance, surface binding, expiry and one-time consumption.
- Verify Google credentials before account linking or session issuance.
- Preserve shop/customer identity separation and account-type checks.
- Persist only the refresh-token hash; never log raw tokens or credentials.
- Keep the existing route paths, public response names and authentication error codes stable.

## Dependencies
Google identity verification, Identity account persistence and token issuance reuse the established shared API services and security components. Refresh-token persistence is owned by `GoogleAuthSessionService`.
