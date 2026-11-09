# SmartPrint — local real-data exhaustive QA

This is the execution guide for the Windows QA runner in this repository.

## Testing boundary

The exhaustive acceptance run targets only the local SmartPrint stack:

- Portal: http://127.0.0.1:3000
- API through the portal reverse proxy: http://127.0.0.1:3000/api/...
- Local PostgreSQL and document storage: the persistent volumes attached to compose.local.yml

The main acceptance journey refuses any target other than the local loopback portal. Its browser and API requests are same-origin. It does not point the test browser at the public site, use the production database, or use mock API routes, synthetic JWTs or fixture customer sessions. The separate opt-in `-TestPublishedApps` check only reads the configured public release metadata and downloads published app assets; it does not access production customer data.

The Cloudflare Free/Bot Fight Mode 403 issue is therefore irrelevant to this local run. No Cloudflare security changes are required.

## One-command run

From a Windows PowerShell window opened in the local SmartPrint repository, with the existing QA PR branch checked out:

~~~powershell
git pull --ff-only origin feature/smartprint-clean-qa-runtime
.\scripts\qa\run-smartprint-qa.ps1
~~~

The runner:

1. Checks Node.js 22.6+, Docker Desktop and Docker Compose.
2. Reuses the existing local .env.local and persistent volumes. The runner pins Docker Compose to project `smart-print` with `--project-name`, overriding any inherited `COMPOSE_PROJECT_NAME`. The existing PostgreSQL and document volumes retain their explicit legacy names (`qrtoprint-local_qrtoprint_local_database` and `qrtoprint-local_qrtoprint_local_documents`) and are mounted as external volumes, so the project rename does not create or reset a new database. The bootstrap never uses `--remove-orphans` and the runner never runs `docker compose down -v`.
3. Verifies localhost port 3000 is not held by another Docker Compose project (and never stops foreign containers), rebuilds the current portal/API containers under the isolated `smart-print` project name, checks the portal at `127.0.0.1:3000`, and probes the private API `/health/ready` endpoint from inside the API container. API host port 8080 is not published.
4. **Before starting the long browser suite**, checks that running API and portal container image IDs match the images just built, API liveness succeeds directly and through Nginx, a preflight accepts the exact `http://127.0.0.1:3000` origin, and the compiled portal contains no Google Fonts network references. A failed prerequisite stops before QA account bootstrap or Playwright and records a diagnostic log.
5. Creates or reuses four clearly labelled QA identities: two shop owners, one global customer and one platform administrator. Shop/customer registration uses the real app registration path; the platform administrator uses the existing protected one-time bootstrap command in the local API container. The bootstrap refuses to elevate a shop user.
6. Stores generated passwords in .secrets/smartprint-qa/qa-accounts.dpapi.json, protected with Windows DPAPI for the current Windows user. Passwords are not printed, placed in test reports or committed to Git.
7. Installs/locates the pinned Playwright package and Chromium in the npm cache. Before the isolated visual suite, resolves the repository-pinned `pnpm@10.18.0` (Corepack first, then the pinned direct pnpm fallback) and prepares the Portal/Desktop workspace dependencies with `pnpm install --frozen-lockfile --prefer-offline` using the two Vite app filters. The lockfile is never rewritten.
8. Validates the E2E JavaScript syntax and runs the headed Playwright-managed Chromium browser against the real local portal and API. It does not launch or fall back to the system-installed Microsoft Edge. It then runs the separate synthetic-fixture visual suite `apps/portal/tests/theme-responsive.browser.mjs`, which covers responsive routes, light/dark layouts, the full shared customer/shop Photo Studio control and failure matrix, A4 PDF raster/pagination checks and the Windows Desktop WebView viewport. The fixture suite intercepts API calls with 503 responses; it creates no real customer data, job, payment, refund or physical print. Its portal/Desktop Vite servers use loopback ports 4173 and 1420, are started and stopped by that suite, and a busy port fails safely without stopping its existing owner.
9. Runs the existing portal logic tests, API/.NET tests and Windows Agent tests when the corresponding SDKs are installed. The destructive PostgreSQL migration test is excluded from the local application database by leaving SMARTPRINT_TEST_POSTGRES unset.
10. If the Microsoft Print to PDF virtual queue is installed, opts in to the Agent's real GDI spooler integration test. That test verifies the Windows virtual printer produced a PDF; it does not send paper to a physical printer.
11. If system Gradle and the Android SDK are installed, runs Android JVM tests and builds a local-portal debug APK. If an Android emulator is already running, it also runs connected instrumentation tests. It never installs the APK on an attached physical phone.

Generated logs, screenshots and JSON/TXT reports are written under `artifacts/smartprint-qa/`; this directory is Git-ignored. QA account passwords remain DPAPI-protected under `.secrets/`.

### Optional published Windows and Android app acceptance

The published app check is opt-in and runs after the local QA suites:

~~~powershell
.\scripts\qa\run-smartprint-qa.ps1 -TestPublishedApps
~~~

It reads `scripts/qa/smartprint-qa.config.json`, checks the public release status endpoint, and downloads the published Windows installer and Android APK through the website's same-origin download routes. Transfer length, attachment names, package structure and SHA-256 evidence are recorded under the run's `published-apps` directory.

For Windows, an existing SmartPrint installation is only launched and smoke-tested; its installer is never run over it. A new install is considered only when the install folder, Agent service and SmartPrint machine-data directory are all absent and the current PowerShell session is elevated. A running desktop session or ambiguous/partial install is left untouched and reported as skipped.

For Android, app checks run only when exactly one connected ADB device matches the configured emulator serial pattern. The downloaded APK is checked with Android SDK tooling for its package ID, versionCode, versionName and declared launcher activity. Before launch, the runner reads the installed package version and asks Android Package Manager to resolve its MAIN/LAUNCHER component for the active Android user. A mismatch or missing launcher is reported as one root failure; dependent UI checks are skipped. An existing installation is never updated, uninstalled or cleared. The APK is installed only when that package is absent. Physical phones and multiple connected devices are never targeted in this combined mode. The suite does not pair a shop, change printer settings, or uninstall an app.

Windows and Android checks are independent: a failure in one platform is recorded without preventing the other platform from running. Downloads and result evidence remain in the QA artifacts folder; successfully installed apps are left in place for inspection.

No extra branch, remote QA secret, Cloudflare rule or manual account setup is required.

### Fast incremental E2E after a GitHub update

For routine test-harness changes, use the incremental runner instead of rebuilding the app:

~~~powershell
.\scripts\qa\run-smartprint-qa.ps1 -E2EOnly -PullLatest
~~~

The runner fetches the current checked-out branch from origin. If GitHub has newer commits, it requires a completely clean working tree and uses `git merge --ff-only`; it never stashes, resets, rebases, force-updates, or discards local changes. If the local commit already matches GitHub, it skips the merge and preserves local edits/untracked files. In that no-op case, tracked local QA/docs/test edits are allowed, but tracked application/runtime edits cause a safe refusal before any container operation so the run cannot silently test stale images. Untracked files are not modified. The Photo Studio visual stage also ensures the pinned Portal/Desktop Vite workspace dependencies are present through a frozen-lockfile install; this affects ignored `node_modules`/package-store state only, not tracked files, containers or persistent application data. It compares the files changed by an actual pull:

- Changes limited to scripts/qa/, test folders, docs/, .github/, or Markdown files: it reuses the running API, Portal, PostgreSQL and Agent. **No Docker image build and no container restart.** It also uses the already cached Playwright package/browser rather than installing them again.
- Before Chromium starts, it syntax-checks every QA JavaScript module and runs the existing prerequisite/isolation and HTTP-audit regression tests with Node's built-in test runner. A failure stops before the live journey. After the real-data journey, it also runs the isolated synthetic-fixture visual/Photo Studio matrix on loopback ports 4173 and 1420; those servers are owned by the test process and existing listeners are never stopped.
- Any other changed path is conservatively treated as application/runtime-affecting. Only the existing API and Portal services are built/refreshed; PostgreSQL and its data volume, QA identities and Agent remain untouched.
- If the existing services are missing, stopped, or unhealthy in reuse mode, the runner stops with a diagnostic instead of recreating them.

The first time this runner version is used, pull it once manually so the new switches are available:

~~~powershell
git pull --ff-only origin feature/smartprint-clean-qa-runtime
~~~

Then use the incremental command for later updates. -E2EOnly without -PullLatest retains the previous explicit API/Portal refresh behaviour.

The public landing page uses system font fallbacks so local acceptance testing does not fetch Google Fonts or another third-party asset.

## Existing test coverage retained

### Portal logic regression tests

The runner executes these repository tests with the Node test runner:

- apps/portal/tests/portal-routing.test.ts
- apps/portal/tests/order-journey.test.ts
- apps/portal/tests/customer-session-state.test.ts
- apps/portal/tests/table-layout.test.ts
- apps/portal/tests/theme-responsive.test.ts
- apps/portal/tests/batch-checkout-state.test.ts
- apps/portal/tests/local-upload-routing.test.ts
- apps/portal/tests/print-fulfilment-flow.test.ts
- apps/portal/tests/photo-studio.test.ts
- apps/portal/tests/upi-payment-intent.test.ts

These are isolated logic tests. They are reported separately from the real-data E2E suite and are not presented as live API tests.

### Existing Playwright UI fixture suite

apps/portal/tests/theme-responsive.browser.mjs runs as a separate fixture-based visual acceptance stage in both the full local runner and the incremental `-E2EOnly` runner. It deliberately intercepts API calls and uses synthetic photo fixtures; it is not counted among the 262 real-data acceptance cases or a real-data/shop/physical-print pass. Its evidence is saved under `artifacts/smartprint-qa/<run-id>/photo-studio-browser/`.

### Existing live-production smoke suite

scripts/qa/live-environment.mjs remains available for a separate production-origin smoke check. It is not the database-mutating local acceptance suite. GitHub-hosted headless requests may still receive Cloudflare Bot Fight Mode challenges on the Free zone.

## Real-data Playwright journey

The suite is scripts/qa/smartprint-local-e2e.mjs. It launches the pinned Playwright-managed Chromium executable in headed mode and records its version and executable path. It never selects the system Edge channel. It saves screenshots and a redacted JSON report.

### Account creation and authentication

- Register two new shops through the real shop registration UI, with unique generated business names and example.invalid email identities.
- Sign out and sign back in through the real shop sign-in UI.
- Verify incorrect shop passwords are rejected.
- Create the global customer in local PostgreSQL through the real customer registration API, then verify invalid and successful customer login.
- Bootstrap a separate platform-admin account in the local API container; verify incorrect password rejection and sign in through the platform-admin UI.
- Capture real access/refresh tokens from the browser's authenticated session storage; never create test JWTs.

The customer QR-entry screen does not have a separate customer sign-in page. The suite creates a real customer identity through the supported registration API, authenticates that identity, then attaches it to the real station-session request made by the customer QR page.

### Platform-admin control centre

- Navigate Overview, Shop tenants, Plans & entitlements, Shop subscriptions and Operations & retention.
- Create or reuse SMARTPRINT_QA_AUTOMATION, a zero-priced, cloud-assisted QA-only plan, through the real platform plan screens.
- Publish a free plan version and assign it to each generated QA shop through the real Grant plan access dialog.
- Read the saved subscriptions and append-only assignment audit history back from the local API.
- Verify that a shop cannot access platform endpoints and a platform admin cannot use a shop-scoped endpoint.

### Shop workspace

Each generated shop opens the real sidebar sections:

- Order Journey
- Overview
- Print Queue
- Customers
- Printers
- QR Stations
- Print Agent
- Pricing
- Payments
- Reports

The suite also opens the account-menu-only Personal details, Shop profile & branding, Trial & subscription and Help & support screens.

For each shop it creates a no-dispatch QA printer record and a dedicated QR station through the actual shop UI. The QA printer has no Agent device attached and its queue name is SMARTPRINT_QA_NO_DISPATCH. It configures a QA-only rate card and the UPI/cash display methods using the real shop forms, then confirms all persisted values through the local API.

### Responsive and theme coverage

- Public landing, shop sign-in, shop registration and platform-admin sign-in pages.
- Both QA shop workspaces and the platform-admin workspace.
- Viewports: 1440×1000, 1024×900, 768×1024, 520×900 and 390×844.
- Light and dark theme in each authenticated workspace.
- Root/body horizontal overflow and visible interactive controls clipped beyond the viewport.
- JavaScript exceptions, same-origin console errors, HTTP 5xx responses and failed same-origin JavaScript/CSS/font resources.

The responsive checks navigate real pages using real local data; they do not install mocked API routes.

### Customer QR, documents and batch review

- Open the generated QA station route both as an ordinary browser guest and with the native-account Authorization header; create real server-issued sessions and verify the correct session binding.
- Verify the real session API and shop binding for both entry modes. Guest browser uploads must remain guest-scoped and must not attach to an unrelated global customer identity.
- Confirm that an invalid PDF is rejected with HTTP 422 before creating a print job.
- Upload a synthetic two-page PDF through the actual customer file picker and upload UI.
- Calculate a quote, send it to the shop, verify the customer status/history and shop queue, and confirm the shop can reject an unpaid job.
- Open a second fresh QR session and upload two different synthetic PDFs in one batch through the actual customer UI.
- Verify distinct PrintJob IDs, one Batch ID, both files in the correct shop queue and customer-visible status.
- Open the actual shop batch review modal and preview the first uploaded PDF through the real document-download API.
- Uncheck the second file, re-check it to prove rejection can be undone before applying, then uncheck it again and apply the review.
- Verify that exactly one file is AWAITING_PAYMENT and one file is REJECTED.
- Verify that the rejected file is excluded from the single batch payment total.

### Payment-intent safety and accounting guards

The suite creates one real UPI batch payment intent for the one accepted file. It submits a clearly synthetic reference prefixed QA-NOT-REAL, verifies duplicate-reference idempotency, then rejects the pending reference from the shop and verifies duplicate rejection idempotency.

It deliberately does not:

- transfer money;
- claim the synthetic reference is a real UTR;
- verify or authorise the payment;
- record a refund or say money was returned;
- enable a live payment gateway;
- dispatch the job to an Agent or printer.

The suite also reads payments/refunds, verifies refund preview is blocked without a verified payment, and checks that dispatch is blocked for a rejected/unpaid job.

### Agent and tenant boundaries

- Generate a real one-time Agent enrollment code and redeem it through the actual `/agent/enroll` endpoint to create a disposable local QA Agent identity with a unique QA fingerprint.
- Authenticate the QA protocol client using the real secret returned by enrollment, send a heartbeat, reject malformed printer discovery metadata, and accept an empty printer-discovery sync. No physical printer is advertised or asserted.
- Verify that an enrollment code cannot be replayed, revoke the disposable QA Agent through the shop endpoint, and prove its credential can no longer heartbeat.
- Probe all 17 protected Agent API routes without credentials and verify they return 401. These are live API auth tests, not mocked routes.
- The runner does not install, pair or reconfigure a Windows Agent service, and it does not touch a physical printer.
- Verify that Shop One cannot access or mutate Shop Two's resources and job history.
- Verify that customer/shop/admin tokens cannot cross their respective scopes.

## Expanded functional acceptance catalogue and 100% release gate

The local real-data Playwright runner now carries **262 uniquely identified functional acceptance cases**. Case IDs are grouped under E2E records. At the end of a run, report.json and report.txt distinguish case IDs with a check result; IDs attached to checks that actually ran (PASS, FAIL or WARN); IDs whose owning check completed successfully; IDs associated with failed/incomplete checks; IDs associated with skipped checks; missing, duplicate and unplanned IDs; and reconciliation of all case-status totals. The terminal JSON also includes the name and message for every actual WARN record.

**Coverage definition:** functional case completion coverage = case IDs belonging to records that completed all their assertions successfully ÷ 262. This is deliberately conservative: when a grouped record aborts on a failed assertion, the runner does not count every case ID attached to that record as fully completed because it cannot prove that the remaining assertions ran. Cases exercised means IDs attached to a record that ran; it does not imply every assertion in a failed grouped record completed. Cases fully completed means IDs attached to PASS records only. Incomplete means IDs attached to FAIL/WARN records, and skipped means IDs attached to records blocked by prerequisites. These groups must reconcile exactly to the mapped case count. The release gate requires **100%** completion: every planned case ID must be mapped exactly once and every owning check must PASS. Any FAIL, WARN, SKIPPED, missing, duplicate or unplanned case ID, unclassified status or case-mapping error fails the run. Independent records continue after an unrelated failure; only records whose explicit prerequisites did not PASS are skipped. A skipped dependent record still keeps the overall release gate red.

Physical Windows-printer and Android acceptance omissions are recorded in scopeNotes, not in the warning count. They remain separate acceptance work. An actual WARN result from an executed QA check is recorded with its name and message and is release-blocking; scope notes never suppress a real warning.

This is **not source-code line or branch coverage**. No code-coverage claim is made. Measuring line/branch coverage requires a separate instrumented Coverlet/Node coverage run and generated coverage reports; it must not be inferred from the count of functional cases.

### Failure isolation and prerequisite contracts

Every E2E record is declared either with explicit prerequisite record names or as intentionally independent. The health check is the common root. CI validates that every record is declared, all dependency names exist, and each dependency appears before its dependent record. A new record without a contract fails closed as an individual check; it is never silently treated as independent.

Each record runs through `scripts/qa/local-e2e-record-runner.mjs`. An assertion failure is recorded against that check and does not abort later independent records. Dependent checks are marked SKIPPED with the failed prerequisite IDs/names; other shop, role and security suites continue. WARN is not a pass and also blocks dependants and the final release gate. Cleanup for disposable resources remains inside each test action's `finally` block.

### Case catalogue

| Area | Case IDs | Cases | Coverage intent |
|---|---|---:|---|
| Runtime and environment | ENV-001–004 | 4 | Local target, portal/API health, database readiness, expected local stack boundary |
| Public routes | PUB-001–005 | 5 | Landing, shop sign-in, legacy sign-in, registration and admin sign-in render |
| Responsive public routes | RESP-001–020 | 20 | Four public route surfaces across the five declared viewport sizes |
| Shop registration/sign-in | SHOPAUTH-001–006 | 6 | Two independent real shops, registration/sign-in, wrong password and session continuity |
| Customer identity/auth | CUSTAUTH-001–004 | 4 | Global customer registration, duplicate registration and successful/failed customer authentication |
| Platform-admin authentication | ADMINAUTH-001–002 | 2 | Bootstrap-created administrator sign-in and invalid-password rejection |
| Authentication negative/security lifecycle | AUTH-001–008 | 8 | Invalid/duplicate registrations, cross-role login denial, anonymous route denial, refresh rotation, replay prevention and logout revocation |
| Plans and subscriptions | PLAN-001–006 | 6 | QA free-plan creation, draft save/publish, tenant assignment and persisted subscription state |
| Platform UI and operations | PLATFORM-001–014 | 14 | All control-centre areas, directory/plan/subscription/settings reads, settings validation, audit history and safe no-op save |
| Shop setup and validation | SHOP-001–024 | 24 | Printer/station/rate/payment setup in two tenants; invalid pricing, invalid capabilities, unsupported printer type, foreign-printer binding, insecure logo, required-field, branding and unknown-resource validation; verify rejected writes did not mutate saved state |
| Shop navigation/account tools | SHOPUI-001–027 | 27 | Both shop workspaces, all primary sections, account-menu tools and authenticated responsive navigation |
| Shop reports | REPORT-001–004 | 4 | 7/30/90-day summaries, unsupported range rejection and both tenant report scopes |
| Authorization and isolation | RBAC-001–010 | 10 | Shop/admin/customer role separation, customer/shop/platform cross-role denial, foreign tenant resource access and cross-tenant job-history denial |
| QR station/session lifecycle | QR-001–013 | 13 | Ordinary HTTPS link opens a guest station session; authenticated native-account header binds a session to the customer; session token never enters the URL; renewal/resume, station binding, enable/disable and code rotation are covered |
| Guest-only customer journey | GUEST-001–007 | 7 | Browser link creates a guest session; guest uploads a synthetic PDF without Authorization; quote and job reach the correct shop queue; guest sees rejection; job is not attached to a different signed-in customer's history or session count |
| Customer account linkage | CUST-001–002 | 2 | QR print session tied to global customer identity and customer job-history endpoint |
| Local upload validation | UPLOAD-001–004 | 4 | Invalid digest and unsupported extension fail closed before a local upload ticket can be issued |
| Document upload | DOC-001–006 | 6 | Malformed PDF rejected without job creation, real synthetic PDF upload, two-page parsing, quote preparation and saved job metadata |
| Job lifecycle | JOB-001–011 | 11 | Customer quote/submit, shop queue, customer history/status, unpaid rejection and rejection guards against approval/dispatch |
| Batch workflow | BATCH-001–012 | 12 | Two real documents, shared Batch ID, distinct PrintJob IDs, queue/customer states, preview, accept-by-default, undo rejection and individual final decisions; the shop UI searches the exact persisted job number to load its paged queue row, then targets files by original filename and persisted job ID, not sort order |
| Payment workflow | PAY-001–013 | 13 | Payment verification/intent guards, one accepted-file total, synthetic reference idempotency, shop rejection and customer-visible rejected state; no actual money receipt |
| Refund safety | REFUND-001–004 | 4 | Refund preview without verified funds, sent-confirmation requirement and refund-screen/list guard checks; no false refund record |
| Agent lifecycle and protocol security | AGENT-001–033 | 33 | Agent presence/dispatch guard, one-time enrollment, ephemeral QA identity, authenticated heartbeat, printer-sync validation, one-time-code replay, revocation and no-credential denial on every protected Agent endpoint |
| Printer/station controls | DEVICE-001–002 | 2 | QA printer and station disable/restore lifecycle |
| Tenant lifecycle | TENANT-001–010 | 10 | Invalid status rejected, QA tenant suspension blocks normal work, restore is audited and idempotent duplicate state is safe |
| Security guard | SEC-001 | 1 | Unpaid/rejected work cannot cross into a financial refund action |
| Browser and resource diagnostics | OBS-001–004 | 4 | Same-origin runtime errors, console errors, failed resources and HTTP 5xx monitoring |
| Photo Studio | PHOTO-001–006 | 6 | Real browser image selection, crop/look/background tools, optional form details, photo size/count and local multi-photo A4 PDF generation without a server print job |

The six cases above are local editor/browser regressions only. They do not verify physical photo dimensions or PDF tile coordinates. They also do not certify the Android native WebView; APHO-001–036 require separate physical-device evidence. Run PHOX-001–021, PSHT-001–031 and APHO-001–036 in [ROLE_BASED_CROSS_PLATFORM_QA.md](ROLE_BASED_CROSS_PLATFORM_QA.md) for the server-side photo order, fixed A4 grid coordinates, page-count arithmetic, exact physical geometry, partial sheets, refunds, Android customer Photo Studio touch/file-provider/device-lifecycle parity and measured physical printer output. PHOX-015–021 separately test owner-originated photo upload, existing-customer/walk-in identity, pending payment, idempotency and tenant security. The owner flow is implemented in code but has not yet been acceptance-passed. **P0 release finding:** the API now validates each page's portrait A4 MediaBox and exactly one edge-to-edge 2480×3508 raster, but it still does not inspect the raster's internal photo tiles for count, dimensions or coordinates. PSHT-025 remains open even when client unit tests and page-count checks pass.

### Additional negative and lifecycle assertions

The extra cases are intentionally run against QA-only identities and records:

- **Authentication:** the refresh-token test uses a newly authenticated QA shop session, proves the old refresh token cannot be replayed, and verifies logout revokes the replacement token. This block runs after the longer UI/data journeys to reduce the risk of colliding with the API's authentication rate limit during startup.
- **Shop configuration:** each invalid update is followed by a read-back. Rejected rate/printer/station writes must not change the rate-card version or create unintended rows. A station cannot be attached to another tenant's printer.
- **QR/session:** tests prove the opaque session token cannot be read anonymously or resumed against another tenant's QR station. A disabled or rotated station code cannot create a new session; the new rotated code must work.
- **Reports:** Shop One is checked against each supported 7-, 30- and 90-day summary window, an unsupported 14-day window is rejected, and Shop Two reads its own report under its own token.
- **Agent protocol:** a QA identity is enrolled with a one-time code and later revoked. The heartbeat and printer-discovery routes are called against the real local API, and all protected Agent routes reject missing credentials. No local Windows Agent executable or physical printer is represented as paired.
- **Photo Studio:** a synthetic PNG is processed in a separate browser page. The test opens the actual editor, exercises crop/look/background and optional form controls, generates the prepared A4 photo PDF, verifies the preview, and compares the exact customer job-ID set before and after to prove no server-side print job was created or replaced. The prepared file is not uploaded or sent to the shop.
- **Quote acceptance:** after the customer selects Send batch to shop, the browser test waits for the exact POST /api/v1/public/jobs/{jobId}/accept-quote response for every file, validates its result, and reads back the persisted job status before proceeding. A visible status panel alone is not treated as proof of completion.
- **Payment/refund:** test-only payment references are explicitly marked synthetic. A rejected payment cannot be manually verified, an unpaid/rejected job cannot be approved/dispatched, refund preview without verified money is rejected, and the API refuses a refund request whose `confirmedSent` value is false. No real transfer, payment verification, refund, gateway or printer dispatch occurs.
- **Platform/tenant:** only the generated QA shop is suspended; it is restored to Active in the same run. Operational settings are saved with their existing values only to prove the audit trail, so platform policy values are not changed. A duplicate Active transition must be idempotent.

- **Extended authorization and tenant isolation:** positive and negative access checks cover customer-to-shop, shop-to-customer, shop/customer-to-platform denial, plus printer/station/Agent list isolation and foreign-tenant printer/station mutations. Every rejected mutation is followed by a read-back to prove the other tenant's record remains unchanged.
- **Additional request validation:** the suite rejects invalid public station codes, invalid opaque session tokens, missing idempotency keys, malformed upload batch IDs, non-multipart document posts, unknown customer job IDs, unsupported platform directory bounds, invalid shop branding/device fields and malformed/overlong UPI references.
- **Agent authentication:** a real disposable Agent is probed with an incorrect secret, malformed Agent ID, unknown enrollment code and incomplete enrollment metadata before normal heartbeat/sync and revocation tests. These probes do not create another Agent or printer.

### Coverage boundaries that remain separate

The 262-case gate measures the documented local functional catalogue. It does not replace these additional suites or acceptance requirements:

- The portal Node regression suite and the API/Agent .NET unit suites remain separate test results. They provide domain/logic regression coverage beyond the mapped browser/API acceptance cases.
- The Windows Agent executable's full live machine lifecycle—actual service enrollment, local queue claim/receipt, spooler accepted/failed/unconfirmed states, PDF export and output confirmation—is not exercised by the protocol test client. The E2E does exercise the server's real one-time enrollment, heartbeat, printer-sync validation, revocation and auth protections with a disposable QA identity. The Agent unit suite and optional Microsoft Print to PDF integration remain separate.
- The Android emulator test suite is conditional on an installed SDK and an already running emulator. Physical Android camera, permission, external-link opening, process-death recovery, local-network and Wi-Fi readiness tests remain in docs/ANDROID_PHYSICAL_ACCEPTANCE.md.
- Real LAN direct upload, Windows service identity, a physical printer, printed paper, power/network interruption recovery, concurrency/load, backup/restore rehearsal and payment-provider settlement require separate dedicated acceptance. They are not counted as passed by this 262-case browser suite.
- No destructive tenant deactivation, data deletion, storage cleanup, real payment, actual refund, permanent printer pairing or physical print is performed by this runner.

## Android and Windows execution

### Android

When system Gradle and Android SDK are installed, the orchestrator:

- runs :app:testDebugUnitTest;
- builds :app:assembleDebug;
- uses http://10.0.2.2:3000 so the Android app targets the local portal from the Android emulator;
- runs :app:connectedDebugAndroidTest only if an Android emulator is already online.

Physical Android devices are not targeted automatically. Camera permission flows, real camera scanning, the phone's document picker, Android Wi-Fi permissions and actual phone-to-shop LAN upload still require device acceptance.

### Website-published Android APK only

To test only the APK published on the SmartPrint website, without rerunning the local portal/browser/API E2E suite, Windows tests, Docker builds or PostgreSQL checks:

~~~powershell
adb devices
.\scripts\qa\run-smartprint-qa.ps1 -AndroidAppOnly -AndroidDeviceSerial "YOUR_ADB_SERIAL"
~~~

This standalone mode checks the configured HTTPS release metadata, downloads and verifies the published APK, validates its application ID, versionCode, versionName and launcher activity, then compares the installed package version and Android's resolved MAIN/LAUNCHER component before attempting launch on the exact ADB serial supplied with `-AndroidDeviceSerial`. Supplying the serial is the explicit opt-in to target that physical phone; no other connected device is selected. When no serial is supplied, the runner remains emulator-only. It installs the APK only when the app is absent; it never replaces an existing installation. If the installed version differs from the published version, the default run records a preflight failure and leaves the phone's app and data untouched. To explicitly authorise an in-place update of the selected device, add `-InstallPublishedAndroidApk`; this uses `adb install -r`, then re-reads the installed package version and launcher before launch. Android enforces signing-certificate compatibility. If the update is rejected, the runner records the device response and never uninstalls the old app or clears app data. The opt-in switch is valid only with `-AndroidAppOnly`. The live app check starts the exact Package Manager-resolved launcher component through Android ActivityManager for the active Android user, verifies the resumed foreground process and structured `Status: ok` result, and inspects the native app root plus Home and My Files controls. It prefers stable resource IDs when the installed APK contains them. For the already-published APKs that predate the new navigation/heading IDs, it uses the existing exact navigation content descriptions. The two distinct, visible native navigation controls also provide a compatibility proof for the native shell when the legacy APK has no `smartprint_native_root` ID. It then verifies **My Files** using the accessible **Create a new folder** action description and **Home** with the **Your space** heading. The resource-ID names are centralized under `apps.android.uiAutomationIds` and contract-tested against Android `ids.xml` and instrumentation tests. The compact phone navigation and screen headings expose these IDs in new Android builds; the Material landscape rail keeps its internal view IDs unchanged and uses its existing accessibility descriptions. If app launch fails, downstream UI checks are marked **SKIPPED** with that launch failure as their prerequisite; the same root error is not repeated as multiple failures. It does not create/delete user files, change settings, clear app data, access credentials, open a shop session or run website UI E2E. The public website is used only as the APK/release download source. Windows installer checks are not performed in this mode. If the selected serial is absent/offline or any required live app check does not pass, the run fails. Results are saved under `artifacts/smartprint-qa/<run-id>/android-published-app/`.

### Windows virtual printer

If Microsoft Print to PDF is installed, the Agent test run enables only SMARTPRINT_RUN_WINDOWS_SPOOLER_INTEGRATION=1. This exercises the real Windows GDI → virtual spooler → generated PDF output path. The secondary queue integration flag remains off. This is not a physical printer acceptance test.

A physical Windows Agent paired to the QA shop, actual printer dispatch, printed paper output and the owner's Android-to-shop-Wi-Fi flow still require a dedicated test machine and printer. The script does not reconfigure a real Agent service, change its identity, install/uninstall the desktop app, or print to an ordinary shop printer.

## Safety and repeatability

- All account names, documents, plans, stations, payment references and jobs are explicitly labelled SmartPrint QA.
- All portal, API and database operations from the E2E suite are local-only.
- No existing shop, customer, printer, Agent identity or existing station is intentionally modified.
- A QA plan is zero-priced. No paid plan assignment is made.
- QA payment settings use a non-live placeholder UPI identity and cash method. No payment is verified.
- The two-file batch creates real local records in the local QA tenant. The pending synthetic UPI reference is rejected before payment verification. No refund is recorded.
- The generated QA account password file is DPAPI-protected for the current Windows user and lives in .secrets/, which is gitignored.
- Do not upload the .secrets directory, .env.local, raw tokens, screenshots containing private content or local database files to GitHub.
- Do not aim this runner at the public production URL. Its local E2E script hard-fails unless the origin is loopback on port 3000.

## Evidence and result interpretation

Each run creates:

~~~text
artifacts/smartprint-qa/<run-id>/
  summary.json
  summary.txt
  logs/
  local-e2e/
    report.json
    report.txt
    screenshots/
~~~

A test is PASS only when the real local UI/API operation and its persisted result meet the assertion. Missing local SDKs/devices are SKIPPED; hardware-only acceptance is reported as not performed. A fully green local web/API run does not certify physical Android or printer output. Production release sign-off still requires all applicable items in docs/ANDROID_PHYSICAL_ACCEPTANCE.md and the security/operations checklist.
