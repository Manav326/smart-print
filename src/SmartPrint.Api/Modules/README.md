# API Modules

This directory contains the capability-oriented modules introduced by the API refactoring.

## Required boundary

Each module owns a cohesive business capability. Add only the folders the capability actually needs:

```text
Modules/
  <Capability>/
    Abstractions/     # interfaces/contracts intentionally consumed by other modules
    Contracts/        # module-owned commands, queries and integration DTOs
    Domain/           # pure policies, rules and value types
    Endpoints/        # HTTP route adapters only
    Services/         # application workflows and orchestration
    Persistence/      # module-owned EF/persistence details, when needed
    Infrastructure/   # external adapters, when needed
    Registration/     # explicit dependency-injection registration entry point
    README.md         # module purpose, ownership and invariants
```

Do not create empty folders or force every module to use every layer. Keep the namespace aligned with the folder path:
`SmartPrint.Api.Modules.<Capability>.<Layer>`.

## Dependency direction

- A module may use its own layers according to the API Refactoring Guidelines.
- A module may consume another module only through that module's `Contracts` or `Abstractions`.
- Do not reference another module's endpoint, service, domain, persistence or infrastructure implementation.
- `Program.cs` is the composition root and may reference a module's `Registration` entry point only.
- Domain rules must not depend on ASP.NET Core, EF Core, persistence or application services.
- Contracts must remain independent of API endpoints, EF entities, persistence and application services.
- Endpoint modules must delegate writes and transaction boundaries to application services.
- Shared code belongs outside feature modules only when it is genuinely cross-cutting and has no dependency on a feature module.

## Enforcement

The API test project contains `ApiModuleArchitectureTests`. It scans this directory and enforces folder/namespace alignment, cross-module dependency boundaries, layer restrictions, endpoint write restrictions and composition-root imports. Add or update those tests whenever a new boundary rule is introduced.

These tests intentionally guard newly extracted modules. The existing pre-refactoring implementation remains in place until each capability can be moved without changing runtime behavior. Do not use the legacy layout as a reason to add new feature logic to `Program.cs` or to bypass module rules.
