# Delivery Roadmap and Acceptance Criteria

This roadmap distinguishes implemented capabilities from remaining work. A checked item means it exists in the repository and is covered by build/tests where applicable; it does not mean that production network access or physical hardware has been validated.

## Phase 1 / Phase 2 hardening — feature branch implementation

The branch `feature/phase-1-2-security-data-hardening` adds the following controls; they remain pending branch CI review and merge:

- [x] Tenant status enforcement at API boundaries, safe suspension recovery paths and audited platform-admin lifecycle controls.
- [x] Backend plan enforcement for active subscription access, Advanced Reports and White-label custom branding.
- [x] Source-IP-partitioned request limiting for authentication, public, QR, Agent and upload APIs.
- [x] Transactional local/cloud upload reservations, idempotency rechecks and shared batch capacity enforcement.
- [x] Payment gross collections and shop-sent refunds reported as separately dated financial events.
- [x] Configurable cloud and Windows-Agent document retention that preserves output-unconfirmed jobs and financial/job metadata.
- [x] Regression coverage for tenant status transitions, batch bounds and retention decisions.

Production blockers still outstanding: end-to-end encryption of LAN upload bytes, least-privilege Agent service account validation, malware scanning/parser isolation, email verification/account recovery, PostgreSQL concurrency integration tests, encrypted off-host backups/restore drills, per-tenant storage quotas and local-Agent disk-loss re-upload recovery. See `docs/IMPLEMENTATION_TRACKER.md` and `docs/SECURITY_AND_OPERATIONS.md`.

## Workstream 0 — engineering and delivery foundation

- [x] Monorepo with React/TypeScript portal, .NET 10 API/Agent and PostgreSQL migrations.
- [x] Portal type-check/build and API/Agent build in CI.
- [x] API model/unit tests and Agent local-queue tests in CI.
- [x] Docker images for API and portal, compose stack, Cloudflare Tunnel connector and GHCR publication from main.
- [x] CI-produced self-contained Windows x64 Agent artifact with pairing/install scripts.
- [ ] Full lint/format gates, dependency/security scanning and integration tests against a real PostgreSQL service.

Acceptance: the repository builds in CI. Deployment still needs DNS, registry access and restore rehearsal before production sign-off.

## Workstream 1 — SaaS and tenant foundation

- [x] Shop/tenant registration, shop-owner identity and authentication.
- [x] Tenant-scoped shop profile, printers, QR stations, pricing and payment settings.
- [x] Platform-admin identity and separate authorization policy.
- [x] Versioned plan catalogue and enforced printer/Agent/station/monthly-job/document-size entitlements.
- [x] Audited trial/free assignment and operator-confirmed paid-plan activation with transaction reference.
- [ ] Automated subscription gateway checkout, invoices, webhooks, reconciliation, renewals, dunning, refunds and signed offline licence/grace policy.
- [ ] Complete shop membership/role matrix and user administration.

Acceptance: core tenant and subscription administration exists; automated SaaS billing and complete shop-role management remain outstanding.

## Workstream 2 — shop operations

- [x] Shop profile, branding, pricing and UPI/cash payment settings.
- [x] Printer registry, paired Agent/printer discovery, QR stations and enable/disable controls.
- [x] Print-job list, quote review, shop approve/reject, printer assignment, dispatch and output confirmation.
- [x] Payment attempt review and manual payment verification/rejection.
- [x] Per-file and per-page rejection with full or proportional partial refunds before computer handoff; rejected pages are omitted from physical print and PDF output.
- [ ] Rich customer directory/history (implementation in PR #4: tenant-scoped, signed-in SmartPrint customers only, masked emails, and shop-only print history; guest jobs remain anonymous).\n- [ ] Full operator role matrix, comprehensive reporting, collection workflow and configurable notification studio.
- [ ] Full mobile/tablet and light/dark accessibility/visual-regression audit.

Acceptance: an owner/manager can configure and operate the core shop print workflow; broader shop operations remain in progress.

## Workstream 3 — customer PWA

- [x] QR session creation, expiry, resume, renewal and customer keepalive.
- [x] Single-document PDF/JPG/JPEG/PNG upload, signature checks, SHA-256 and page/image analysis (20 MB hard limit; PDF maximum 500 pages).
- [x] Customer choice of direct Shop Wi-Fi upload or Cloud-assisted upload.
- [x] Direct phone-to-Agent ticket, integrity check and DPAPI-protected local staging; document bytes do not traverse cloud storage in LAN mode.
- [x] Server-calculated quote, customer quote acceptance, shop review and customer job tracking.
- [x] Shop-configured UPI/cash payment attempt and customer UPI reference submission; a reference or screenshot is not proof of payment.
- [x] Multi-file PDF/JPG/JPEG/PNG uploads as separate print jobs under one shared batch ID (up to 20 files / 50 MB total), with shop-side per-file review/rejection and refunds.
- [ ] Office-document uploads, server-side malware scanning, richer customer receipts and issue/collection workflow.
- [ ] Automated payment-provider verification and cross-device customer account/history.
- [ ] Physical Android/iOS browser and shop Wi-Fi client-isolation test matrix for Local Network Access.

Acceptance: both transfer modes use one server-owned job lifecycle. LAN mode requires an online API/Agent and user-granted browser permission; it is not offline printing.

## Workstream 3A — native Android companion

- [x] Native Android project scaffold with pinned Android Gradle Plugin/Kotlin toolchain and Android 8+ baseline (minSdk 26).
- [x] Branded CameraX/ML Kit on-device QR scanner with permission handling and flashlight; decoded URLs are validated against the existing HTTPS `/s/{publicCode}` route.
- [x] App-private document library with folders/subfolders, rename, delete, open and share actions.
- [x] Multi-file and recursive folder import through Android's Storage Access Framework; file and folder export to a user-selected provider.
- [x] Native saved-document picker integrated with the customer portal; multi-file selection hands original files into the existing separate-file batch upload contract (fresh-branch CI and physical-device acceptance tracked in `docs/ANDROID_ENHANCEMENT_TRACKER.md`).
- [x] WebView HTTP request filter limiting cleartext traffic to private IPv4 Agent upload endpoints on TCP 17842.
- [x] JVM tests for QR route validation, print-format/size policy, local storage, duplicate preservation, path traversal and bounded folder-transfer budgets; CI Android debug build and the `smartprint-android-debug` artifact.
- [x] Windows PowerShell helper to download the latest successful `smartprint-android-debug` artifact and optionally install it via ADB.
- [x] Android emulator UI tests for library landing, folder creation/navigation and camera permission explanation (API 35).\n- [ ] Physical Android UI tests across OS versions and different file-manager/document providers.
- [ ] Real shop-Wi-Fi test matrix for Android WebView Local Network Access, private endpoint discovery, client isolation and paired Agent upload.
- [ ] Signed release bundle, Play Store listing, update channel and production release signing.

Acceptance: Android must reuse the server-owned QR session, quote, approval, payment and output-confirmation lifecycle. CI APK generation is not proof of physical-device or printer compatibility.

## Workstream 4 — local Windows edge and printer integration

- [x] Windows Service, paired identity, DPAPI LocalMachine credentials and authenticated cloud heartbeat.
- [x] LAN endpoint discovery and Private-profile firewall rule on TCP 17842.
- [x] Read-only local health endpoint and direct multipart document upload with exact-origin CORS.
- [x] Shared PDF/JPG/PNG document analysis, SHA-256 verification and DPAPI-protected local document queue.
- [x] Staged-upload recovery and expiry cleanup; paid-job claim refreshes print settings before queue acknowledgement.
- [x] Windows spooler submission, status reconciliation and explicit shop output confirmation.
- [ ] Tray companion, signed installer/auto-updater and richer device diagnostics.
- [ ] SQLite queue/event journal (the current durable queue is encrypted document files plus JSON manifests).
- [ ] IPP/network printer adapters, vendor capability adapters and representative physical-printer matrix.

Acceptance: Windows build and queue-state tests pass; physical model/driver support and real-device LAN transfer still require shop-floor testing.

## Workstream 5 — cloud control plane and cloud-assisted transfer

- [x] Protected cloud upload and retention as an alternative transfer mode.
- [x] Tenant/station-bound routing and paired-Agent retrieval.
- [x] Server-calculated quote, quote expiry/acceptance and shop approval.
- [x] UPI/cash payment attempt with manual shop verification before Agent dispatch.
- [x] Separate shop print-payment records from platform subscription assignments.
- [ ] Payment gateway adapters and verified webhook lifecycle.
- [x] Shop-recorded full and partial refunds for verified page selections, with cumulative amount and idempotency guards.
- [ ] Automated refund execution, reconciliation and customer-to-shop settlement reporting.

Acceptance: manual payment verification is implemented; do not describe it as automated gateway verification or reconciliation.

## Workstream 6 — platform administration and operations

- [x] Dedicated platform-admin identity, offline bootstrap and separate authorization policy.
- [x] Read-only platform overview and paginated tenant directory.
- [x] Versioned plan catalogue with immutable published versions.
- [x] API enforcement of assigned-plan resource and document-size quotas.
- [x] Audited trial/free and operator-confirmed paid subscription assignment.
- [ ] Subscription invoices, automated paid-plan checkout/webhook verification, platform payment reconciliation, revenue analytics, coupons, support workflow and full audit search.

Acceptance: platform plan/subscription administration works without direct database edits; full SaaS billing and support operations remain future work.

## Workstream 7 — production readiness

- [x] Docker compose stack for PostgreSQL, API, portal and Cloudflare Tunnel HTTPS.
- [x] Optional migration-on-startup, persistent database/document/TLS volumes and one-time secrets generated by the PowerShell starter.
- [x] Docker backup/export script for PostgreSQL, encrypted document storage and env recovery material.
- [x] Docker restore script with explicit operator confirmation and preservation of the current deployment environment.
- [x] CI build/publish of GHCR API/portal images and self-contained Windows Agent artifact.
- [ ] Anonymous GHCR pull availability (requires one-time package visibility configuration) or an operational authenticated registry policy.
- [ ] Backup restore rehearsal, retention/deletion policy, incident process and monitoring/alerting.
- [ ] Production DNS/TLS cutover and deployment/rollback rehearsal.
- [ ] Security/dependency review, load/accessibility/visual-regression tests and representative physical-printer validation.
- [ ] Real Android browser, shop Wi-Fi and printer end-to-end acceptance run.

Acceptance: CI and packaging are green. Production rollout is not signed off until network, domain, backups, physical printer and real-device LAN tests pass.

## Explicit non-goals for this release

Public Play Store distribution until the native Android app's real-device release matrix is complete, universal support for every printer, Office-file conversion, advanced OCR/AI document editing, unsupported finishing options, franchise management, loyalty programmes and fully offline quote/payment/printing. Multi-file PDF/JPG/JPEG/PNG batches are implemented; this line does not defer them.
