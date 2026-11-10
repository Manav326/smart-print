# SmartPrint final pre-production release gate

**Purpose:** One authoritative stop/go checklist for the final SmartPrint candidate. This document joins the existing detailed QA catalogues; it does not replace their individual steps or expected results.

**Candidate branch:** `feature/smartprint-clean-qa-runtime`  
**Review:** [PR #52](https://github.com/Manav326/smart-print/pull/52) — open, draft; not approved for merge.  
**Release state:** **NO-GO / NOT SIGNED OFF** until every mandatory gate below has evidence on the exact release candidate and all P0 blockers are closed or formally dealt with under the release authority's rules.

## 1. What must be executed

The detailed test cases already live in the linked catalogues. Their counts have been reconciled from the actual unique case-ID rows in the files.

### 1.1 Master product/release catalogue — 345 unique cases

| Group | Cases | Primary coverage |
|---|---:|---|
| RLS | 20 | Release artefacts, Windows install/runtime, Desktop launch and upgrades |
| AUTH | 19 | Registration, sign-in, roles, session and access boundaries |
| SHOP | 17 | Shop profile, pricing, payment options and entitlements |
| AGT | 26 | Agent identity, pairing, readiness, queue and printer integration |
| QR | 16 | Station code, customer entry, session scope and expiry |
| AND | 30 | Android application, scanner, file handling and document studio |
| DOC | 26 | File validation, upload, batch and document integrity |
| QTE | 12 | Quote calculation, snapshot and expiry |
| JOB | 25 | Queue, review, assignment, dispatch and physical-output states |
| REF | 21 | Per-file/per-page rejection, refund allocation and replay safety |
| PAY | 11 | Manual UPI/cash attempts, verification and financial integrity |
| CUS | 5 | Customer directory and customer-scoped history |
| RPT | 8 | Reporting periods, tenant scope and financial summaries |
| ADM | 22 | Platform admin, tenants, plans, operations and audit |
| SEC | 22 | Authentication, tenant isolation, file, network and abuse security |
| REC | 20 | Failure recovery, replay, restart and uncertain output |
| OPS | 23 | Retention, backup, deployment, monitoring and operational controls |
| UX | 10 | Accessibility, responsive presentation and theme consistency |
| BLK | 12 | Explicit release blocker closure evidence |
| **Total** | **345** | **Every case needs an explicit result** |

Source: [Production QA Master Test Plan](PRODUCTION_QA_TEST_PLAN.md).

### 1.2 Role and native-surface supplement — 174 unique cases

| Group | Cases | Primary coverage |
|---|---:|---|
| WOWN | 9 | Shop-owner Web workflow and account controls |
| WCUS | 10 | Customer Web/PWA workflow |
| ANDX | 23 | Android account, document studio, QR/session and native UI |
| APHO | 36 | Customer Photo Studio inside Android WebView |
| WDSK | 18 | Actual Windows Desktop owner workspace |
| XPLAT | 12 | Cross-platform handoff and same-work lifecycle |
| HST | 14 | History, session continuity, restore boundaries and isolation |
| PHOX | 21 | Customer photo jobs, editing, shop-originated photo orders, payment and export |
| PSHT | 31 | Photo tile dimensions, grid positions, page layout, quantity and physical print |
| **Total** | **174** | **Every case needs an explicit result** |

Source: [Role-based cross-platform acceptance matrix](ROLE_BASED_CROSS_PLATFORM_QA.md).

The 345 + 174 catalogues therefore contain **519 unique manually/role-mapped case IDs**. Other suites below have their own execution IDs or assertion granularity. Do not add them to 519 and call the result a unique feature-case count: many exercise the same business rules through a different layer or surface.

### 1.3 Additional execution gates that are not replacements for the catalogues

| Suite | Required scope | Release evidence |
|---|---|---|
| Local real-data browser/API E2E | 262 mapped acceptance records using isolated local accounts, API and PostgreSQL | 262/262 required records PASS; no unmet prerequisites silently counted as pass |
| Responsive/appearance browser suite | Public, login, shop, admin and customer station routes across viewports/themes; currently 295 assertions in the latest complete baseline | Assertion report, screenshots, route/viewport/theme record |
| Android emulator | JVM tests plus connected instrumentation; all four Android Photo Studio WebView tests must execute | JUnit XML with zero ignored Photo Studio tests |
| Android physical device | PHY-01–PHY-17, APHO-001–036 on the native app, and relevant PSHT cases | Actual supported phone/tablet, OS/API, logs and generated PDFs |
| Native Windows Desktop | WDSK suite and RLS-017/RLS-018 on the actual Tauri/WebView2 executable | Native UI interaction evidence, not an Ubuntu desktop-route simulation |
| Windows Agent/installer | Agent tests, real Microsoft Print to PDF spooler integration, install/upgrade/rollback/uninstall | Windows job logs and lifecycle evidence |
| Physical printer | Named model + driver + connection + paper/output matrix, including PSHT geometry | Actual paper samples, printer/driver/version and measured dimensions |
| PostgreSQL concurrency | Concurrent quota reservations, idempotency, payment/refund/Agent claims and tenant transitions | Reproducible real-PostgreSQL integration results |
| Backup/restore | Database + encrypted documents + required secrets as one recovery set | Isolated restore drill, RPO/RTO and verification record |
| Security | LAN confidentiality, service identity/ACLs, malware/parser isolation, auth/rate limiting and tenant boundaries | Security design, negative tests, packet evidence and reviewer approval |

## 2. Baseline CI evidence and what it proves

The last fully completed baseline CI run before the Android fixture correction was [run #3524](https://github.com/Manav326/smart-print/actions/runs/42433709039), commit `24f2c2abe69ba691f5aa3fd235b4522c0e0257e1`. Its automated jobs completed successfully; container publishing was skipped.

| Runner/suite | Observed result | Interpretation |
|---|---|---|
| Portal logic | 160 passed | Includes 43 Photo Studio unit/logic tests |
| Browser appearance/responsive | 295 passed assertions | Includes six synthetic Photo Studio browser scenarios and route/viewport/theme checks |
| API/.NET | 373 passed | API unit/regression suite |
| Windows Agent/.NET | 148 passed | Agent unit/regression suite; real spooler scenarios are separate runner steps |
| Android JVM | 42 passed | JVM policy/storage/UI logic |
| Android emulator instrumentation | 44 total: 40 passed, 4 ignored | All four ignored were Android Photo Studio WebView cases; this was not Android Photo Studio acceptance |
| Windows native Rust host | 1 passed | One host unit test, not a Desktop feature suite |
| Windows Desktop launch | 1 launch/render smoke flow | WebView2 and React rendered; not full native UI acceptance |
| Windows installer | 7 passed lifecycle scenarios | Fresh install, same-version reinstall, upgrade, failed-upgrade rollback, uninstall, reinstall recovery |
| Android package lifecycle | 8 passed lifecycle scenarios | Fresh install, reinstall, upgrade/downgrade, signing mismatch, uninstall/data semantics and production-signed version update |
| QA harness checks | 74 passed | Test-tooling/config validation, not product-feature coverage |

The arithmetic count sometimes quoted as 842 is **not 842 unique product scenarios**: it combines 160 Portal + 373 API + 148 Agent + 1 Rust + 86 Android (42 JVM + 44 instrumentation) + 74 QA harness tests. It includes 4 ignored Android cases. The 295 browser assertions and 7 Windows/8 Android lifecycle scenarios are reported separately because they represent different test units and overlap with feature coverage.

The six Windows-desktop route checks in the responsive Chromium suite are rendered web views at a desktop viewport. They are **not** the Windows native executable's interactive UI. A native launch smoke is likewise not proof that every menu, button, dialog, file picker, printer command or recovery path works.

The Android emulator was API 36. Even after the four WebView tests pass on that emulator, it does not prove compatibility across physical manufacturers, OS versions, document providers, cameras or shop Wi-Fi networks.

## 3. Newly enforced Android Photo Studio CI gate

The previous run's JUnit XML explicitly marked these four tests as skipped because `PHOTO_STUDIO_QA_ENABLED` was false:

1. `customerPhotoStudioRunsInsideAndroidWebViewAndProducesExactMultiPagePdf`
2. `shopOwnerCanEditAnExistingPhotoJobInsideAndroidWebViewAndSaveReplacementPdf`
3. `androidSystemBackClosesPhotoStudioBeforeLeavingTheCustomerStation`
4. `photoStudioModalStateAndSecureFlagSurviveActivityRecreation`

The candidate CI now builds the Vite Photo Studio fixture only with `VITE_ENABLE_PHOTO_STUDIO_QA=true` in the isolated Android job; serves it on the runner's port 3000; targets it from emulator `10.0.2.2:3000); and runs the side-by-side `com.quantaedge.smartprint.localqa` debug package with `smartPrintPhotoStudioQaEnabled=true`. The production portal build separately checks that `/tests/photo-studio.browser.html` is absent.

After instrumentation, the Android script parses the JUnit XML and requires exactly the four named Photo Studio tests, exactly once each, with no skip/failure/error. A green Gradle exit code without those report assertions is no longer sufficient. This CI run must pass on the exact candidate SHA before the four cases can be marked PASS.

The Android Photo Studio tests are synthetic QA fixture flows. They test the customer editor and the shop-mode existing-job editing surface inside the same shared WebView component. Shop-owner-originated order creation is PHOX-015–021 and also requires the real local API/DB journey; a fixture-mode UI test is not evidence of persisted server behaviour.

## 4. Critical production blockers and release limitations

The following are not closed by a green build or a source-code inspection. Their state must be recorded explicitly in the release record.

| Finding | Severity / release treatment | Required closure evidence |
|---|---|---|
| BLK-01 — Direct LAN document bytes use private-network HTTP and are not end-to-end encrypted in transit | **P0 security blocker** unless formally risk-accepted by accountable security/release authority; never use guest/public Wi-Fi | Authenticated encryption design, keys/rotation, downgrade/replay tests and packet capture proving confidentiality |
| BLK-02 — Windows Agent least-privilege service identity not proven | **P0 security/operations blocker** | Supported service account, DPAPI/ACL/printer permission matrix, negative privilege tests, install/upgrade/recovery tests |
| BLK-03 — Malware scanning and parser isolation partially implemented in candidate | **P0 remains open** | Cloud uploads now require ClamAV and an isolated resource-limited parser worker; direct-LAN uploads require Windows Defender and fail closed. Remaining: isolate the native Agent PDFium/Skia parser/renderer, execute real Windows clean/malicious/scanner-outage acceptance, verify time/resource limits and retain audit evidence |
| BLK-04 — Email verification and secure account recovery absent | P1 / capability not delivered; exclude from claims and approve a documented support/recovery process | Verification/recovery implementation and abuse/enumeration tests, or explicit scoped limitation approved for release |
| BLK-05 — PostgreSQL concurrency coverage expanded; final matrix evidence pending | **P0 remains open until the complete isolated suite passes** | Candidate adds refund-vs-Agent-claim in both lock orders, two distinct Agent claimants, and concurrent Razorpay checkout/webhook capture; retain real-PostgreSQL results and finish upload-completion, payment-transition, quota, idempotency and tenant-lifecycle race coverage |
| BLK-06 — Authenticated encrypted backup and isolated restore drill implemented in candidate; operational rehearsal pending | **P0 remains open until off-site restore evidence is retained** | Candidate bundles PostgreSQL, encrypted documents, `.env` and Cloudflare Tunnel token in AES-256-GCM; backup requires off-site rclone content verification by default; isolated drill checks restored DB references against QSP1 AES-GCM document tags/sizes/hashes. Still configure the real off-site target, run it with owner-held passphrase and record RPO/RTO from an off-site restore |
| BLK-07 — Per-tenant storage quota and local-Agent disk-loss re-upload recovery incomplete | P1; elevate to P0 if paid/unconfirmed source data can be lost | Quota isolation and disk-loss/re-upload tests proving no data loss or duplicate job |
| BLK-08 — Physical printer compatibility matrix incomplete | Core product acceptance gate | Named printer/driver/port/paper tests with real output, exact photo dimensions, colour, copies, duplex, page selection and uncertain-output recovery |
| BLK-09 — Android physical and shop-Wi-Fi acceptance not recorded | P1 native acceptance gate | PHY-01–017, APHO-001–036 and local-network tests on actual supported phone/tablet |
| BLK-10 — Automated payment gateways, subscription billing/webhooks and automated refunds absent | Not implemented; manual UPI/cash only | Keep it out of feature claims until provider-backed signed verification/reconciliation/refund flows are delivered and separately certified |
| BLK-11 — Windows beta installer is unsigned | P1 distribution/trust blocker for general production distribution | Authenticode signing, certificate chain/timestamp/upgrade verification and SmartScreen/managed-device tests, or strictly limited approved beta policy |
| BLK-12 — Retention/legal-hold policy needs real-shop validation | P1 operations/compliance gate | Approved schedule, dispute/legal hold, tenant export/deletion, audit and support sign-off |
| PSHT-025 — API checks A4 page/raster structure but does not inspect individual photo tiles inside the raster | **P0 photo-output trust-boundary finding** | Server-side tile/raster-content verification OR an approved, audited mandatory shop PDF visual/geometry verification gate; then PDF raster inspection and physical ruler/caliper evidence |
| PHOX-015–021 — Shop-owner-originated photo orders | Implemented in candidate source; **acceptance pending** | Real local-data creation, existing-customer vs walk-in, UPI/cash pending verification, idempotency, validation, cancellation and tenant isolation |

Additional limitations which must not be advertised as supported: automated subscription billing; universal IPP/vendor printer compatibility; iOS/Play Store distribution; Office document conversion; fully offline quote/payment/printing; automatic Android App Link launch from external links; unlimited customer Print History; restoring/reprinting directly from Android customer history; Agent tray companion/automatic updater; complete manager/operator role matrix.

Source of blocker definitions: [master QA plan](PRODUCTION_QA_TEST_PLAN.md), [roadmap](ROADMAP.md), [implementation tracker](IMPLEMENTATION_TRACKER.md), and [security/operations](SECURITY_AND_OPERATIONS.md).

## 5. Required final execution order

Run this order on a single candidate SHA. A fix creates a new SHA and requires affected test suites to be rerun.

1. **Source and automated CI:** verify clean PR head, build/typecheck, all Portal/API/Agent/Android/Windows/PostgreSQL jobs; confirm exact test totals, zero required skips, and no publish job.
2. **Web real-data E2E:** prepare isolated synthetic accounts for two shops, platform administrator and customer; execute all 262 mapped records against local Docker/API/PostgreSQL; retain each case ID and run JSON. Prove shop/customer/platform isolation, local/cloud upload, quote, review, reject/undo, manual payment verification, printer assignment, dispatch, output, reports, retention and recovery. Do not use production accounts/data.
3. **Photo Studio customer journey:** upload synthetic images as the customer; exercise each supported size, quantity boundary, crop/zoom/position, looks, brightness/contrast/colour, background sampling/replacement, optional name/DOB captions and font/colour, density/photos-per-page, preview navigation, partial final page, retries and error recovery. Reconcile count → pages → PDF bytes → embedded rasters → visual tiles.
4. **Shop-owner photo journey:** edit a customer's existing unpaid photo job; save replacement to the same job; re-open and prove source/original retention. Separately run PHOX-015–021: shop-originated order, existing shop customer, walk-in, pending UPI/cash, manual verification, idempotency, invalid/oversize input, cancel/retry and tenant checks.
5. **Android native app:** run emulator suite with all four Photo Studio WebView tests actually executed; then physical device PHY/APHO tests on named supported Android phones/tablets, orientations/font scales, camera, file-provider/cloud files, process death, permission denial/grant, low memory, offline/retry and shop-Wi-Fi. Use only test shop and synthetic images.
6. **Windows Desktop/Agent:** install the candidate on a disposable Windows VM; exercise every implemented WDSK destination in the native executable, keyboard/mouse/file pickers, resize/minimise/restore/DPI, theme, session, offline/restart, WebView2 present/missing, Agent pairing/revocation, printer enumeration, queue reload and safe uncertain-output recovery. Run the actual installer upgrade/rollback/uninstall cases separately.
7. **Physical print validation:** for every printer/driver explicitly advertised as supported, record model, driver/version, connection, paper stock, tray, orientation, scaling/fit mode and firmware. Print calibration/test sheets and PSHT cases. Measure each photo tile with ruler/caliper, margins/gutters/coordinates and page count; record actual print count and inspect the partially filled final page. Do not generalise to untested printer models.
8. **Security and operations:** complete BLK-01–BLK-07 and BLK-12 closure evidence; run rate-limit/auth/tenant isolation, payload trust, malicious document, concurrency, restart/disk-full, retention/legal hold, encrypted backup restore and rollback drills.
9. **Sign-off:** resolve P0; fix/retest P1 or obtain explicit time-bounded written risk acceptance; record limitations and release version/digests; obtain QA, security, operations and release-authority approvals.

## 6. Exact candidate evidence template

Every catalogue ID has one status for the exact same candidate SHA. Do not combine passes from different commits into one release sign-off.

| Field | Required value |
|---|---|
| Case ID | Exact ID from master/supplement/physical catalogue |
| Candidate | Git commit SHA; branch and PR |
| Build | Portal, API, Agent, Android APK, Windows Desktop and installer versions/digests where applicable |
| Environment | Local/staging, DB schema, OS/API, device/printer model and driver |
| Fixture | Synthetic shop/account/document/order identifiers, no secrets |
| Preconditions | Exact role, station, subscription, network and job state |
| Steps | Reproducible ordered actions |
| Expected / actual | Concrete observed outcome, including amounts/pages/bytes where relevant |
| Result | PASS / FAIL / BLOCKED / NOT RUN / NOT IMPLEMENTED / NOT APPLICABLE |
| Evidence | Run/job URL, log, report, screenshot/video, PDF hash and physical measurement record |
| Defect / approval | Issue, severity, owner, fix SHA and retest; or authorised risk acceptance |

A case is PASS only when all expected results were observed and its evidence retained. A written case, code path, UI rendering, HTTP 200, APK build, Agent heartbeat, spooler acceptance, or successful backup command alone is not a PASS.

## 7. Final go/no-go decision

**GO** requires all of the following:

- Exact candidate SHA has a fresh, fully green required CI run; no required test is skipped or absent.
- Every applicable P0 case in the 345 master and 174 supplement has PASS evidence; every P1 has PASS or written, time-bounded release-authority acceptance.
- All 262 local real-data records are complete; Android physical and printer matrices are explicitly completed for the supported launch configurations.
- PSHT-025 server/output trust boundary has been closed or formally approved with the mandatory visual verification control and physical measurement evidence.
- BLK-01 through BLK-12 have documented closure/approval; security, tenant isolation, financial integrity and recoverability cannot be informally waived.
- Production signing, build provenance, checksums, backup/restore and rollback are verified for the actual distribution channel.
- Product/download claims match the implemented limitations. Manual UPI/cash is not described as automated gateway verification. Unsigned beta installer is not represented as signed/trusted production distribution.
- QA, security, operations and accountable release authority have signed the exact candidate.

**NO-GO** if a P0 remains unresolved, a required case is BLOCKED/NOT RUN/NOT IMPLEMENTED, Android Photo Studio is skipped, a real print-output claim lacks physical evidence, data recovery is unproven, or the tested SHA differs from the candidate being released.

**Current decision: NO-GO / NOT SIGNED OFF.** BLK-03, BLK-05 and BLK-06 now have implementation and regression coverage in a separate hardening candidate, but they are not marked closed until the exact branch CI, native Windows acceptance and real off-site restore rehearsal are evidenced. This is intentional: the existing implementation and green CI baseline are meaningful progress, but the documented security, photo-output, hardware and recovery gaps must be resolved or formally approved before production.
