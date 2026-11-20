# Platform data-transfer module

## Ownership
Owns platform-administrator data-transfer routes and their HTTP response mapping.

## Invariants
- Preserve platform-admin authorization from the parent route group.
- Keep tenant identity and transfer scope explicit.
- Keep transfer recording and lifecycle operations in the existing application services.
- Do not expose raw credentials or sensitive transfer payloads in responses or logs.

## Dependencies
The module delegates transfer workflows to the existing data-transfer services and uses the shared API persistence model during the incremental migration.
