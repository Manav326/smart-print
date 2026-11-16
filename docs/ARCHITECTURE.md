# Architecture

## 1. Architectural approach

Use a modular monolith for the cloud control plane and a local edge runtime for print execution. Keep clear module boundaries and contracts; do not introduce microservices until measurable operational needs justify them.

### Proposed technology baseline

- Customer PWA and shop operations portal: React + TypeScript + Vite, shared component/design packages.
- Windows shop-management desktop app: Tauri 2 native window hosting the compiled React shop workspace. It is a first-class desktop application, not a browser tab or tray-only shell (1360 × 900 default; 1000 × 680 minimum).
- Cloud API: ASP.NET Core on .NET 10, modular application layers and versioned REST endpoints.
- Cloud database: PostgreSQL with tenant-scoped data access and migrations.
- Local Windows Agent/edge runtime: separate .NET 10 Worker Service with the existing local document/upload API and printer execution. The native desktop UI uses an ACL-protected Windows named pipe for first-run owner-authorized enrollment. The pipe rejects remote clients and claims the first instance name; closing the desktop window never stops the service.
- Local durable state (current implementation): DPAPI LocalMachine-encrypted document files and JSON manifests under an ACL-protected ProgramData directory. SQLite remains a future option for richer queue/event history; it is not used by the current Agent queue.
- Contracts: versioned OpenAPI plus shared, generated TypeScript client types where practical.
- CI: build, lint, typecheck, unit tests, API integration tests, dependency/security checks and packaging validation.

This is the initial engineering baseline and may be adjusted only with a documented technical reason before implementation spreads across modules.

## 2. Logical components

### Cloud control plane

- Identity and tenant membership
- Tenant provisioning and shop configuration
- Subscription, plans, entitlements and licence issuance
- Device/agent registration and signed heartbeat
- QR station registration and session authorisation
- Job metadata and cloud-assisted upload coordination
- Payment provider integration and webhook verification
- Pricing/quote policy and immutable quote snapshots
- Audit, notifications, support and release metadata

### Local edge runtime

- LAN customer upload endpoint
- QR session validation/cache and rate limits
- Local document staging and preview/analysis
- Durable local job queue and state journal
- Printer discovery and capability registry
- Printer adapter execution through Windows spooler/IPP where supported
- Cloud synchronisation and cloud-assisted job retrieval
- Local diagnostics, backup and retention

### Client applications

- Customer PWA, including the public QR station routes.
- Shop operations web portal.
- Windows SmartPrint desktop workspace, reusing the same shop components and API contracts inside a native Tauri window.
- Platform-owner admin portal.
- Shared UI, API client, auth/session and telemetry packages.

The desktop app stores its owner session in Windows Credential Manager. After the owner signs in, it requests a short-lived, single-use Agent enrollment authorization from the existing shop API and sends it directly to the local service through the named pipe. The code is never shown or copied by the owner. The Agent keeps its separate DPAPI LocalMachine device secret. The API remains authoritative for shop membership and device enrollment. A computer already paired to a different shop is never silently transferred; the previous shop must revoke it first.

## 3. Multi-tenancy

Every tenant-owned record must carry a tenant identifier or be reachable only through a tenant-scoped parent. Enforce tenant scope in the API/application layer and database constraints where possible. Never infer tenant from a client-supplied tenant ID alone; derive it from authenticated membership/device claims and validate requested resources against it.

Tenant-owned data includes users/memberships, stations, printers, documents, jobs, quotes, payments, refunds, customers, branding, pricing and audit events. Platform-level records are explicitly marked as platform scope. Cross-tenant reporting uses controlled aggregate queries, not unrestricted tenant table access.

Add automated isolation tests for list, read, update, delete, file download, job retrieval, payment webhook mapping and agent polling.

## 4. Print job orchestration

Use an explicit state machine with optimistic concurrency/version numbers and idempotency keys. Each physical submission receives a unique submission attempt ID and spooler correlation metadata. Persist intent before external side effects. On crash/restart, reconcile with spooler state when possible; if physical output is uncertain, mark OUTPUT_UNCONFIRMED and require operator action. Never blindly replay.

Cloud and local state synchronisation must be event-based and idempotent. Define conflict policy per entity; printer/job execution state is authoritative at the local edge after a job has been leased for physical execution.

## 5. Printer abstraction

Define a provider-neutral capability model and adapter contract:

- Discover and identify device/queue.
- Read capabilities and current status with an explicit source/freshness timestamp.
- Validate requested print settings against capabilities.
- Submit a prepared print artifact with a stable attempt ID.
- Query/correlate spooler job state.
- Pause/resume/cancel only when supported.
- Report unsupported/unknown states without inventing values.

Initial adapters: Windows Print Spooler (installed vendor driver), IPP where supported, and a generic network discovery layer. Add model-specific adapters only when a real device requires them. Do not claim toner, paper, jam or physical completion status unless the device/driver actually exposes it.

## 6. Local and cloud-assisted data flow

**Direct LAN document transfer (implemented):** customer browser on an HTTPS origin → QR-bound cloud session → API issues a one-use, 15-minute grant bound to the tenant/session/station/printer/Agent and expected file hash → browser transfers bytes to the paired Agent's private-LAN HTTP endpoint → Agent verifies grant with the API, validates signature/hash/page analysis and stores the file encrypted with DPAPI LocalMachine → API records metadata only as `StorageMode=LOCAL_AGENT`.

**Cloud-assisted document transfer (implemented):** customer browser → authenticated tenant/station session → API validates and stores encrypted cloud document → shop/quote/payment lifecycle → assigned Agent claims a verified, explicitly dispatched job → Agent downloads and verifies the document into the same DPAPI-protected queue.

These are two ingress/storage modes for one PrintJob model, not two parallel job systems. Both use the cloud API for authoritative session, tenant, quote, approval, payment, entitlement and dispatch state. Direct LAN transfer keeps document bytes off cloud storage but is not an offline quote/payment/dispatch mode. A `LOCAL_AGENT` document is printer/Agent-bound; cloud document endpoints reject retrieval and the shop cannot reassign it to a different printer. The Agent must prove the local file is present and intact before acknowledging receipt or executing an authorised print.

## 7. Payment boundaries

Use separate domain modules, credentials, ledgers and reconciliation for:
1. Customer-to-shop print payments.
2. Shop-to-platform SaaS subscription payments.

Never route shop print proceeds through the platform subscription ledger. Gateway account onboarding and provider capabilities must be confirmed before selecting a concrete payment implementation.

## 8. Offline and licence behaviour

Issue signed, time-bounded licence/entitlement documents to registered devices. Define configurable offline grace after product/legal review; initial design target is 7–14 days, not a hard-coded promise. Already-authorised local jobs must be able to finish during an internet outage. Expiry must not delete shop data or terminate a physical job mid-sheet. Restrict new premium operations only according to explicit grace and entitlement policy.

## 9. Deployment and domain

Target thinkwithsujeet.in with separate customer, shop, admin, API and release subdomains. Use TLS, secure cookies, strict CORS, HSTS and environment-specific secrets. DNS/hosting are deployment tasks and are not assumed configured.

## 10. Observability

Use structured logs with correlation IDs, tenant-safe redaction, health endpoints and metrics for queue age, job transitions, agent heartbeat, printer status freshness, upload failures, payment webhook processing and sync lag. Never log document contents, payment secrets or raw access tokens.


## Android companion client

The native app in `apps/android` is a customer-side companion, not a second backend:

- Google Play services Code Scanner reads the shop's existing HTTPS `/s/{publicCode}` QR link. The shared API creates and validates the station-bound customer session.
- The on-device document library stores imported files in app-private storage. Android's Storage Access Framework is used for multi-file import, recursive folder import, single-file export and folder export; the app does not request broad shared-storage permissions.
- The print portal is hosted in Android WebView and continues to call the same customer APIs. The WebChrome file chooser is backed by the local library through a scoped FileProvider URI.
- WebView cleartext is restricted in `shouldInterceptRequest` to RFC1918 IPv4 addresses, TCP 17842 and the paired Agent upload route. The shop portal remains HTTPS. The API remains authoritative for upload grants, QR session validation, quote, payment and dispatch.
- The Android app does not include a local pricing engine, payment verification system or print executor. Physical printing continues on the paired Windows Agent.

The first CI delivery is an unsigned debug APK and JVM tests. Instrumented UI tests, physical Android LAN testing, signed releases and store distribution remain release gates.

## API modularization working agreement

The API refactoring on `refactor/api-modularization-current-main` must follow the [API Refactoring Guidelines](API_REFACTORING_GUIDELINES.md). The guide defines endpoint, service, domain, contract and persistence responsibilities; documentation and commenting expectations; compatibility and security invariants; and the incremental validation workflow. It supplements this architecture document and does not replace the modular-monolith principles above. The module folder contract is documented in [`src/SmartPrint.Api/Modules/README.md`](../src/SmartPrint.Api/Modules/README.md), and `tests/SmartPrint.Api.Tests/Architecture/ApiModuleArchitectureTests.cs` enforces the initial dependency rules in the normal API test run.
