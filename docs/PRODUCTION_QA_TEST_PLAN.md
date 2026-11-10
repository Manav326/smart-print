# SmartPrint Production QA Master Test Plan

**Status:** Complete 345-case catalogue; execution evidence is recorded per candidate in the final release-gate report; manual and physical acceptance remains pending until signed off.  
**Product:** SmartPrint by QuantaEdge  
**Repository:** Manav326/smart-print  
**QA branch:** feature/smartprint-clean-qa-runtime  
**Scope:** Customer PWA, Android app, Windows Desktop, Windows Print Agent, shop operations, platform administration, API/data/security, deployment and release operations.

## 1. Purpose and result definitions

This is the master production-readiness suite for the capabilities currently represented in the repository. It is deliberately broader than smoke testing. A test passes only when its expected result is observed and evidence is retained. Opening a screen, receiving HTTP 200, seeing an Agent heartbeat, or passing CI is not proof of an end-to-end workflow.

| Result | Meaning |
|---|---|
| PASS | All expected results observed; evidence retained. |
| FAIL | Actual result differs from expectation; preserve first-failure evidence and create a defect. |
| BLOCKED | Cannot safely execute because a device, environment, account, fixture or dependency is unavailable. |
| NOT IMPLEMENTED | Capability is not delivered in this release. Never count it as a pass. |
| NOT APPLICABLE | Truly outside this release/platform; record the reason and approver. |
| NOT RUN | No execution evidence exists. |

**Priority:** P0 = release blocker (security, tenant isolation, financial integrity, data loss, wrong-shop routing, duplicate/unauthorised printing, or core workflow unusable); P1 = major functional/recovery/compatibility defect; P2 = moderate issue with safe workaround; P3 = minor/cosmetic.

One P0 failure blocks release. P1 failures require a fix/retest or a documented, time-bound risk acceptance by the release authority.

**Mandatory role/surface supplement:** In addition to the 345 baseline catalogue cases in this file, execute the 174 role-based cases in [ROLE_BASED_CROSS_PLATFORM_QA.md](ROLE_BASED_CROSS_PLATFORM_QA.md). The supplement covers customer and shop-owner Web workflows, Android customer account/document studio and Photo Studio WebView acceptance (APHO-001–036), the actual Windows Desktop owner workspace, history/session recovery, photo orders and shop photo enhancement. These 174 cases are separate and are not implicitly passed by CI, native build success or the 262-case local Playwright gate. ANDX-023 separately verifies Android UPI-app handoff and return without real payment or automatic verification. WOWN-009 covers the shop owner's personal account details and authenticated email change. HST-001–014 cover session continuity, history viewing, history-based restoration/reprint gaps and cross-device isolation. PHOX-001–014 cover customer photo orders, editing customer-uploaded photos already attached to shop jobs, cloud/Agent photo enhancement, photo pricing/refunds, PDF footer safety and PDF-export retry/receipt integrity. PHOX-015–021 cover the distinct shop-owner-originated photo upload/new-job workflow, existing-shop-customer versus walk-in identity, UPI/cash pending payment verification, idempotency, upload validation, cancellation and tenant isolation. The owner-created flow is implemented in code but remains acceptance-pending until its real-data journey passes. PSHT-001–031 cover exact photo tile dimensions, fixed grid coordinates, A4 pagination, partial sheets, crop/resolution, captions, pricing/refund arithmetic, printer scale, cut guides and physical output. The fixed photo-grid renderer uses one shared coordinate helper for preview, PDF generation and background sampling. **Open P0 trust-boundary finding:** current API validation checks photo metadata, PDF size, page count, portrait A4 MediaBox and exactly one non-mask 2480×3508 full-page raster per A4 page, placed edge-to-edge. It does not inspect pixels within that raster to verify the number, exact physical dimensions, margins, gutters or coordinates of individual photo tiles. PSHT-025 is not passed until server-side tile/raster-content verification is added, or the remaining boundary is formally accepted with a mandatory, auditable shop-owner PDF preview/verification gate; generated-PDF raster inspection and physical ruler/caliper measurements remain required. APHO-001–036 separately require the shared customer editor and Android WebView/file-provider/touch/device-lifecycle behaviour to be verified on a physical phone; Android is not a shop-owner editor surface. History-based restoration from Print History remains distinct from active-session/draft recovery and is not currently implemented in the Android Activity history UI. The six local Photo Studio checks in the 262-case browser suite do not replace PHOX, PSHT or APHO execution.

The fixed photo grid now uses a single shared position helper for preview and generated raster pages; unit tests cover fixed origins, partial pages and density bounds. The Chromium browser fixture inspects exact-A4 MediaBoxes, embedded page rasters, tile pixels, captions and blank final slots. The corresponding Android native WebView suite must perform the same PSHT checks on the physical/local QA app, followed by ruler/caliper measurement on each supported named printer/driver. These new tests are acceptance instructions until their local Android/physical-device evidence is recorded; do not count source changes or a green general CI run as a photo-print certification. A valid current-session/draft restore is distinct from history-driven restoration; history restore/reprint remains NOT IMPLEMENTED in the Android Activity history UI. Customer history is limited to the latest 100 jobs, and older rows created before the PhotosPerPage migration correctly display 'layout not recorded' rather than a guessed density.

## 2. Product scope and known limitations

### 2.1 Implemented scope to verify

- Tenant-scoped shop registration/authentication and shop workspace.
- Separate platform-admin identity and API authorisation.
- Shop profile/branding, printers, paired Agent discovery, QR stations, pricing and UPI/cash payment settings.
- Versioned plans and server-side subscription/feature/resource entitlement enforcement.
- Station QR session creation, expiry, keepalive/resume and station-bound routing.
- Customer PDF/JPG/JPEG/PNG uploads. Print upload limit: 20 MB per file; PDF maximum: 500 pages; multi-file batch: 20 files / 50 MB total.
- Customer choice of direct shop-LAN Agent upload or cloud-assisted upload. Both use the same cloud-coordinated PrintJob, quote, shop review, payment and output-confirmation lifecycle.
- Server-calculated quote and customer quote acceptance.
- Shop approval/rejection, printer assignment, explicit dispatch and output confirmation.
- Manual UPI/cash payment attempt review and verification/rejection.
- Per-file and per-page rejection before handoff, undo before applying review, partial/full refund recording, cumulative/idempotency protections and omission of rejected pages from physical/PDF output.
- Windows Desktop workspace, local Agent enrollment/pairing, Agent status, network selection/phone readiness check, local queue and Windows spooler integration.
- Android native QR scanner, app-private document library, folders, SAF import/export, native document picker and shared customer portal.
- Platform overview, tenant directory, versioned plan catalogue, audited trial/free/operator-confirmed paid plan assignment, tenant lifecycle and operations/retention controls.
- Same-origin download/status endpoints and versioned release artefacts.

### 2.2 Explicit limitations: do not claim these have passed

| Capability / limitation | QA treatment |
|---|---|
| Automated customer payment gateway verification, automated refunds, subscription gateway checkout/webhooks/reconciliation | NOT IMPLEMENTED. Verify manual UPI/cash flows only. Never use live payment details or treat a customer success screen/reference/screenshot as proof. |
| End-to-end encryption of direct LAN document bytes | DOCUMENTED SECURITY BLOCKER. Current private-LAN HTTP, grants, CORS, ownership checks and hashes do not encrypt bytes in transit. Do not use guest/public Wi-Fi. This suite cannot mark the encryption gap closed. |
| Dedicated least-privilege Windows Agent service identity | DOCUMENTED OPERATIONS/SECURITY GAP. Inspect actual service account and ACLs; do not assume restricted identity is configured. |
| Malware scanning and isolated document parser/converter | Candidate implementation: mandatory ClamAV scan + isolated parser worker for cloud uploads; mandatory Windows Defender scan for direct-LAN uploads. Residual gap: Agent PDFium/Skia parsing/rendering is still in-process and native Windows acceptance/corpus evidence remains required. |
| Email verification and secure account recovery | NOT IMPLEMENTED. Do not assume email ownership has been verified or recovery is available. |
| PostgreSQL concurrency/integration coverage for simultaneous critical operations | Candidate adds real-PostgreSQL refund-vs-Agent-claim in both orders, two distinct Agent claimants and concurrent Razorpay verify/webhook capture. Full critical-state race matrix and recorded run evidence remain required. |
| Encrypted off-host backup and complete restore rehearsal | Candidate adds authenticated AES-256-GCM bundle, off-site rclone verification and isolated DB/document restore drill. Real off-site target, owner-held key and retained restore evidence remain required. |
| Per-tenant storage quota and local-Agent disk-loss re-upload recovery | DOCUMENTED GAP. |
| Customer all-time history pagination | Current customer history endpoint returns the latest 100 jobs only; there is no older-page cursor in the current Android UI. Verify the cap and do not claim unlimited history. |
| Universal printer support, IPP and vendor-specific capability adapters | NOT IMPLEMENTED. Validate only named printer/driver combinations actually tested. |
| Agent tray companion and automatic Agent updater | NOT IMPLEMENTED. |
| iOS app, Play Store distribution, Microsoft Store/MSIX, Office upload/conversion and fully offline printing | OUT OF CURRENT RELEASE SCOPE. |
| Automatic Android App Link launch from external camera/message links | NOT IMPLEMENTED. Ordinary HTTPS browser opening is the expected external-link behaviour. |
| Restore an unfinished request/draft or reprint directly from customer Print History | NOT IMPLEMENTED. Activity supports history search and viewing order details/timeline only. Do not call that session restoration or reprint. |

### 2.3 Safety rules

- Execute destructive, quota-exhaustion, retention, installer rollback, concurrency and security-fuzz tests only in disposable QA.
- Use synthetic shops, customers, documents, UPI references and a virtual or controlled test printer.
- Never submit real payment, issue real refund, print a private customer document, or change live shop configuration during QA.
- Do not uninstall, clear, overwrite, re-pair, switch or reset an existing owner/customer installation or Agent without explicit approval.
- Do not stop a production Agent, change production firewall rules, delete persistent volumes, purge live documents or run a destructive restore over production.
- Never paste credentials, QR/session tokens, Agent secrets, Wi-Fi passwords, payment data or document contents into reports or logs.
- Spooler acceptance is not proof paper physically exited. An uncertain physical submission must never be automatically resubmitted.
- Preserve the first failure evidence before retrying.

## 3. Test environment and fixtures

Complete this record for every candidate:

| Field | Value |
|---|---|
| QA run ID and date/time/timezone | |
| Release tag, product version and Git commit SHA | |
| API image digest and portal image digest | |
| Windows installer filename and SHA-256 | |
| Desktop executable and Agent versions/hashes | |
| Android APK versionName/versionCode/signing fingerprint | |
| Windows edition/build/architecture/RAM | |
| WebView2 Runtime version | |
| Android model/OS/API level/RAM | |
| Browser name/version | |
| PostgreSQL version and applied migration level | |
| QA public origin | |
| QA shop labels / tenant IDs | |
| Test printer model/driver/connection | |
| Tester/reviewer | |

Required fixtures:
- SHOP-A: active QA shop with owner, enabled QR station, pricing, paired Agent and QA printer.
- SHOP-B: separate active shop for tenant-isolation testing.
- SHOP-C: disposable shop for suspension/inactivation.
- OWNER-A and OWNER-B: distinct shop-owner identities.
- PLATFORM-ADMIN: separate platform-admin identity.
- CUSTOMER-GUEST and CUSTOMER-ACCOUNT: guest and signed-in global customer, where account functionality is enabled.
- DOC-PDF: known-good PDF with known page labels; include a 500-page synthetic boundary file.
- DOC-JPG, DOC-JPEG and DOC-PNG: known-good images and known dimensions/orientation.
- DOC-INVALID: malformed/truncated files and text/HTML renamed to supported extensions.
- DOC-BOUNDARY: test files around 20 MB per-file and 50 MB per-batch limits.
- BATCH-1: multiple synthetic files with distinct page counts.
- NETWORK-SHOP: isolated private shop LAN; NETWORK-OTHER: separate inaccessible network; NETWORK-GUEST: negative connectivity test only.
- QA-PRINTER: virtual/test printer; PRINTER-FAULT: safe printer/simulator for offline/error/paper-out cases.

## 4. How to execute and record

1. Freeze the candidate. Record commit, tag, image digests, APK/installer versions and checksums.
2. Confirm all tests target the isolated QA origin and synthetic tenants, not a real shop or live financial account.
3. Run identity, tenant-isolation and core customer-to-output cases before negative, boundary, concurrency, resilience and physical compatibility tests.
4. For every case record actual result, result state, tester, timestamp, exact build, environment, evidence reference and defect ID.
5. Use screenshots/video for UI/native interactions; sanitised API traces for service behaviour; database assertions only in isolated QA; redacted Agent/Windows logs; labelled physical print samples.
6. Use a fresh evidence folder for every run. Do not reuse a folder when a script protects existing files from overwrite.
7. Repeat all P0/P1 cases affected by a fix. A regression pass must use one exact candidate build; do not combine passes from different commits as one release result.
8. Before retrying an ambiguous payment, refund, dispatch, print or enrollment action, check authoritative state first.
9. Mark missing features NOT IMPLEMENTED and leave them in the release blocker register.

### 4.1 Live terminal progress contract

Every executable QA runner must report the work **while it is happening**. Do not make the operator wait for a final summary to discover what the runner was doing.

Required terminal behaviour:

- Print a timestamped start line before each logical QA step or test case, with its exact name and, where available, case ID.
- Print meaningful sub-actions during long work (for example, downloading the published installer, inspecting the Agent service without changing it, polling for Desktop UI Automation landmarks, selecting the Android device, opening Files navigation, reserving a document upload, or reconciling a payment record).
- Print an immediate per-case PASS/FAIL/SKIP result and elapsed time when the case ends.
- For blocked/skipped cases, state the prerequisite or safety reason. Never report a skipped case as a pass.
- Stream Node, xUnit and Gradle test output live. The Node reporter prints case start/end, source path/line and failure detail; xUnit verbose mode is sequential and configured not to stop after the first test failure; Android Gradle test tasks report per-test lifecycle events.
- Continue executing independent test records after an isolated test failure. Skip only records whose declared prerequisites did not pass. Stop only when a critical preflight, environment, security boundary or harness failure makes further execution unsafe or invalid.
- Keep final machine-readable results and logs aligned with terminal output. Terminal progress is observability, not a substitute for evidence.

Standard prefixes:

| Prefix | Meaning |
|---|---|
| \`[QA-STEP-START]\` | A top-level QA step is starting. |
| \`[QA-STEP-ACTION]\` / \`[QA-ACTION]\` | The exact operation currently being performed. |
| \`[QA-CASE-START]\` / \`[TEST ... START]\` | A named test case is beginning. |
| \`[QA-PROGRESS]\` | A long-running operation is still active, with current sub-stage/attempt. |
| \`[QA-RESULT]\` / \`[QA-CASE-END]\` / \`[TEST PASS|FAIL|SKIP]\` | Individual result and elapsed time. |
| \`[QA-STEP-END]\` | A top-level step completed with exit code and duration. |

Example terminal output:

\`\`\`text
[QA-STEP-START] 08 | 14:12:06 | Portal logic regression tests
[TEST 001 START] rejects an expired accepted quote — batch-checkout-state.test.ts:5
[TEST PASS] rejects an expired accepted quote — batch-checkout-state.test.ts:5 (8 ms)
[TEST 002 START] keeps verified batch payment visible — batch-checkout-state.test.ts:9
[TEST FAIL] keeps verified batch payment visible — batch-checkout-state.test.ts:9 (22 ms)
  [TEST ERROR] Expected payment state VERIFIED, received PENDING
[QA-STEP-END] 14:12:07 | FAIL | exit=1 | elapsed=1.1s
\`\`\`

A failed test stays visible in the run report, but does not by itself cancel unrelated independent cases.

Per-case execution record: Case ID | Result | Actual result | Evidence path | Defect ID | Retest build/commit | Retest result | Tester/time.

---

# 5. Detailed test case catalogue

## A. Release artefacts, installation and Windows Desktop

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| RLS-001 | P0 | Compare release metadata, tag, website version, Windows installer, Desktop executable, Agent and APK versions. | All artefacts belong to one intended release; no mixed/stale version. |
| RLS-002 | P0 | Download Windows installer through the public same-origin route using a fresh evidence folder. | Correct stable asset downloads; browser is not redirected to GitHub/CDN. |
| RLS-003 | P0 | Verify Windows installer SHA-256 against published checksum. | Exact match; mismatch blocks installation. |
| RLS-004 | P0 | Download Android APK and verify package ID, size, versionName and versionCode. | Non-empty APK matches release metadata and expected application identity. |
| RLS-005 | P0 | Verify APK signature and compare certificate fingerprint with previous installed release. | Valid single signer; upgrade identity matches; debug or mismatched package is not accepted as stable release. |
| RLS-006 | P0 | Inspect Windows installer Authenticode status. | Interim beta is honestly reported as unsigned; stable GA remains gated on signing or an approved beta distribution policy. |
| RLS-007 | P0 | Install on a clean disposable supported Windows VM. | Guided Desktop and Agent installation succeeds without asking for pairing code, API URL or manual PowerShell steps. |
| RLS-008 | P1 | Launch with WebView2 Runtime already present. | Native window opens and the React sign-in/workspace content actually renders; no blank window. |
| RLS-009 | P1 | Launch on clean VM missing WebView2 Runtime. | Verified Microsoft bootstrapper installs prerequisite or installer gives safe retry guidance. |
| RLS-010 | P1 | Launch while an existing Desktop instance is running. | Existing session is not killed or replaced; no duplicate Agent service is installed. |
| RLS-011 | P0 | Upgrade an older QA installation with paired Agent and pending queue. | Pairing, encrypted queue, printer configuration and recoverable owner session remain consistent. |
| RLS-012 | P0 | In disposable VM, simulate failure starting newly staged Agent during upgrade. | Installer restores previous executable path/running state and leaves previous installation recoverable. |
| RLS-013 | P1 | Inspect successful upgrade staging/cleanup. | New files, service and firewall health are confirmed before superseded Agent files are removed. |
| RLS-014 | P1 | Install/upgrade under standard Windows user and managed-device policy. | Required elevation is clear and bounded; failure is understandable; user is not told to disable Windows security protections. |
| RLS-015 | P1 | Reboot Windows and sign out/sign back in. | Desktop/service recover according to configured startup policy; no false Agent-ready status. |
| RLS-016 | P0 | Inspect production Desktop executable/runtime arguments. | No CI-only DevTools endpoint/port or temporary CI profile is enabled in production. |
| RLS-017 | P1 | Open every primary Desktop destination: Order Journey, Overview, Print Queue, Printers, QR Stations, Print Agent, Pricing, Subscription, Payments, Customers, Reports and Settings, where enabled. | Each implemented destination renders correct data or helpful empty state; no blank panel/fatal error/dead navigation. |
| RLS-018 | P1 | Resize/minimise/restore Desktop and test supported screen resolutions/scaling. | Controls, dialogs and content remain reachable and unclipped. |
| RLS-019 | P2 | Close Desktop normally and relaunch. | Desktop exits cleanly; Agent service lifecycle remains independent. |
| RLS-020 | P0 | Verify Desktop connects only to configured SmartPrint API and expected local Agent endpoints. | No production/test origin mixing or unintended environment writes. |

## B. Authentication, sessions and identity

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| AUTH-001 | P0 | Register a QA shop with valid business/owner names, unique email and 12+ character password. | One shop and owner are created; trial messaging is accurate; no payment is collected at registration. |
| AUTH-002 | P1 | Register with missing/blank names, malformed email, overlong fields and short password. | Client and server reject invalid data; no partial shop is created. |
| AUTH-003 | P0 | Register same email using case/whitespace variants. | Duplicate identity handled consistently; no duplicate owner/shop or account enumeration leak. |
| AUTH-004 | P0 | Sign in with valid owner credentials in web workspace. | Correct tenant workspace loads. |
| AUTH-005 | P1 | Sign in with wrong password, unknown email and malformed request. | Safe error; no stack trace, credential/token disclosure or sensitive existence signal. |
| AUTH-006 | P0 | Attempt shop Desktop sign-in using PLATFORM-ADMIN identity. | Rejected from shop scope; admin token is not stored as shop session. |
| AUTH-007 | P0 | Call platform-admin APIs with shop-owner token. | Server rejects regardless of portal menu visibility. |
| AUTH-008 | P0 | Call shop-owner APIs with platform-admin token. | Server rejects; admin session is not migrated into shop scope. |
| AUTH-009 | P0 | Inspect web session storage and Desktop secure session handling after sign-in. | Web uses correct scoped storage; Desktop shop session is stored through Windows Credential Manager, not plaintext local files. |
| AUTH-010 | P0 | Sign in to Desktop, close, relaunch and restore session. | Correct shop is restored from secure store; no blank screen or wrong tenant. |
| AUTH-011 | P0 | In disposable profile corrupt/remove Desktop secure session or make Credential Manager unavailable. | App falls back to sign-in and gives recoverable error; no crash/blank loop. |
| AUTH-012 | P0 | Expire access token but retain valid refresh token; perform authenticated read/write. | Refresh happens safely once and action does not duplicate side effect. |
| AUTH-013 | P0 | Trigger parallel authenticated requests during token refresh. | Only one refresh rotation is consumed; all requests use new session consistently. |
| AUTH-014 | P0 | Revoke/expire refresh token and request protected data. | Session is cleared; protected request is denied; user returns to sign-in. |
| AUTH-015 | P0 | Sign out, restart Desktop and inspect secure session. | Shop session is deleted; Agent pairing identity is not accidentally removed. |
| AUTH-016 | P1 | Use shop/admin routes with separate identities/profiles and browser back/forward. | Sessions stay isolated; protected screens do not reappear after sign-out. |
| AUTH-017 | P1 | Register/sign in with Unicode names and supported email casing. | Safe display and deterministic identity matching. |
| AUTH-018 | P1 | Exceed authentication rate limit from one QA source IP. | Server enforces rate limit with controlled response. |
| AUTH-019 | P1 | Attempt account verification/recovery workflows. | Until delivered, mark NOT IMPLEMENTED; do not claim email ownership or recovery is verified. |

## C. Shop profile, pricing, payment settings and entitlements

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| SHOP-001 | P1 | Load new profile and edit valid business/display details. | Changes persist only for current shop. |
| SHOP-002 | P0 | Read/update SHOP-B profile using OWNER-A and SHOP-B identifiers. | Cross-tenant access is denied and no data changes. |
| SHOP-003 | P1 | Set valid HTTPS logo and inspect customer/owner views. | Correct shop logo and accessible description are used. |
| SHOP-004 | P1 | Set unavailable/malformed/untrusted logo in QA. | Safe fallback appears; no script execution/broken endless loading. |
| SHOP-005 | P1 | Edit pricing at minimum, normal and maximum supported values. | Values validate/persist and affect future quotes only. |
| SHOP-006 | P0 | Submit negative, overflow, excessive precision and malformed price via direct API. | Server rejects invalid values; client validation is not the only boundary. |
| SHOP-007 | P1 | Change price after a quote has been issued. | Existing quote retains immutable snapshot; future quote follows new rules. |
| SHOP-008 | P1 | Configure UPI only and inspect customer options. | UPI offered with correct payee snapshot; disabled methods absent. |
| SHOP-009 | P1 | Configure cash only and inspect customer options. | Cash offered; stale UPI information is not exposed. |
| SHOP-010 | P1 | Disable both methods or configure invalid UPI information. | Unsupported method cannot be submitted; owner gets actionable warning. |
| SHOP-011 | P0 | Change payment settings through another tenant or unauthorised identity. | API denies change. |
| SHOP-012 | P1 | Load subscription summary, plan/status/interval/period dates. | UI matches authoritative API assignment. |
| SHOP-013 | P0 | Exceed printer, Agent, station, monthly-job and document-size quotas through direct API. | Server enforces entitlements even when UI is bypassed. |
| SHOP-014 | P0 | Access Advanced Reports/White-label routes on a plan without entitlement. | API denies access; hiding menu is not accepted as enforcement. |
| SHOP-015 | P1 | Change QA plan assignment and reload workspace. | New entitlements apply consistently; existing jobs/documents are not unexpectedly deleted. |
| SHOP-016 | P1 | Publish a new plan version and inspect old subscription. | Published plan versions remain immutable; history retains original snapshot. |
| SHOP-017 | P1 | Test resource limit exactly at maximum and one above maximum. | Allowed boundary succeeds; excess is rejected with safe guidance. |

## D. Windows Agent, pairing and shop network

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| AGT-001 | P0 | Open Print Agent with service running and paired to SHOP-A. | Service reachability, local API, pairing and readiness are accurate. |
| AGT-002 | P0 | Stop Agent in disposable QA and open Desktop Agent view. | Desktop reports disconnected/not ready; no false printing availability. |
| AGT-003 | P1 | Simulate stale heartbeat while local service remains reachable. | Cloud heartbeat and local API reachability are distinguished. |
| AGT-004 | P1 | Simulate stale printer inventory and stopped print worker. | Queue/worker/inventory readiness is accurately separated. |
| AGT-005 | P0 | Guided enrollment from signed-in SHOP-A owner session. | Short-lived enrollment authorisation is sent to local Agent via protected control channel; no manual code/API URL requested. |
| AGT-006 | P0 | Attempt enrollment with expired, malformed, replayed or wrong-shop authorisation. | Rejected; no wrong-tenant device is paired. |
| AGT-007 | P0 | Pair already-active SHOP-A computer to SHOP-B without revoking first. | Transfer refused until old shop revokes; no silent reassignment. |
| AGT-008 | P0 | Revoke paired Agent server-side and restart it. | Device remains revoked; no silent re-enrolment. |
| AGT-009 | P1 | Use explicit Reconnect after server-confirmed revocation. | Fresh owner-authorised pairing; correct audit/device identity. |
| AGT-010 | P1 | Exercise standalone manual Agent pairing-code path in separate QA installation. | Code expires after 10 minutes and cannot be reused or paired to wrong tenant. |
| AGT-011 | P0 | Inspect Agent service account, executable path, ACLs and printer permissions. | Least privilege is proven; if absent, retain blocker and do not waive it. |
| AGT-012 | P0 | Call Agent named pipe from unprivileged local process and remote client. | ACL rejects unauthorised caller and remote client; no secret/privileged command exposed. |
| AGT-013 | P0 | Inspect Agent identity and document staging directories. | DPAPI/ACL protections are applied; credentials are not plaintext. |
| AGT-014 | P1 | Restart Agent with empty and pending synthetic queue. | Queue reloads without loss or duplicate processing. |
| AGT-015 | P0 | Enumerate adapters, select NETWORK-SHOP and inspect endpoints. | Correct interface/endpoints selected; unrelated VPN/guest network not silently selected. |
| AGT-016 | P1 | Switch selected network between Wi-Fi and Ethernet. | Endpoint snapshot updates; pairing, session, printer and jobs stay intact. |
| AGT-017 | P0 | Start Test with a phone and inspect readiness QR. | QR is shop/network-scoped, short-lived and displays five-minute expiry guidance. |
| AGT-018 | P0 | Scan fresh readiness QR from phone on NETWORK-SHOP. | Phone test passes and Desktop updates automatically without manual refresh. |
| AGT-019 | P0 | Scan QR from NETWORK-OTHER. | Test fails clearly; shop/session/printer settings remain unchanged. |
| AGT-020 | P0 | Scan expired readiness QR. | Rejected as expired; no false success. |
| AGT-021 | P0 | Reuse readiness QR after one successful scan. | Second scan rejected as already used/expired. |
| AGT-022 | P1 | Run readiness check with existing Android station session and unsent draft. | Shop, station, session, draft, network and printer remain unchanged. |
| AGT-023 | P1 | Disable selected adapter during readiness test. | Retryable failure; stale success is not retained. |
| AGT-024 | P1 | Restart local Agent while Desktop is open. | Polling recovers accurate status without reinstall/database repair. |
| AGT-025 | P0 | Call local API from remote host and disallowed browser origin. | Remote/untrusted origin is denied; only intended private endpoint/origin is allowed. |
| AGT-026 | P0 | Inspect direct local upload transport on isolated shop LAN. | Functional validation may pass, but unencrypted HTTP is recorded as an open security blocker; never use guest/public Wi-Fi. |

## E. QR station, customer entry and session isolation

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| QR-001 | P0 | Generate SHOP-A station QR and scan with ordinary camera/browser. | Correct HTTPS customer route opens; no session/account token in URL. |
| QR-002 | P0 | Scan SHOP-A station QR with Android in-app scanner. | Native scanner validates origin/path and opens same station route in WebView. |
| QR-003 | P0 | Inspect QR payload, query, browser history and logs. | Only station locator/public code is present; no customer/token/Wi-Fi/Agent secret. |
| QR-004 | P0 | Create guest session from SHOP-A QR. | Opaque short-lived station-bound session created without customer Authorization header. |
| QR-005 | P0 | Open station URL in browser already signed into another customer account. | Guest session remains separate; is not bound to unrelated customer. |
| QR-006 | P0 | Use SHOP-A session against SHOP-B upload/quote/job/status routes. | Server rejects every cross-shop operation. |
| QR-007 | P0 | Tamper hostname, scheme, port, route, station code and query. | Invalid/wrong-origin value rejected; current shop/session not changed. |
| QR-008 | P0 | Disable or rotate station, then scan old QR. | Old code is unavailable/revoked; no new session issued. |
| QR-009 | P1 | Create multiple stations within quota and enable/disable independently. | Each station maps to correct tenant/configuration. |
| QR-010 | P0 | Create station at and above plan quota through UI and API. | Server quota is enforced; no hidden station is created. |
| QR-011 | P1 | Keep session active through supported keepalive/resume. | Remains bound to original station/shop and expires according to policy. |
| QR-012 | P0 | After expiry, attempt upload/quote/payment with old session. | Server rejects; user receives safe fresh-scan recovery path. |
| QR-013 | P1 | Reload browser tab/reopen Android during valid session. | Only documented session storage/Keystore mechanism resumes; no duplicate PrintJob. |
| QR-014 | P1 | Open station link from external Android camera/message app. | Ordinary HTTPS browser opens (automatic app link is not implemented); token stays tab-scoped and out of URL. |
| QR-015 | P1 | Generate excessive QR sessions from one source IP in QA. | Server-side rate limit protects service; no token leak. |
| QR-016 | P1 | Suspend SHOP-A while guest session remains open. | New work blocked; permitted read-only/recovery path follows tenant policy. |

## F. Android app, scanner and document studio

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| AND-001 | P0 | Install APK on clean supported Android device (API 26+ baseline). | Installation and first launch succeed; branding/launcher correct. |
| AND-002 | P0 | Upgrade existing installation without uninstalling. | Same package/signature accepts update; app-private documents persist. |
| AND-003 | P1 | Deny camera permission on first scan. | Clear explanation and settings/retry path; no crash or unrelated permission prompt. |
| AND-004 | P1 | Grant permission and scan valid SHOP-A QR. | CameraX scanner reads QR and routes to correct station. |
| AND-005 | P0 | Scan wrong-origin, public HTTP, malformed and unsupported-scheme QR values. | Rejected; no arbitrary navigation or cross-tenant session. |
| AND-006 | P1 | Toggle flashlight during scan. | Works where supported; safely disabled when unavailable. |
| AND-007 | P1 | Rotate device and background/foreground scanner. | Preview resizes; camera resources released/reacquired safely. |
| AND-008 | P1 | Permanently deny or revoke camera permission in Settings. | Clear recovery guidance; no repeated prompt loop. |
| AND-009 | P0 | Open empty app-private document library. | Helpful empty state; no broad storage permission requested. |
| AND-010 | P1 | Create nested folders and navigate/reopen. | Folder tree persists; navigation remains inside app-private library. |
| AND-011 | P1 | Import PDF/JPG/JPEG/PNG via SAF. | Correct original file and metadata are saved. |
| AND-012 | P1 | Import multiple files and recursive folder. | Supported files and nested structure preserved; invalid files reported without corrupting valid ones. |
| AND-013 | P0 | Import file just below and above 100 MB library limit. | Below-boundary accepted; above-boundary safely rejected. This differs from the print-upload limit. |
| AND-014 | P1 | Import duplicate filenames from different directories. | Documented duplicate preservation behaviour; no silent overwrite. |
| AND-015 | P0 | Import traversal segments, encoded separators and deceptive Unicode names. | No path escape or unintended overwrite. |
| AND-016 | P1 | Rename/open/share/delete file and folder. | Correct target only; destructive confirmation and provider sharing are appropriate. |
| AND-017 | P1 | Export file and folder to supported SAF providers. | User-selected destination honoured; exported file/folder readable. |
| AND-018 | P1 | Open customer print picker and choose saved document. | Original file enters shared upload lifecycle; no duplicate scanner/print state machine. |
| AND-019 | P1 | Select several saved documents. | Existing separate-file batch contract used with shared batch ID. |
| AND-020 | P1 | First ML Kit Document Scanner use with supported hardware/Play services. | ML Kit owns capture, crop, review and PDF/JPEG output; no duplicate CameraX document scanner. |
| AND-021 | P1 | Scan multiple pages and correct crop/rotation. | Page order/orientation correct; saved output opens from library. |
| AND-022 | P1 | Simulate ML Kit download failure, missing Play services or unsupported low-RAM device. | Clear retry/import-existing-file recovery; no unapproved duplicate scanner fallback. |
| AND-023 | P1 | Force-stop/reopen during active station session. | Saved files remain; session resumes only if valid; no duplicate job. |
| AND-024 | P1 | Test tablet portrait/narrow split-screen. | Bottom navigation and actions remain usable without clipping. |
| AND-025 | P1 | Test tablet landscape. | Left navigation rail appears as designed; content remains readable. |
| AND-026 | P1 | Test Android 16 restricted-local-network compatibility only on dedicated QA hardware with documented opt-in flag. | Record separately; normal customers are not given unnecessary Nearby devices setup. |
| AND-027 | P0 | Android direct upload while phone and Agent share NETWORK-SHOP. | Correct paired SHOP-A Agent receives file; hash/byte checks pass; metadata lifecycle remains cloud-coordinated. |
| AND-028 | P0 | Local upload from NETWORK-OTHER or with local access denied. | Transfer fails safely; no false uploaded state; recovery/cloud option remains available. |
| AND-029 | P1 | Reopen app after OS kills process under memory pressure. | App-private documents persist; no import corruption/duplicate. |
| AND-030 | P1 | Test TalkBack, font scaling and system contrast. | All primary controls have accessible labels and remain usable. |

## G. Document validation, upload, batch and data integrity

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| DOC-001 | P0 | Upload known-good PDF through cloud-assisted mode. | SHA-256/page analysis and correct tenant/station/job association are stored. |
| DOC-002 | P0 | Upload same PDF through direct LAN Agent on isolated QA LAN. | One-use grant, tenant/station/Agent binding, byte count and hash validated; same PrintJob lifecycle used. |
| DOC-003 | P0 | Upload valid JPG, JPEG and PNG through each supported mode. | Actual content analysed; no extension-only acceptance. |
| DOC-004 | P0 | Upload HTML/text renamed PDF and PDF renamed image. | Signature/content mismatch rejected. |
| DOC-005 | P0 | Upload malformed, truncated, zero-byte and structurally invalid files. | Safe validation error; no printable job created; service remains healthy. |
| DOC-006 | P0 | Upload exactly 20 MB and just over 20 MB single file. | Allowed boundary succeeds if otherwise valid; over-limit consistently rejected by API and Agent. |
| DOC-007 | P0 | Upload exactly 500-page PDF and 501-page PDF. | 500-page boundary handled as supported; 501-page file rejected. |
| DOC-008 | P1 | Upload unusually large images, rotated/CMYK images and transparent PNG. | Analysis is bounded; dimensions/orientation handled or safely rejected. |
| DOC-009 | P0 | Upload encrypted/password-protected PDF. | Clear supported/unsupported result; no false page count or corrupt output. |
| DOC-010 | P0 | Batch 20 files totalling at or below 50 MB. | Accepted; each file has separate PrintJob under one shared batch ID. |
| DOC-011 | P0 | Batch 21 files. | Rejected or excess prevented; no partial orphan jobs. |
| DOC-012 | P0 | Batch 20 individually valid files with combined size above 50 MB. | Shared batch limit enforced in cloud and local paths. |
| DOC-013 | P1 | Batch supported, unsupported and corrupt items together. | Valid items handled deterministically; failures identified; no silent substitution. |
| DOC-014 | P1 | Upload duplicate content under different names and replay same idempotency key. | Duplicate policy explicit; retry creates no unintended duplicate reservation/job. |
| DOC-015 | P0 | Interrupt cloud upload mid-stream then use recovery flow. | Incomplete upload never becomes printable; retry/resume does not duplicate job. |
| DOC-016 | P0 | Interrupt local upload by disabling Wi-Fi/stopping Agent. | Partial staging is not acknowledged as complete; recovery/expiry cleanup safe. |
| DOC-017 | P0 | Replay expired, consumed or cross-tenant upload grant. | Rejected; grant cannot be reused for another file/shop/station/printer/Agent. |
| DOC-018 | P0 | Tamper with filename, byte count, hash or grant metadata. | Binding/hash verification fails closed. |
| DOC-019 | P1 | Use Unicode, quotes, spaces, separators, long filenames and control chars. | Safe display/normalisation; no traversal, markup injection or overwrite. |
| DOC-020 | P0 | Reserve final monthly quota slot, then make concurrent submissions. | Reservation/quota lock permits only valid capacity; no race-based over-quota work. |
| DOC-021 | P1 | Let incomplete local reservation expire. | Expired reservation no longer consumes capacity; no phantom job. |
| DOC-022 | P0 | Upload same file via cloud and direct LAN and compare analysis. | Page count, identity, quote inputs and downstream lifecycle agree. |
| DOC-023 | P0 | Request cloud preview/download of local-only document. | Local document bytes are not returned by cloud preview/download. |
| DOC-024 | P1 | Apply retention to completed and output-unconfirmed cloud jobs. | Only eligible documents purge; output-unconfirmed/financially held work remains recoverable. |
| DOC-025 | P0 | Preview/download after eligible cloud document purge. | HTTP 410 documented response; job/payment/refund metadata remains. |
| DOC-026 | P1 | Reject one file in a multi-file batch. | Rejected file excluded individually; accepted files remain valid. |

## H. Print configuration and quote lifecycle

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| QTE-001 | P0 | Request quotes for every print option enabled by SHOP-A pricing and printer capability. | Server amount agrees with configured supported rules. |
| QTE-002 | P0 | Change Copies from default to another value, blur field and continue. | Exact Copies retained and included in quote/job. |
| QTE-003 | P0 | Test minimum, maximum and one-above-maximum copies. | Valid boundaries accepted; invalid/overflow values rejected server-side. |
| QTE-004 | P0 | Quote first/last/all/disjoint/invalid page ranges. | 1-based page ranges are correct; invalid/out-of-range values rejected. |
| QTE-005 | P1 | Exercise duplex, copies and minimum-charge combinations using known allocation fixture. | Total and per-page allocations match pricing, including duplex sheet sharing/minimum charge. |
| QTE-006 | P1 | Select unsupported/disabled printer or plan option. | UI and API reject unsupported configuration. |
| QTE-007 | P0 | Tamper client request amount, currency, page count, printer, tenant and price. | Server ignores/rejects client financial values; authoritative quote snapshot wins. |
| QTE-008 | P0 | Accept valid quote before expiry. | Acceptance recorded once and state advances correctly. |
| QTE-009 | P0 | Accept expired quote and replay acceptance. | Expired quote rejected/requoted; replay is idempotent. |
| QTE-010 | P1 | Change shop price between quote issue and acceptance. | Original snapshot/expiry policy honoured; no silent quote mutation. |
| QTE-011 | P1 | Open quote in multiple tabs/devices under same guest session. | State remains bound to correct job/session; conflicting state is clear. |
| QTE-012 | P0 | Request quote while tenant suspended/inactive or subscription access blocked. | API blocks new quote independent of UI navigation. |

## I. Shop queue, review, assignment, dispatch and output

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| JOB-001 | P0 | Complete customer submission and inspect Order Journey/Print Queue. | One correct job appears with document, batch, quote and status. |
| JOB-002 | P1 | Filter, sort, search and paginate jobs across supported statuses. | Results stable, complete and scoped to current shop. |
| JOB-003 | P0 | Open job details and verify session, file, page count, copies, quote and payment. | Matches authoritative records; no secrets or other-tenant data. |
| JOB-004 | P0 | Approve eligible job as owner. | One valid transition, persisted once. |
| JOB-005 | P0 | Reject eligible job with valid reason. | Reason persists, customer sees safe status, no dispatch occurs. |
| JOB-006 | P1 | Reject with empty, whitespace, overlong or control-character reason. | Validation applied; no malformed history. |
| JOB-007 | P0 | Approve/reject twice or concurrently from two sessions. | Conflicting transition rejected or idempotent; no duplicate side effect. |
| JOB-008 | P0 | Assign eligible job to compatible SHOP-A QA printer. | Correct printer persisted and delivered to Agent. |
| JOB-009 | P0 | Assign SHOP-B, disabled or incompatible printer. | API rejects cross-tenant/invalid printer. |
| JOB-010 | P1 | Reassign before Agent claim/dispatch. | New assignment saved and reflected by Agent. |
| JOB-011 | P0 | Reassign after local handoff/claim/spooler activity. | State boundary prevents redirecting uncertain physical work. |
| JOB-012 | P0 | Attempt dispatch of unpaid job. | Agent cannot claim/print unless documented policy authorises it. |
| JOB-013 | P0 | Confirm manual payment requirement before dispatch. | Only server-verified shop payment state unlocks dispatch. |
| JOB-014 | P0 | Dispatch approved/paid synthetic job to QA-PRINTER. | One authorised Agent claim and correct lifecycle. |
| JOB-015 | P0 | Double-click dispatch and replay API request. | No duplicate physical submission or claim. |
| JOB-016 | P0 | Interrupt cloud after local authorised claim. | Agent preserves authorised local state as designed; no unauthorised fresh claim; later reconciliation idempotent. |
| JOB-017 | P0 | Stop Agent/reboot Windows at safe pre-spooler queue points. | Pending work recoverable or clearly failed; no silent loss/duplicate. |
| JOB-018 | P0 | Simulate spooler acceptance without paper output. | UI does not claim physical completion; explicit output confirmation remains required. |
| JOB-019 | P0 | Physically inspect controlled output and confirm it. | Correct job reaches completed state only after explicit confirmation. |
| JOB-020 | P0 | Confirm output for rejected, unsubmitted or wrong-tenant job. | API rejects; state unchanged. |
| JOB-021 | P0 | Crash after physical spooler submission but before result persistence. | Job is uncertain/output-unconfirmed; never auto-resubmitted. |
| JOB-022 | P0 | Use explicit operator retry for uncertain job in QA. | Duplicate risk is explained; deliberate confirmation is required. |
| JOB-023 | P1 | Compare customer-visible progress at quote/review/payment/dispatch/spooler/output stages. | Status language accurate; spooler acceptance not called physical output. |
| JOB-024 | P1 | Retain output-unconfirmed job beyond normal cleanup window. | Source and recovery metadata remain available. |
| JOB-025 | P0 | Suspend shop while paid/claimed/spooler job exists. | New work blocked; existing physical/paid job follows narrow audited recovery policy. |

## J. Batch review, rejection and refunds

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| REF-001 | P0 | Open shop review for batch of three or more synthetic files. | Every file is accepted by default. |
| REF-002 | P0 | Reject one file before applying review. | Only selected file rejected; other files remain accepted. |
| REF-003 | P1 | Reject then undo before applying review. | File returns to accepted state; no refund/rejection recorded yet. |
| REF-004 | P0 | Reject multiple files and apply review. | Only selected files excluded; remaining files eligible. |
| REF-005 | P0 | Reject every file in verified-paid batch before handoff. | Remaining refundable amount is manually returned/recorded and job/batch reaches correct refunded state. |
| REF-006 | P0 | Select 1-based pages for rejection in paid multi-page file. | Preview shows exact page list and additional refund before confirmation. |
| REF-007 | P0 | Test page allocations for duplex, copies and minimum-charge cases. | Refund uses original quote allocation snapshot, not current price/client amount. |
| REF-008 | P0 | Record two disjoint partial refunds. | Cumulative refund correct and never above original payment. |
| REF-009 | P0 | Attempt to refund previously refunded page. | Previously refunded page cannot be restored/refunded again. |
| REF-010 | P0 | Replay same refund idempotency key. | No duplicate record/amount. |
| REF-011 | P0 | Reuse refund idempotency key across files/jobs. | API rejects unsafe reuse. |
| REF-012 | P0 | Request refund above remaining refundable amount. | API blocks over-refund even if preview/request is tampered with. |
| REF-013 | P0 | Refund after Agent handoff/claim/spooler/output confirmation. | Blocked at documented handoff boundary. |
| REF-014 | P0 | Refund unverified/rejected payment. | No refund recorded for money not verified received. |
| REF-015 | P0 | Return QA money outside SmartPrint, then record manual refund. | Record represents actual shop-sent refund, not automated payout. |
| REF-016 | P1 | Submit refund with invalid amount/reference/reason. | Validation blocks bad record; no false success. |
| REF-017 | P0 | Race refund against Agent claim using coordinated PostgreSQL requests. | Transaction rules ensure either refund-before-handoff or block-after-handoff; never both refund and print same pages. |
| REF-018 | P1 | Inspect physical/PDF output after page rejection. | Rejected pages omitted; accepted pages correctly ordered; original 1-based page mapping respected. |
| REF-019 | P1 | Print accepted remainder after partial refund. | Accepted pages remain eligible; refunded pages never reappear. |
| REF-020 | P1 | Review mixed file outcomes in batch. | Per-file states and batch summary are accurate; one rejection does not cancel other files. |
| REF-021 | P1 | Reconcile payment/refund/collection records after multiple partial refunds. | Gross, refund and net values reconcile exactly in minor currency units. |

## K. Payments, customer directory and reports

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| PAY-001 | P0 | Customer submits synthetic UPI reference for exact job. | Attempt created for correct amount/job; reference remains unverified. |
| PAY-002 | P0 | Submit payment screenshot/reference and inspect dispatch eligibility. | Evidence alone does not verify payment or unlock dispatch. |
| PAY-003 | P0 | Owner independently confirms QA receipt and manually verifies UPI attempt. | One verification event with amount/method/time snapshot. |
| PAY-004 | P0 | Owner rejects UPI attempt with reason. | Attempt rejected; dispatch remains blocked; customer sees safe next step. |
| PAY-005 | P0 | Verify cash attempt. | Owner verification recorded; no payment gateway assumption. |
| PAY-006 | P0 | Verify wrong amount/job, duplicate reference and already-finalised attempt. | API rejects invalid transitions. |
| PAY-007 | P0 | Replay payment verification/double-click. | No duplicate payment event. |
| PAY-008 | P0 | Try to mark payment paid from customer request/browser payload. | Server denies customer self-verification. |
| PAY-009 | P1 | Change UPI settings after existing payment attempt. | Historical payee/UPI snapshot remains intact; new attempts use new settings. |
| PAY-010 | P0 | Compare shop print payment and platform subscription assignment records. | Financial flows remain separate; one cannot activate the other. |
| PAY-011 | P0 | Check for gateway checkout/webhook/refund capability in candidate release. | Mark NOT IMPLEMENTED unless separately delivered; no live gateway tests/claims. |
| CUS-001 | P1 | Open SHOP-A customer directory with signed-in customers and guests. | Only signed-in global customers linked to SHOP-A appear; guests remain anonymous. |
| CUS-002 | P0 | Same global customer uses SHOP-A and SHOP-B; inspect both directories. | Each shop sees only its own sessions/jobs. |
| CUS-003 | P1 | Inspect customer list response and email display. | Email is masked; full global email not returned to shop users. |
| CUS-004 | P1 | Search/paginate with empty, partial, Unicode and long input. | Bounded, stable query; tenant filter cannot be bypassed. |
| CUS-005 | P0 | Change customer ID to another customer's history. | API returns only authorised shop history or denies. |
| RPT-001 | P1 | Generate report with known total/completed/output-unconfirmed jobs. | Counts match fixture. |
| RPT-002 | P1 | Verify pages requested and counts by status. | Aggregates match source jobs. |
| RPT-003 | P0 | Verify gross payment collections by payment verification date. | Later refund does not rewrite earlier gross period. |
| RPT-004 | P0 | Verify refunds by date shop records money was sent. | Refund appears in its own event period. |
| RPT-005 | P0 | Reconcile UPI/cash gross, refunds and net collections. | Exact arithmetic in minor currency units. |
| RPT-006 | P1 | Report for empty period and date/time boundaries. | Correct zero state/timezone boundaries; no off-by-one. |
| RPT-007 | P0 | Access Advanced Reports by unentitled plan through UI/API. | Server denies. |
| RPT-008 | P1 | Compare report totals with transaction detail and QA database fixture. | All totals reconcile. |

## L. Platform administration and tenant lifecycle

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| ADM-001 | P0 | Sign into platform workspace using PLATFORM-ADMIN. | Admin identity opens admin workspace; shop owner account not reused. |
| ADM-002 | P0 | Call platform APIs without token, expired token and shop token. | Server denies all. |
| ADM-003 | P1 | Load overview, tenant search and pagination. | Counts/records consistent with QA fixtures. |
| ADM-004 | P0 | Open SHOP-A and SHOP-B records. | Correct tenant details/status/plan; actions audited. |
| ADM-005 | P0 | Suspend SHOP-A with valid 5–500 character reason. | Status changes; actor, old/new states, reason and timestamp persisted. |
| ADM-006 | P0 | Suspend with blank, too-short or overlong reason. | Rejected; no state change/incomplete audit. |
| ADM-007 | P0 | Try new QR, print submission, quote, payment start, Agent claim and dispatch while suspended. | New work denied server-side regardless of portal navigation. |
| ADM-008 | P0 | Verify permitted read-only history and narrow recovery actions during suspension. | Existing paid/physical work can only be reconciled where lifecycle permits. |
| ADM-009 | P0 | Reactivate suspended shop through authorised admin action. | Active status restored and audited. |
| ADM-010 | P0 | Set tenant Inactive and attempt reactivation. | Inactive is terminal; reactivation rejected. |
| ADM-011 | P0 | Submit invalid lifecycle transitions directly to API. | Only documented state transitions accepted. |
| ADM-012 | P1 | Edit draft plan and publish new version. | Published versions remain immutable and auditable. |
| ADM-013 | P0 | Assign trial/free plan through platform workflow. | Operator/time/plan snapshot recorded and entitlements correct. |
| ADM-014 | P0 | Assign paid plan after independently checking QA receipt and entering reference. | Amount/currency/operator/reference snapshot stored; customer screen cannot grant paid state. |
| ADM-015 | P0 | Attempt plan assignment as shop owner. | API denies. |
| ADM-016 | P1 | Change Operations & retention settings with mandatory reason. | Before/after values, actor/time/reason audited. |
| ADM-017 | P0 | Run retention against completed, unpaid-abandoned, expired-ticket, empty-session and stale-token QA records. | Only eligible records swept; job/payment/refund/audit metadata preserved. |
| ADM-018 | P0 | Run retention on paid, output-unconfirmed and financially held jobs. | Recovery source/document data preserved according to policy. |
| ADM-019 | P1 | Test retention interval boundaries and invalid configuration. | Values bounded; no unsafe sweep interval; update audited. |
| ADM-020 | P1 | Inspect read-only operations overview. | No unnecessary document contents/secrets exposed. |
| ADM-021 | P1 | Attempt unsupported platform support/document access. | No implicit blanket access; unsupported workflow recorded as a gap. |
| ADM-022 | P0 | Hide a UI entitlement then call feature endpoint directly. | API remains enforcement authority. |

## M. Security, privacy and abuse resistance

Execute only in authorised QA/staging with a safe test corpus.

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| SEC-001 | P0 | OWNER-A attempts SHOP-B profile/printer/station/job/document/payment/refund/customer/report access. | Every object is tenant-scoped; no data leak or mutation. |
| SEC-002 | P0 | Attempt vertical escalation to platform admin or privileged Agent action. | Server rejects unauthorised identity/claim changes. |
| SEC-003 | P0 | Change tenant, station, Agent, printer, job, document and payment identifiers. | Server verifies ownership for every operation. |
| SEC-004 | P0 | Submit protected mass-assignment fields: tenant, role, status, verified, refunded, amount, owner and device identity. | Server binds only permitted fields. |
| SEC-005 | P0 | Test SQL injection in auth/search/filters/filenames/report parameters. | No query injection or database detail leakage. |
| SEC-006 | P0 | Test stored/reflected XSS in names, filenames, rejection reasons, references and filters. | Payload rendered as text/rejected; no script execution. |
| SEC-007 | P0 | Test CSRF/origin abuse on state-changing web actions. | Cross-site request cannot perform unauthorised state change. |
| SEC-008 | P0 | Verify production CSP, HTTPS and mixed-content protection. | HTTPS enforced; unsafe content blocked; headers match deployment design. |
| SEC-009 | P0 | Test CORS with allowed origin, sibling host, arbitrary HTTPS, file and null origins. | Only intended origins allowed; credentials not exposed to arbitrary origins. |
| SEC-010 | P0 | Test Agent local API from localhost/private LAN/guest Wi-Fi/remote internet. | Agent API not publicly exposed; remote/disallowed clients denied. |
| SEC-011 | P0 | Inspect API/Agent/browser/evidence logs after login/upload/print. | No tokens, secrets, Wi-Fi credentials, document bytes or payment credentials logged. |
| SEC-012 | P0 | Verify document storage is outside public web root and uses authorised opaque references. | Anonymous/static access fails. |
| SEC-013 | P0 | Test traversal, encoded separators, oversized/decompression inputs and malformed parser files in isolated harness. | Storage stays confined and processing bounded. Confirm ClamAV/Defender fail-closed behaviour and isolated cloud worker limits; native Agent PDFium/Skia process isolation remains a separate open item. |
| SEC-014 | P0 | Verify cloud document AES-256-GCM configuration and deployment-specific key storage. | Correct encryption/key separation; development key not used. |
| SEC-015 | P0 | Capture direct LAN traffic in isolated QA and assess document confidentiality. | Document unencrypted HTTP limitation is explicitly recorded; this case does not close BLK-01. |
| SEC-016 | P0 | Exercise authentication/public/Agent/QR/upload rate limits. | Documented baseline defaults (20 auth/min, 120 public/min, 30 Agent/min, 30 QR/min, 8 uploads/min) or approved current values are enforced. |
| SEC-017 | P0 | Attempt to evade IP limits by spoofing client forwarding/session/Agent headers. | Client cannot control trusted source-IP partition. |
| SEC-018 | P0 | Inspect Cloudflare/Nginx/API forwarded IP chain. | Nginx overwrites forwarded address from trusted Cloudflare client IP; API port remains private. |
| SEC-019 | P0 | Inspect Windows firewall after Agent install. | Rule scoped to required local TCP port and Private profile; not broadly open on Public profile. |
| SEC-020 | P1 | Export diagnostics/evidence bundle. | Secrets, documents and sensitive personal/payment data redacted by default. |
| SEC-021 | P0 | Verify global customer identity is not shop-owned. | Customer identity global; station/jobs remain shop scoped. |
| SEC-022 | P0 | Verify guest history is not silently linked to a signed-in customer. | Guest and account histories remain separate and private. |

## N. Failure injection, recovery and performance

Use isolated environment and record each injected fault.

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| REC-001 | P0 | Disconnect internet before opening new station session. | App clearly explains cloud connectivity required; no offline claim. |
| REC-002 | P0 | Disconnect internet after session but before quote/payment/dispatch. | Accurate pending/retryable state; no false financial/print success. |
| REC-003 | P0 | Disconnect internet after job is authorised and locally queued. | Agent preserves authorised local state as designed; later sync is idempotent. |
| REC-004 | P0 | Disable shop Wi-Fi during direct upload. | Upload remains pending/failed, never falsely complete or printable. |
| REC-005 | P0 | Fill QA Agent disk close to capacity. | New staging fails safely; existing paid/output-unconfirmed work is not deleted. |
| REC-006 | P0 | Crash Agent before queue, while queued, pre-spooler and at uncertain spooler point. | Durable state recovered; no silent loss or automatic duplicate. |
| REC-007 | P0 | Reboot Windows with pending paid and output-unconfirmed jobs. | Queue/recovery evidence retained; no duplicate physical submit. |
| REC-008 | P1 | Kill Desktop while Agent is healthy, then reopen Desktop. | Agent continues independently; Desktop restores status/session. |
| REC-009 | P0 | Stop API while Agent runs, then restore API. | Agent handles heartbeat/claim failures safely and resumes without duplicate claims. |
| REC-010 | P0 | Restart API during quote, reservation, payment and refund QA transitions. | Operation commits once or fails safely; no partial financial/job state. |
| REC-011 | P0 | Run concurrent PostgreSQL operations for quota reservation, quote/payment, Agent claim and refund-vs-claim. | Transactions/constraints ensure one authoritative result; incomplete PG integration coverage remains a blocker. |
| REC-012 | P1 | Load portal with many job/printer/customer records. | Bounded loading/pagination; no unbounded database/API work. |
| REC-013 | P1 | Load concurrent sessions/uploads to approved QA target. | Agreed latency/error SLO met; rate limits/quota locks stable. |
| REC-014 | P1 | Inject slow network, loss and intermittent DNS/API reachability. | Helpful retry guidance; ambiguous side effects checked before retry. |
| REC-015 | P0 | Run retention sweep with eligible/ineligible records. | Only eligible data deleted; financial/job/audit history retained. |
| REC-016 | P0 | Make cloud document store unavailable/full during upload. | Upload fails safely; no complete-job state for missing bytes. |
| REC-017 | P0 | Stop PostgreSQL during a state transition, then restore it. | Controlled API failure; no false in-memory success; transaction integrity retained. |
| REC-018 | P1 | Keep Desktop session open through refresh and overnight idle. | Session remains valid or returns to sign-in cleanly; no refresh storm. |
| REC-019 | P1 | Background Android during scanner/import/session. | Resources released/restored; no duplicate or lost document. |
| REC-020 | P1 | Run 24-hour QA soak with Agent heartbeat, Desktop polling and idle sessions. | No progressive resource leak, unbounded log, stuck spinner or false-ready status. |

## O. Public download proxy and deployment operations

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| OPS-001 | P0 | Check /downloads/status with release containing expected assets. | Only verified current Windows/Android assets and versions reported. |
| OPS-002 | P1 | In QA, make Android signed asset absent. | Android shown unavailable; no debug or wrong asset offered as stable. |
| OPS-003 | P0 | Request /downloads/windows and /downloads/android. | Same-origin server streaming; no client redirect to GitHub/CDN. |
| OPS-004 | P1 | Resume interrupted download with single byte range. | Correct 206/range semantics; no buffering/corruption. |
| OPS-005 | P1 | Send malformed, multiple or unsatisfiable range headers. | Safe 4xx/416; no resource exhaustion or wrong byte stream. |
| OPS-006 | P0 | Simulate upstream timeout, 404, rate limit and 5xx. | Retryable/unavailable response; upstream signed Location never exposed to browser. |
| OPS-007 | P0 | Confirm no historical/wrong version is served as latest stable. | Metadata, size, asset and checksum match. |
| OPS-008 | P0 | Start Compose stack with correct env and persistent volumes. | PostgreSQL/API/portal healthy; existing volumes intact. |
| OPS-009 | P0 | Verify PUBLIC_BASE_URL is the single public app origin. | Portal, API routes and station QR links use canonical HTTPS origin. |
| OPS-010 | P0 | Inspect production-like exposed ports. | Public path via Cloudflare/Tunnel and portal; API port 8080 private. |
| OPS-011 | P0 | Verify Cloudflare published application route targets intended portal service. | One intended SmartPrint origin; no split/legacy route. |
| OPS-012 | P0 | Run release/deploy validation with malformed, non-HTTPS, path/query/fragment origin. | Validation fails closed with clear error. |
| OPS-013 | P0 | Apply EF migrations to isolated PostgreSQL with historical QA jobs/payments/refunds. | Migration succeeds; existing records readable. |
| OPS-014 | P0 | Rehearse upgrade with pre-upgrade backup and rollback plan. | Repeatable migration/recovery; no destructive operation without restore evidence. |
| OPS-015 | P0 | Create database, encrypted-document and recovery-secret backup set in QA. | Complete set captured with one timestamp/version and restrictive permissions. |
| OPS-016 | P0 | Encrypt complete backup set before off-host transfer. | Off-host set encrypted/access controlled; current documented gap remains open until proven. |
| OPS-017 | P0 | Restore DB, document volume and required secrets into isolated recovery environment. | App reads authorised documents and reconciles jobs/payment/refund history; RPO/RTO recorded. |
| OPS-018 | P1 | Restore corrupted/incomplete backup or missing key. | Restore fails safely; never overwrites live deployment. |
| OPS-019 | P0 | Run required CI jobs for exact candidate commit. | Portal/.NET/Agent/Windows/Android/script checks complete green; cancelled/stale runs excluded. |
| OPS-020 | P0 | Validate release workflow in controlled draft path. | Stable semantic version/origin, Windows installer and signed Android artefacts verified; checksums match. |
| OPS-021 | P0 | Verify release immutability and old-version retention. | Published history not replaced/deleted; latest pointer moves only after verification. |
| OPS-022 | P1 | Reboot deployment host and verify service recovery. | Persistent data and services recover without interactive shell dependence. |
| OPS-023 | P1 | Test GHCR pull using actual deployment identity. | Image pulls with documented auth/visibility policy; secrets not exposed in logs/devices. |

## P. Accessibility, usability and visual regression

| ID | Priority | Test / execution | Expected result |
|---|---:|---|---|
| UX-001 | P1 | Inspect every primary screen in light and dark mode. | Text/icons/borders/dialogs/forms have adequate contrast; no white overlay unexpectedly appears in dark mode. |
| UX-002 | P1 | Use keyboard only through Desktop/web primary workflows. | Logical tab order, visible focus, usable controls and no keyboard trap. |
| UX-003 | P1 | Test accessible names/roles/states with screen reader and Android TalkBack. | Icon controls, form fields, dialogs and status have meaningful names. |
| UX-004 | P1 | Zoom browser/Windows to 200%; increase Android font scale. | Primary actions remain visible; no blocking overlap/clipping. |
| UX-005 | P1 | Test portal at phone, tablet portrait/landscape and desktop widths. | Core task reflows without blocking horizontal overflow; navigation reachable. |
| UX-006 | P1 | Inspect loading, empty, success, warning, error, offline and permission-denied states. | Clear state and safe next action; no raw stack trace. |
| UX-007 | P1 | Inspect dispatch/refund/rejection/printer/destructive confirmations. | Dialog states affected item and irreversible/duplicate risk; high-risk action not triggered accidentally. |
| UX-008 | P2 | Check currency/date/time/page-count formatting. | Consistent units/timezone and unambiguous date values. |
| UX-009 | P2 | Use long shop names, Unicode names, long filenames and null server fields. | Layout stable; safe wrapping/truncation and useful fallback. |
| UX-010 | P1 | Inspect error text for local network denial, expired QR, unsupported file, pending payment and printer offline. | No private IP/token/stack trace; plain-language recovery guidance. |

---

# 6. Mandatory end-to-end acceptance journeys

These are release-level workflows. Run each on a fresh fixture and retain continuous evidence.

### E2E-01 — Customer QR to verified shop output, cloud-assisted

1. Owner signs into SHOP-A Desktop and verifies Agent/printer status.
2. Customer scans SHOP-A QA station QR in ordinary HTTPS browser.
3. Customer gets station-bound guest session.
4. Upload synthetic PDF by cloud-assisted mode.
5. Change Copies from default; configure supported print options; request quote.
6. Verify quote is server-calculated; accept it.
7. Shop sees job exactly once, reviews and approves.
8. Customer submits test UPI reference or cash selection.
9. Shop independently verifies QA payment via owner flow.
10. Shop explicitly dispatches to QA-PRINTER.
11. Inspect Agent/spooler status and physically inspect output.
12. Shop explicitly confirms output.
13. Verify customer sees accurate terminal status.
14. Reconcile session, job, quote, payment and physical output.

**Pass:** one intended job, one payment record, one controlled physical output, correct Copies, accurate status at every stage, no cross-tenant data, no duplicate submit, no false output completion.

### E2E-02 — Same lifecycle using direct LAN upload

Repeat E2E-01 via direct LAN on isolated shop network. Verify byte count/hash and same cloud-coordinated lifecycle. Explicitly retain the unencrypted LAN transport blocker; this test must never use guest/public Wi-Fi.

### E2E-03 — Multi-file batch, one rejected file and partial refund

1. Upload at least three synthetic files.
2. Confirm every file starts accepted.
3. Reject one file, undo before applying, then reject and apply.
4. Confirm other accepted files remain eligible.
5. Use a separate verified-paid test file for page-level rejection.
6. Inspect original-quote allocation preview and exact refund amount.
7. Return synthetic QA money outside SmartPrint, then record refund.
8. Verify cumulative refund, idempotency and no over-refund.
9. Print/export only accepted/non-refunded pages.
10. Reconcile gross/refund/net report values and batch states.

**Pass:** accepted-by-default, undo-before-apply, per-file isolation, correct original allocation, accurate manual refund, no double refund and no refunded page in output.

### E2E-04 — Android scan, library and print

1. Install signed QA APK and record package/version/signature.
2. Scan SHOP-A HTTPS station QR with CameraX.
3. Import a synthetic PDF via SAF and create a folder.
4. Select saved file using native picker.
5. Verify shared portal session, Copies, quote and customer submission.
6. Complete shop review/payment/dispatch/output.
7. Restart Android and confirm app-private files remain.

**Pass:** correct shop, same server-owned lifecycle, correct file, no duplicate scanner/state machine, exact Copies and saved file persistence.

### E2E-05 — Windows/Android owner-to-customer Wi-Fi readiness

Run every case PHY-01 to PHY-17 in docs/ANDROID_PHYSICAL_ACCEPTANCE.md and its full owner-to-customer sequence. A launcher smoke test or a readiness screen appearing is not evidence that the complete workflow passes.

### E2E-06 — Tenant isolation and suspended-shop recovery

1. Create fixtures under SHOP-A and SHOP-B.
2. Attempt cross-tenant operations with each owner.
3. Suspend SHOP-A with platform admin and reason.
4. Attempt new QR/session/quote/payment/Agent claim/dispatch.
5. Confirm new work blocked and SHOP-B unaffected.
6. Reconcile permitted existing paid/physical work.
7. Reactivate SHOP-A; then permanently inactivate SHOP-C and test terminal behaviour.

**Pass:** no cross-tenant access, suspension enforced at API, recovery narrow/audited, SHOP-B unaffected, inactive terminal.

### E2E-07 — Uncertain spooler output and recovery

1. Submit one synthetic job to QA-PRINTER.
2. Inject Agent/spooler interruption at uncertain point.
3. Confirm output-unconfirmed/uncertain status.
4. Verify no automatic retry.
5. Inspect actual printer output before operator retry.
6. Use risk-confirmed retry only if safe.
7. Confirm output after physical inspection and reconcile attempts.

**Pass:** no duplicate automatic output; operator risk is explicit; completion only after physical confirmation.

### E2E-08 — Complete backup and restore

1. Create QA shop, station, jobs, documents, payment/refund and audit records.
2. Back up PostgreSQL, encrypted document storage and recovery secrets as one set.
3. Encrypt entire set before off-host storage.
4. Restore into separate isolated environment.
5. Verify login, tenants, authorised documents, quotes, payments/refunds and job recovery.
6. Record RPO/RTO and missing records.

**Pass:** full restore demonstrated from encrypted set; no production volume touched.

---

# 7. Known release blockers and closure evidence

| ID | Gap | Closure evidence |
|---|---|---|
| BLK-01 | Direct LAN document bytes not end-to-end encrypted in transit. | Cryptographic design, key lifecycle/rotation, downgrade/replay tests, packet capture proving confidentiality and Android/Windows interoperability. Until then restrict to trusted segmented LAN and obtain explicit risk decision. |
| BLK-02 | Agent least-privilege service identity not validated/configured. | Service account design, ACL/printer/DPAPI permission matrix, install/upgrade and negative privilege tests on supported Windows devices. |
| BLK-03 | Cloud scanning/parser isolation and direct-LAN Defender gate implemented in candidate; native Agent parser isolation remains open. | Run EICAR/safe corpus, scanner-unavailable/timeouts, worker-auth/resource-limit tests, native Windows Defender acceptance and prove PDFium/Skia cannot crash or compromise the Agent service process. |
| BLK-04 | Email verification/account recovery absent. | One-time expiring verification/recovery, abuse/enumeration tests, account notification and audit. |
| BLK-05 | Candidate adds refund-vs-claim, two-Agent claim and subscription webhook-vs-checkout races. | Execute full isolated PostgreSQL matrix: quota/upload reservations, payment and refund transitions, claim/handoff, tenant suspension/lifecycle and idempotency; retain DB assertions and race outcome evidence. |
| BLK-06 | Candidate adds encrypted v2 recovery bundle and isolated DB/document integrity drill. | Configure independent off-site remote; verify rclone content; perform restore drill from that remote bundle; record database table counts, QSP1 tag/hash/size checks, RPO/RTO and pass evidence. |
| BLK-07 | Per-tenant storage quota and Agent disk-loss re-upload recovery incomplete. | Tenant quota enforcement and recovery tests that preserve paid work and avoid duplicate jobs. |
| BLK-08 | Physical printer compatibility matrix incomplete. | Named printer/driver/connection matrix with real output for size, colour, duplex, copies, page selection, faults and uncertain spooler recovery. |
| BLK-09 | Android physical and shop-Wi-Fi acceptance outstanding until recorded. | PHY-01 through PHY-17 and full owner-to-customer sequence on real supported devices. |
| BLK-10 | Automated gateways, subscription billing, webhooks and automated refunds absent. | Exclude from release claims or implement and separately certify signed provider verification/idempotency/reconciliation. |
| BLK-11 | Interim Windows installer is unsigned. | Signed installer/update chain or formally approved limited-beta distribution policy and managed-device compatibility decision. |
| BLK-12 | Retention/legal-hold policy needs real-shop validation. | Approved schedule, dispute/legal-hold policy, tenant deletion/export and operational sign-off. |

---

# 8. Production release exit criteria

Production Accepted only when all are true:

- [ ] Candidate commit, Windows installer, Android APK, Agent and API/portal image digests are recorded.
- [ ] Required CI jobs for exact candidate commit are green; no stale/cancelled run counted.
- [ ] Applicable RLS cases pass.
- [ ] Every P0/P1 AUTH, SHOP, AGT, QR, AND, DOC, QTE, JOB, REF, PAY, CUS, RPT, ADM, SEC, REC, OPS and UX case has a result.
- [ ] E2E-01 through E2E-08 pass in isolated QA.
- [ ] Android PHY-01 through PHY-17 and complete owner-to-customer journey pass on real supported devices.
- [ ] At least one named Windows printer/driver configuration has actual physical output evidence; other models are recorded separately.
- [ ] Tenant isolation/role boundaries have zero leak or unauthorised action.
- [ ] No duplicate payment/refund/dispatch/physical output in replay/concurrency tests.
- [ ] No unresolved P0 defects; P1s fixed/retested or formally accepted with safe workaround and target.
- [ ] BLK-01 through BLK-12 closed or explicitly accepted by accountable release authority. Security, tenant isolation, financial integrity and data recovery must not be waived informally.
- [ ] Full backup/restore rehearsal successful.
- [ ] Release notes, known limitations, support/recovery and rollback procedure published.
- [ ] Sanitised evidence retained against exact release commit.

A download pass is not an install pass. A Desktop window is not a rendered workspace. Android launch is not QR/file/LAN/hardware acceptance. Green unit tests are not PostgreSQL concurrency/physical tests. Agent heartbeat is not verified pairing/printing. Payment reference is not verified payment. Spooler acceptance is not physical output. Backup creation is not restore proof.

## 9. Defect report template

**Title:** [P0/P1/P2/P3] [Area] concise failure  
**Case ID:**  
**Release version / commit:**  
**Environment / device:**  
**Fixture IDs / preconditions:**  
**Reproduction steps:**  
**Expected:**  
**Actual:**  
**Frequency:** always / intermittent / once  
**Impact:** customer / shop / financial / privacy / security / data / operations  
**Evidence reference:** sanitised log/screenshot/video/API trace/physical sample  
**Sensitive-data check:** no token/password/document/payment credential attached  
**Workaround:**  
**Owner / target fix:**  
**Retest commit / result:**  

## 10. Run summary and sign-off

| Metric | Result |
|---|---|
| Cases in this document | |
| Passed / failed / blocked | |
| Not implemented / N/A / not run | |
| Open P0 / P1 defects | |
| E2E journeys passed / total | |
| Android physical cases passed / total | |
| Printer models physically verified | |
| PostgreSQL concurrency suite | |
| Backup restore drill | |
| Exact candidate commit | |

| Sign-off role | Name / initials | Date | Outcome / exceptions |
|---|---|---|---|
| QA owner | | | |
| Windows/Desktop/Agent owner | | | |
| Android/customer journey owner | | | |
| API/security reviewer | | | |
| Operations/backup owner | | | |
| Product/release authority | | | |

**Final status: NOT SIGNED OFF** until all applicable P0/P1 cases and mandatory end-to-end/release gates have recorded outcomes.
