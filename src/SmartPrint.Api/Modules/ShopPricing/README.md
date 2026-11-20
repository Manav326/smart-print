# Shop pricing module

## Ownership
Owns shop pricing updates for mono/colour, duplex, photo formats and ID-document prices.

## Invariants
- Only existing shop owner/manager roles can change pricing, and tenant scope comes from the verified claim.
- Preserve the 100,000 minor-unit rate cap and 10,000,000 minor-unit minimum-charge cap.
- Mono and colour per-side prices remain strictly positive; duplex/photo rates remain non-negative.
- A null ID-document price keeps the previously configured value, matching existing behavior.
- Successful updates mark pricing configured, increment the version exactly once and update its timestamp.
- All monetary values remain in minor currency units (paise for INR).

## Dependencies
Uses shared `ShopIdentity`/`TenantIdentity` claim resolvers and the existing EF Core shop pricing entity. Validation is pure and does not introduce a pricing engine duplicate.
