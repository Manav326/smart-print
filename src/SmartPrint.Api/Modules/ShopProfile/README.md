# Shop profile module

## Ownership
Owns the shop profile read and update endpoints, including the owner identity shown in the profile view.

## Invariants
- Profile GET remains owner-only; profile PUT remains available to the existing owner/manager roles.
- Both operations resolve the shop tenant from the authenticated `tenant_id` claim.
- Preserve the profile owner/tenant match check and the active-tenant requirement on GET.
- Preserve display-name, six-digit hex accent-colour and optional HTTPS logo validation.
- Custom logos or non-default accent colours require the current plan's server-authoritative white-label entitlement.
- Persist only the existing profile fields; no schema or public response changes are introduced.

## Dependencies
Uses shared `TenantIdentity` and `ShopIdentity` claim resolvers, pure profile validation rules, the existing Identity user store and `TenantEntitlementService`.
