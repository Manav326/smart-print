# API Refactoring Guidelines

**Status:** Working agreement for the API modularization effort  
**Applies to:** `src/SmartPrint.Api`, API tests, API-facing portal contracts and related documentation  
**Baseline:** The `refactor/api-modularization-current-main` branch, created from the verified `main` commit  
**Audience:** Anyone changing, reviewing or testing the SmartPrint API

These guidelines are the engineering contract for this refactoring. They define how the API is to be decomposed, documented, tested and reviewed. They do not authorize a product redesign, a change to existing API behavior, or a rewrite of the printing lifecycle.

## 1. Goals and non-goals

### Goals

- Make the API understandable by organizing code around clear business capabilities.
- Keep HTTP concerns, application workflows, domain rules, persistence and security responsibilities separate.
- Make important behavior discoverable through accurate names, comments, XML documentation, module notes and tests.
- Preserve existing API contracts, tenant isolation, privacy, financial integrity, print safety and operational behavior.
- Make each extraction small enough to review, test and revert independently.
- Keep the application a modular monolith with explicit internal boundaries.

### Non-goals

- Do not introduce microservices, a message bus, CQRS infrastructure, MediatR, a generic repository layer, a service locator, or a new framework merely to make the folder tree look modular.
- Do not create a second API, a second print-job state machine, a parallel payment/refund pipeline, or duplicate copies of business rules.
- Do not change route names, payloads, status codes, database meaning, pricing, retention, permissions or print behavior as incidental effects of code movement.
- Do not split the API into one project per endpoint or create empty abstractions that have no independent responsibility.
- Do not combine a large behavioral change with a structural extraction unless the behavior change is separately justified, tested and documented.

A modular monolith is the default. A new deployable service or project requires a separate, evidence-based architecture decision.

## 2. Non-negotiable engineering principles

1. **Current main is the source of truth.** Start from the current refactoring branch and keep it synchronized with approved changes from `main`. PRs 38 and 80 are historical references only; do not transplant their branch contents wholesale.
2. **Behavior before file movement.** Record the current behavior and its tests before extracting code. A refactor is successful only when behavior is preserved and the resulting design is clearer.
3. **One owner for each rule.** A business rule must have one authoritative implementation. Reuse it instead of copying it into endpoints, services, the portal or the Agent.
4. **Explicit boundaries.** Tenant, customer, shop, platform, Agent, payment, document and printer ownership must remain explicit at every boundary.
5. **Fail closed for sensitive operations.** Missing, stale, ambiguous or unverifiable evidence must not be converted into permission, payment success, document access or physical completion.
6. **Small, reviewable changes.** Prefer a bounded capability at a time over a large mechanical rewrite.
7. **Document decisions, not noise.** Comments and documentation must explain intent, invariants, side effects, constraints and operational consequences—not restate obvious syntax.

## 3. Target API structure

Use the existing API project and its established namespaces. Organize new and extracted code consistently by responsibility and business capability. Prefer clear capability subfolders over a flat directory containing hundreds of unrelated files.

The following is the target shape; it is a guide, not an instruction to create empty folders or move every file in one commit.

- `Program.cs`: application composition root, configuration validation, middleware and top-level startup only.
- `Endpoints/<Area>/`: route registration, HTTP binding, authorization metadata and HTTP response mapping.
- `Services/<Area>/`: application workflows that coordinate domain rules, persistence and external boundaries.
- `Domain/<Capability>/`: deterministic business rules, value policies and state-transition decisions.
- `Contracts/<Area>/`: explicit request/response DTOs and API contract types.
- `Security/`: token, credential, grant and authorization primitives that have a genuinely shared security responsibility.
- `Data/`: EF Core context, entity configuration, migrations and persistence-specific behavior.
- `Storage/`: document and other storage implementations and their explicit contracts.
- `Pricing/`: authoritative quote and rate calculation logic where that is the established owner.
- `tests/`: API unit, integration, migration, concurrency and contract coverage.

### Naming and grouping

- Group endpoint modules by the API area and capability, such as Customer, Shop/PrintJobs, Shop/Payments, Platform/Tenants, Agent/Uploads and Public/Quotes.
- Use descriptive names ending in their responsibility: `...Endpoints`, `...Service`, `...Rules`, `...Policy`, `...Request` and `...Response`.
- Keep a feature's related endpoint, service, rule and tests easy to find by using consistent capability names.
- Use namespaces that match the folder organization.
- Avoid vague names such as `CommonService`, `Manager`, `Helper`, `Processor` or `Utils` when a capability-specific name can communicate the responsibility.
- Do not create a class for every route. A cohesive endpoint module may register multiple routes belonging to one capability.
- Do not make a class responsible for unrelated customer, shop, platform and Agent workflows simply because they currently share a file.

## 4. Responsibility and dependency rules

The normal request flow is:

**HTTP endpoint → application service (when a workflow is needed) → domain policy/rules and persistence or external adapter → typed outcome → HTTP response**

Not every simple read requires every layer. Add a layer only when it provides a meaningful boundary, test seam, reuse point or orchestration responsibility.

### 4.1 Program.cs — composition root

Program.cs must progressively become a readable startup file. It may:

- Validate essential configuration and required secrets.
- Register framework and application services.
- Configure authentication, authorization, CORS, rate limiting, health checks and middleware.
- Map top-level route groups by calling endpoint-module registration methods.
- Preserve required bootstrap/administrative startup behavior.

It must not accumulate feature implementations, long route handlers, EF queries for individual features, transaction workflows or business rules.

Do not move configuration into opaque helper methods just to shorten Program.cs. Registration methods must have explicit names, predictable side effects and focused tests where behavior warrants them.

### 4.2 Endpoint modules — HTTP adapters

Endpoint modules own HTTP-specific work:

- Route path, HTTP verb, route-group attachment and API metadata.
- Binding and basic shape validation of route, query, header and body inputs.
- Authentication/authorization metadata and HTTP-specific access checks that belong at the boundary.
- Calling the appropriate application service.
- Mapping a typed application outcome to the established HTTP status code, headers and response body.
- Applying cache, no-store and other response headers when required.

Endpoint modules should not own multi-step business workflows, transaction orchestration, complicated EF write logic, duplicated state-transition rules or external side-effect recovery.

Read-only EF queries may remain close to a route when they are short, bounded, tenant-scoped and genuinely endpoint-specific. Move a query into a query service when it is complex, reused, security-sensitive, difficult to test or part of a larger application workflow. Do not create a service that only forwards one trivial query without adding value.

### 4.3 Application services — use-case orchestration

Services own application workflows that cross multiple steps or boundaries, including:

- Transaction and row-lock boundaries.
- Idempotency, replay and duplicate-request handling.
- State changes and associated audit/event persistence.
- Quota, subscription and entitlement checks that must be authoritative.
- Coordination of database work with storage, payment providers, Agents or other external systems.
- Retry/reconciliation decisions and recovery from ambiguous outcomes.
- Complex tenant-scoped reads or projections that deserve independent tests.

Services must not return ASP.NET `IResult` values or depend on route-specific response formatting. Return explicit result types or well-defined domain/application outcomes. Keep HTTP status-code mapping in the endpoint layer.

Inject dependencies explicitly. Do not access the service provider from application code or introduce a service locator. Keep service lifetimes correct: EF Core DbContext and workflows depending on it must not be registered as singletons.

A service should have one coherent reason to change. Split a large service when its use cases, policies or transaction boundaries are genuinely distinct—not merely because it has many lines.

### 4.4 Domain rules and policies — pure decisions

Domain rule classes should be deterministic wherever possible:

- Accept explicit values as inputs.
- Return an explicit decision, normalized value or failure code.
- Avoid hidden database, network, filesystem, clock or process dependencies.
- Avoid HTTP types, EF entities and UI concepts unless a boundary contract explicitly requires them.
- Keep state-transition, eligibility, validation, arithmetic, retention and authorization decisions in one authoritative location.

A rule that depends on database state belongs in the application workflow, with pure sub-rules extracted where useful. Do not disguise persistence checks as pure domain validation.

### 4.5 Contracts

- Keep externally visible request and response types explicit and intentional.
- Do not expose EF entities, storage manifests, internal exception objects or persistence implementation details as API contracts.
- Preserve JSON property names, nullability semantics, defaults, enum/string values and numeric units.
- Document amounts and units clearly (for example, paise/minor units versus rupees).
- Avoid reusing one DTO for unrelated surfaces if that couples their contracts or permissions.
- Do not rename or reshape a public response merely to match a new internal class name.

### 4.6 Persistence and external adapters

- EF Core remains the persistence mechanism; do not wrap every DbSet in a generic repository.
- Keep migrations explicit, reviewed and compatible with the deployed schema. A pure code extraction should not generate a migration.
- Put provider-specific payment, storage, Windows/Agent and HTTP mechanics behind a meaningful boundary when this makes the workflow testable or replaceable.
- External calls must have bounded timeouts, cancellation, clear failure mapping and explicit idempotency/reconciliation behavior.
- Never perform an irreversible external side effect before the durable intent and recovery strategy are understood.

## 5. API compatibility rules

Unless a change is explicitly approved as a separate product/API change, preserve:

- Route path, HTTP method and route-group behavior.
- Authentication scheme, authorization policy, role/claim requirements and anonymous access rules.
- Request field names, accepted values, validation boundaries and defaults.
- Response shape, JSON naming, status codes, error codes/messages where they are relied upon, and relevant headers.
- Pagination limits, sorting, filtering and date/time semantics.
- Idempotency keys, duplicate replay behavior and concurrency-conflict behavior.
- Tenant scoping, ownership checks and not-found versus forbidden behavior.
- Cache-control, no-store, file-download and content-disposition behavior.

Before moving an endpoint, capture its current contract and add or identify tests that prove it. Update portal source-contract tests only to follow a legitimate code-location change; do not delete or weaken the behavior assertion simply because the code moved.

If an intended API contract change is unavoidable, document it, identify every caller, add compatibility/migration handling where required, and keep it reviewable separately from unrelated extraction.

## 6. Security, privacy and product invariants

These are correctness requirements, not optional implementation details. Every extraction must preserve the applicable invariants and add regression coverage when the code boundary changes.

### Tenant and identity isolation

- Derive tenant identity from verified user membership or registered device identity—not from a client-supplied tenant ID alone.
- Include tenant ownership in reads, updates, deletes, downloads, payment lookups, Agent operations and audit records.
- Validate a requested resource against the authenticated tenant and owner.
- Keep global customer identity separate from shop identity. A customer's access at one shop must not expose their jobs or personal details at another shop.
- Platform-level operations must use explicit platform authorization and controlled cross-tenant access.

### Credentials and security boundaries

- Never log access tokens, refresh tokens, Agent secrets, raw one-use grants, credentials, document bytes or payment secrets.
- Store only the appropriate hash or protected representation of opaque credentials and grants.
- Keep one-use grants bound to their intended tenant, session, station, Agent/printer, file identity and expiry.
- Preserve signature, expiry, audience, issuer, replay and ownership checks.
- Do not widen CORS, local-network address ranges, Agent routes, firewall rules or anonymous access as a side effect of extraction.
- Do not downgrade HTTPS or other protected transport rules.

### Documents and Private Printing

- Preserve signature/content validation, file-size/page-count limits, hash verification, storage-mode checks and protected document access.
- Keep local-Agent documents bound to the authorized Agent/printer where required.
- Preserve Private Printing's source privacy, metadata masking, restricted output modes and purge/acknowledgement lifecycle.
- Never use a normal cloud document read path to bypass local-only or private-source restrictions.
- Keep cache and response headers that prevent protected document leakage.

### Payments, quotes and refunds

- The server remains authoritative for price, currency, quote snapshots, payment state, entitlement and refund calculations.
- Preserve immutable quote/payment allocations and integer minor-unit arithmetic.
- Keep customer-to-shop print payments separate from shop-to-platform subscription billing.
- Preserve idempotency, row locks, duplicate replay and concurrency conflict handling.
- Do not treat a client callback, screenshot or browser success screen as payment proof.
- Do not record a refund as sent unless the shop has actually returned the money through its own payment/cash process.

### Physical printing and output confirmation

- Preserve the current print-job state machine and submission-attempt identity.
- Persist intent before printer/spooler side effects.
- Never blindly retry an ambiguous physical submission.
- Do not equate queue acceptance, spooling or active printing with completed physical output.
- Preserve the current shared output-confirmation eligibility policy, fresh Agent observation requirements, retained-job blocking and explicit manual-reconciliation path.
- Do not mark a job complete, purge a private source or report success without the evidence required by the current policy.
- Do not change installed printer drivers, ports, queues, default printer, Windows Print Spooler configuration or Agent service privileges as part of API-only refactoring.

The current implementation and tests on `main` are authoritative. When a relevant rule is not obvious, locate its current policy and tests before moving it; do not recreate the rule from memory or from an old PR.

## 7. Comments, XML documentation and code readability

Code must be easy for another engineer to understand without reconstructing the entire history of a PR.

### Comments

Write comments to explain:

- Why a non-obvious decision is required.
- Which invariant must remain true.
- Why an operation is ordered before or after another operation.
- Why a transaction, row lock, idempotency check or cleanup step exists.
- Why a fallback is safe—or why a fallback is deliberately prohibited.
- Which external system behavior or compatibility limitation is being handled.

Do not add comments that merely translate the next line into English. Do not leave commented-out code, obsolete implementation notes, unexplained magic values, or TODO comments without an owner/issue and a reason to keep them.

Security-, privacy-, financial- and print-lifecycle-sensitive branches should have concise intent comments when the reason is not self-evident. Link the comment to a named policy or test where practical.

### XML documentation

- Add XML `<summary>` documentation to public service/endpoint registration entry points, public policy types and public methods when their responsibility or contract is not obvious.
- Document non-obvious parameters, return outcomes, units, exception behavior and side effects with `<param>`, `<returns>` and `<remarks>` where useful.
- Document endpoint modules with their area/capability and any unusual authorization or lifecycle requirements.
- Do not generate boilerplate XML comments for private one-line methods whose names already explain their behavior.
- Keep documentation synchronized with behavior; stale comments are defects.

### Method and class readability

- Prefer small methods with one clear responsibility and descriptive names.
- Name booleans positively and explicitly; avoid ambiguous flags that invert behavior.
- Make transaction boundaries and external side effects visible in method names or nearby comments.
- Avoid deeply nested conditionals when guard clauses make the policy clearer.
- Avoid giant parameter lists. Use a focused options/command record only when the values form one meaningful use-case input.
- Avoid static mutable state and hidden ambient dependencies.
- Keep nullable handling explicit. Do not use null-forgiving operators to silence a warning without a documented invariant.
- Follow the project's existing .NET 10, nullable-reference and warnings-as-errors settings.

### Error handling and observability

- Use stable, explicit application failure codes and map them to HTTP responses at the endpoint.
- Catch only exceptions that can be handled meaningfully at that boundary. Do not swallow exceptions or return success-shaped defaults after an unexpected failure.
- Preserve cancellation by passing the request `CancellationToken` through asynchronous work and EF/provider calls.
- Use structured logs with useful correlation and tenant-safe context.
- Never log document contents, raw grants, authentication material, secrets or unnecessary personal/payment data.

## 8. Documentation requirements

Documentation is part of the implementation, not a later cleanup task.

For each extracted capability:

1. Add or update XML documentation for public entry points and important policies.
2. Update the relevant API/module documentation when route behavior, dependencies, transaction boundaries or operational requirements would otherwise be unclear.
3. Document non-obvious state transitions, idempotency rules, time limits, ownership scope, cleanup behavior and external-system assumptions.
4. Update OpenAPI summaries/descriptions and examples when the public API contract itself changes.
5. Update architecture decision records when choosing a new cross-cutting pattern, dependency, persistence mechanism or external integration.
6. Update tests and operational runbooks when the behavior or deployment/diagnostic procedure changes.

Keep a short module note when a capability has several files or important invariants. It should state: purpose, routes, main service(s), data touched, authorization/tenant scope, transaction/idempotency behavior, external dependencies, and key tests. Do not duplicate the entire implementation in prose.

Use concrete examples and exact units/status names. Clearly label behavior that is implemented versus behavior that still requires live, physical-device or production acceptance. Never claim hardware validation from a unit test.

## 9. Testing and validation

Every extraction must preserve and improve confidence in behavior.

### Test layers

- **Domain unit tests:** normalization, validation, arithmetic, eligibility, state transitions, expiry and policy boundaries.
- **Service tests:** use-case orchestration, failure mapping, idempotent replay and side-effect ordering.
- **API/endpoint integration tests:** route binding, authorization, tenant ownership, HTTP status/body/header and serialization.
- **PostgreSQL integration tests:** EF translation, migrations, transaction boundaries, row locks, concurrent updates, unique constraints and idempotency races.
- **Portal contract tests:** customer/shop/platform callers continue to use the same public API contracts and safeguards.
- **Agent/Windows/Android tests:** preserve the relevant cross-component contract when API behavior affects local upload, print claims, spooler lifecycle, native bridges or installer operation.

Use a real PostgreSQL integration test for behavior that depends on PostgreSQL locking, SQL translation, constraints or transaction semantics. An in-memory substitute is not evidence for those behaviors.

### Test quality

- Assert externally observable behavior and security boundaries, not only private implementation details.
- Cover success, invalid input, unauthenticated, unauthorized, cross-tenant, missing resource, expired/stale state, duplicate request, concurrency conflict and external dependency failure as applicable.
- Test boundary values: exact expiry, amount rounding, page/file limits, stale/future timestamps and repeated submissions.
- Keep tests deterministic. Control time and external dependencies instead of relying on sleeps or live services.
- Do not weaken, delete or skip an existing regression test just to obtain a green run. Correct stale test fixtures or source locations while retaining the actual assertion.
- Keep source-text contract tests focused on important architectural guards; prefer runtime/behavior tests where a stable seam exists.

### Validation reporting

For each meaningful extraction, report the exact commands/workflows run and their result. Distinguish passed, failed, skipped and not-run checks. Do not describe a feature as verified when only compilation or a source-text test passed.

A green CI run is necessary but does not replace physical printer, shop-network, Android-device or deployment acceptance when those are relevant.

## 10. Incremental refactoring workflow

Follow this order for each capability:

1. **Inventory:** list affected routes, contracts, callers, domain rules, data entities, external dependencies and existing tests.
2. **Baseline:** record current behavior, authorization, tenant scope, transaction boundaries, statuses, error responses and side effects.
3. **Risk map:** identify money movement, document access, local Agent transfer, concurrency, physical output and retention implications.
4. **Tests first or alongside extraction:** add/identify regression tests before moving sensitive logic.
5. **Extract pure rules:** move deterministic decisions to a focused domain policy and verify equivalence.
6. **Extract the workflow:** move multi-step persistence/orchestration into one cohesive service with explicit dependencies and typed outcomes.
7. **Thin the endpoint:** leave HTTP binding, access metadata and response mapping in the endpoint module.
8. **Register and document:** add the dependency registration and module/XML documentation at the appropriate boundary.
9. **Validate:** run focused tests, relevant PostgreSQL integration tests, API build/tests, portal checks and the full required CI workflow for the branch.
10. **Review the diff:** look for changed behavior, duplicated rules, accidental schema changes, stale comments, hidden service-locator use and excessive abstraction.
11. **Commit the slice:** use a descriptive commit message explaining the capability and keep the diff reviewable.
12. **Proceed:** start the next capability only after the previous slice is green and its behavior is understood.

Do not move every route at once. Do not refactor a whole feature merely because its file is large. Prioritize cohesive boundaries and risk reduction.

## 11. Required review checklist

A refactoring slice is not complete until reviewers can answer **yes** to all applicable questions:

- [ ] Is the capability boundary clear and consistent with the modular-monolith architecture?
- [ ] Does each class have one cohesive responsibility?
- [ ] Are endpoint modules HTTP adapters rather than hidden application workflows?
- [ ] Are complex queries/workflows extracted without creating trivial wrapper services?
- [ ] Are domain policies deterministic and authoritative?
- [ ] Are dependencies explicit and lifetimes correct?
- [ ] Are public API contracts and error semantics unchanged unless separately approved?
- [ ] Are tenant and ownership checks preserved on every path?
- [ ] Are security, privacy, retention and secret-redaction requirements preserved?
- [ ] Are transaction, lock, idempotency, retry and reconciliation boundaries preserved?
- [ ] Are physical print and output-confirmation safeguards preserved?
- [ ] Are comments useful, XML docs accurate and module documentation current?
- [ ] Are tests behavioral, deterministic and sufficient for the risk?
- [ ] Have relevant builds, tests and CI workflows passed, with skipped checks disclosed?
- [ ] Is the change limited to the intended capability, with no unrelated formatting or cleanup churn?
- [ ] Is there no second implementation, duplicate rule, unnecessary package or unapproved schema change?

## 12. Completion criteria for the overall refactor

The API modularization is complete when:

- Program.cs is a concise composition root and no longer contains feature-sized endpoint implementations.
- Endpoint modules are grouped consistently by business area and capability.
- Multi-step application workflows are owned by cohesive, testable services.
- Domain policies have one authoritative implementation and focused tests.
- Contracts and persistence concerns are explicit and do not leak across boundaries.
- Relevant API behaviors, security invariants and integration/concurrency guarantees have regression coverage.
- Documentation accurately describes the module structure and its operational invariants.
- All required branch CI checks are green.
- The final route inventory accounts for every existing API route and identifies any deliberately inline read-only query with a reason.
- No unsupported claim is made about production, shop-floor or physical-printer validation.

A smaller, well-tested modular API is better than a large, superficially tidy rewrite. Prefer clarity, correctness and maintainability over the number of files extracted.
