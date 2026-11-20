# Platform advertising module

## Ownership
Owns read-only platform campaign, creative-preview and campaign-history queries, public active-ad/creative reads, and the shared campaign response projection.

## Invariants
- Admin reads remain under the platform-admin authorized route group.
- Preserve campaign ordering, event counts, creative lookup and response field names.
- Creative previews and public creative delivery continue to use the existing storage adapter.
- Public ad reads remain anonymous, rate-limited and cache-safe.
- Campaign, creative and public impression/click writes remain in the legacy endpoint until their audit and storage workflows are extracted together.

## Dependencies
The query layer reads existing EF Core entities and reuses the established advertising domain rules and creative storage implementation.
