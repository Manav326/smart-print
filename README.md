# SmartPrint by QuantaEdge

SmartPrint by QuantaEdge (repository: smart-print) is a multi-tenant SaaS platform for QR-initiated document printing, combining customer web experiences, a native Windows shop-management desktop application, a separate local Windows Print Agent service, and platform-owner administration.

## Product principles

- Support independent printing shops as the primary SaaS customer.
- Support both local shop-Wi-Fi printing and secure cloud-assisted printing.
- Integrate with multiple printer manufacturers through capability discovery and replaceable adapters.
- Keep print execution local to the shop; the cloud coordinates identity, tenancy, subscriptions, secure job routing, and optional cloud transfer.
- Keep each shop's customers, documents, jobs, payment records, devices, users, and branding isolated.
- Route customer print payments to the shop's own payment account. Route Smart Print SaaS subscription payments to the platform owner. These are separate financial flows.
- Deliver a premium, coherent, accessible UI across customer PWA, shop desktop/web, platform administration, and the Windows agent.

## Deployment origin

The GitHub repository and container images use the `smart-print` identity. Local and production Docker image names are isolated as `localhost/smart-print/smart-print-api:local`, `localhost/smart-print/smart-print-portal:local`, `ghcr.io/manav326/smart-print-api`, and `ghcr.io/manav326/smart-print-portal`. Existing Compose project identifiers remain temporarily unchanged to preserve persistent database/document volumes during the rename; do not rename or delete those volumes as part of this transition. The application has one public origin, configured per deployment in PUBLIC_BASE_URL. The customer portal, shop portal, platform administration, API and station QR links all use that origin; do not add separate API or admin hostnames for this application. Set IMAGE_NAMESPACE in .env to `ghcr.io/manav326`; production images are published as `smart-print-api` and `smart-print-portal`. Set GITHUB_REPOSITORY to `Manav326/smart-print` for release downloads.

## Repository status

The repository contains a customer/shop portal, native Android companion, tenant-scoped ASP.NET Core API, PostgreSQL schema and migrations, paired Windows Print Agent service, encrypted local queue, Windows spooler execution, automated tests and CI. Customer requests support PDF/JPG/JPEG/PNG files (20 MB per file; PDFs up to 500 pages) and batches of up to 20 files / 50 MB total. Each file is a separate PrintJob in a shared batch; the shop can review and accept/reject files individually and use supported pre-handoff page/file refund workflows. Customers configure copies, mono/colour, duplex, supported paper size and orientation, receive server-calculated quotes, pay by shop-enabled UPI/cash and track output. Shops control QR stations, printers, rate cards, payment verification, job approval, dispatch, customer history, reports and output confirmation. Customer document modes are Standard Printing (default 7-day completed-source retention) and optional Private Printing (end-to-end encrypted to the paired Agent, no normal cloud source storage or managed PDF export, source erased after confirmed physical output). Standard Shop Wi-Fi transfer is local HTTP and is not encrypted in transit; Cloud-assisted HTTPS is plan-gated. The service remains required for session, quote, payment and dispatch coordination; this is not fully offline printing. The Android app adds QR scanning, a local file/folder library, document scanning and PDF/photo/ID preparation tools. Shop customer print payments remain manually verified UPI/cash; separate INR SaaS subscription checkout uses Razorpay with server-side signature/webhook verification. See docs/SEO_PRODUCT_CAPABILITY_AUDIT.md for the code-verified feature inventory and docs/ROADMAP.md for release/validation boundaries.

## Native Android customer companion

A native Android client is located in `apps/android`. It complements the customer web experience and uses the same shop QR session, quote, payment and print-job lifecycle.

- Scan shop QR codes with CameraX and on-device ML Kit. QR decoding stays on the device; camera frames are not uploaded for barcode decoding.
- Keep documents in an app-private My Files library with folders/subfolders. Import multiple files or folders through Android's Storage Access Framework; rename, move, delete, open, share and export files/folders.
- Use the built-in document scanner where Google Play Services supports it. Scans are prepared locally before the customer chooses to submit them.
- Edit PDF copies with page preview, rotation, reordering and page deletion; the source is not overwritten. The editor may rasterise pages and is not a lossless vector/text editor.
- Use Photo Studio for print-sized photo sheets and ID Studio for local front/back card capture and an A4 physical-size sheet.
- ID Studio keeps prepared sheets in a separate Android Keystore-encrypted app-local vault. Prepared ID sheets always require Private Printing when sent to a shop.
- Customer uploads remain subject to the API limit of 20 MB per file, PDF maximum 500 pages, and batch limit of 20 files / 50 MB total.
- The embedded customer portal uses the same station-bound session and upload APIs as the web flow. It does not introduce a second print-job lifecycle or claim offline printing.
- The local Agent upload exception permits only RFC1918 IPv4 addresses on TCP 17842 and the Agent upload route over cleartext HTTP; this standard local transfer is not encrypted in transit. HTTPS remains required for the shop portal and Cloud-assisted uploads.
- General library imports are capped at 100 MB per file. Android file/folder pickers control import and export; the app does not request broad storage access.

CI builds the Android debug APK and runs JVM unit tests. A real-device matrix (QR scanning, file-provider interoperability, Android-version coverage, document scanning and shop-Wi-Fi Agent upload) is still required before making production/Play Store availability claims. The SmartPrint Android debug APK is published as the `smart-print-android-debug` workflow artifact.

## Public SmartPrint downloads

The public landing page and shop workspace use same-origin download routes. Customer browsers request files from the SmartPrint website itself; the API resolves and streams release assets server-side. The browser is never redirected to GitHub, a release page or an asset CDN.

- Windows shop installer: `/downloads/windows`, available only when the latest stable release contains the exact Windows installer.
- Android customer APK: `/downloads/android`, available only when the latest stable release contains the verified production-signed APK.
- Availability is checked through `/downloads/status`. Missing releases/assets are shown as unavailable; temporary upstream failures show a retryable status.
- The streaming proxy follows GitHub's signed asset redirects on the server, never forwards upstream `Location` headers, streams without buffering the complete file in memory, and supports single byte-range requests for resumable downloads.

The API obtains the repository owner/name from `GITHUB_REPOSITORY` in the production `.env`. The deployment helper derives it from the checked-out Git origin and refuses a mismatch. No deployment hostname or repository name is baked into the download implementation. GitHub is only the upstream release storage provider.

The Windows installer is intentionally unsigned for the interim beta channel. The website marks it available only when the latest stable release actually contains the installer, and shows a note that Windows may display a first-run security prompt. Some managed Windows 11 devices may block unsigned installers. Do not advise customers to disable Windows security protections. The Android card stays unavailable until the production signing keystore is configured and the signed APK is published.

Every stable release is retained indefinitely as its own versioned GitHub Release; publishing a newer version updates the latest pointer but does not delete or replace older versions. The release workflow always verifies and publishes the Windows installer. When all four Android signing secrets are present, it also builds and verifies the signed Android APK and publishes it in the same release. If none of the Android secrets are configured, Android is skipped and its website card remains unavailable; a partially configured signing setup fails the release.

Create a version tag (for example, `v0.1.0`) only after the intended changes are merged to `main`, main-branch CI passes, and `SMARTPRINT_PUBLIC_BASE_URL` matches the deployment. Configure all four Android signing secrets before expecting the signed APK to appear. The `.github/workflows/release.yml` workflow always builds and publishes the guided Windows installer; it also builds, verifies and publishes the Android APK when signing is configured.

Required GitHub Actions repository secrets for Android (not required for the Windows beta):

| Secret | Purpose |
| --- | --- |
| `ANDROID_SIGNING_KEYSTORE_BASE64` | Base64-encoded Android release keystore |
| `ANDROID_SIGNING_KEYSTORE_PASSWORD` | Android keystore password |
| `ANDROID_SIGNING_KEY_ALIAS` | Android release key alias |
| `ANDROID_SIGNING_KEY_PASSWORD` | Android release key password |

Keep the Android keystore and its passwords backed up securely. Every Android update must use the same signing identity or existing installations will not accept it as an in-place update.

The future Microsoft Store channel will be a separate MSIX package, not the current EXE installer. Microsoft Store submissions of accepted MSIX packages are re-signed by Microsoft. Before Store submission, the current Windows Service and firewall installation architecture must be validated against MSIX packaging and restricted capabilities.

The release and CI workflows require the repository Actions variable `SMARTPRINT_PUBLIC_BASE_URL`. No production host fallback is embedded in the Gradle build or installer scripts.

The guided **SmartPrint Desktop** installer does not ask the shop owner for a pairing code, API URL, or PowerShell command. After sign-in, the desktop requests a short-lived, single-use enrollment authorization from the authenticated shop API and sends it directly to the local Windows Agent over its ACL-protected, remote-client-rejecting named pipe. The Agent stores its device secret with DPAPI LocalMachine protection. For an existing pairing, startup checks the saved identity; an owner-revoked computer is not silently re-enrolled and requires one explicit **Reconnect this computer** action after the server confirms revocation. A computer paired to another active shop cannot be transferred until that shop revokes it. The separate **Agent-only/manual deployment** workflow below still uses a one-time code from **Shop workspace → Print Agent → Generate pairing code**; that code expires after 10 minutes. The guided installer stages Agent files in a version/build-specific folder, switches the Windows service only after the new files are present, and restores the previous executable path and running state if the new service cannot be started. Superseded Agent files are removed only after the replacement service and firewall rule are confirmed healthy.

## Download and install the Android debug APK

On Windows with GitHub CLI authenticated and Android Debug Bridge (ADB) installed:

```powershell
.\scripts\download-android-apk.ps1
.\scripts\download-android-apk.ps1 -Install
.\scripts\download-android-apk.ps1 -Install -DeviceSerial "YOUR_DEVICE_SERIAL"
```

The script selects the latest successful main-branch CI run, downloads the `smart-print-android-debug` artifact, validates the APK and reports its SHA-256. It saves the file as `%USERPROFILE%\Downloads\SmartPrintAndroid\SmartPrint-Android-debug.apk` and also accepts retained APK artifacts from older CI runs created before the naming change. Use `-Install` only after enabling USB debugging and authorising the device. This is a development debug build, not a Play Store release.

The same CI run publishes `smart-print-android-test-reports` with JVM and emulator test evidence.

## Run locally through Cloudflare Tunnel

This is the recommended local end-to-end test path. The phone opens the same publicly trusted HTTPS URL as production; no private CA certificate is installed on customer devices.

1. Use the existing QuantaEdge Cloudflare Tunnel managed by the `QuantaEdge-Production` control plane. Do not create a product-specific connector or a second tunnel.
2. In that tunnel, configure exactly one SmartPrint Published Application origin: **http://smart-print-portal:80** on the shared Docker network `cloudflare-edge`. Do not add a second origin or legacy alias.
3. Start the central connector once from the QuantaEdge-Production checkout with `.\scripts\local-ingress.ps1 up`. Then, from this repository root, run:

    .\scripts\docker-up-local.ps1

   The helper reuses .env.local and the existing database/document volumes. It no longer collects or manages the Tunnel token; Tunnel credentials belong only to the central control plane.
4. The only SmartPrint application URL is PUBLIC_BASE_URL in .env.local. The helper asks for it when no value is configured; it has no domain fallback. The portal and API share this hostname; Nginx routes /api and /health internally to the API.

The phone opens that Cloudflare HTTPS URL. When the customer taps Check shop Wi-Fi, Chrome may request Local Network Access permission for the shop computer. No certificate installation is required. Cloud-assisted upload remains available when local access is unavailable.

### Standalone Agent-only pairing (manual deployment)

Use this only when deploying the Agent without the guided SmartPrint Desktop installer. Pair it against the same public URL and configure its browser origin with the same value:

    $agentDir = Join-Path $env:USERPROFILE "Downloads\SmartPrintAgent"
    $agentExe = Join-Path $agentDir "SmartPrint.Agent.exe"
    $publicBaseUrl = (Get-Content .env.local | Where-Object { $_ -like "PUBLIC_BASE_URL=*" } | Select-Object -First 1).Substring("PUBLIC_BASE_URL=".Length)
    & "$agentDir\pair-agent.ps1" -ApiBaseUrl $publicBaseUrl -AgentExePath $agentExe
    & "$agentDir\install-agent-service.ps1" -AgentExePath $agentExe -CustomerPortalOrigin $publicBaseUrl

Use the current PUBLIC_BASE_URL in those commands. After its first authenticated heartbeat, the Agent receives the canonical origin from the API and updates its saved API destination and allowed browser origin without changing its DPAPI-protected pairing identity.

### Changing the public URL later

Change PUBLIC_BASE_URL in .env.local (or .env for production), update the single SmartPrint Published application route in the central Cloudflare Tunnel to http://smart-print-portal:80, and restart the Compose stack. Cloudflare's route is infrastructure configuration and must match the chosen hostname. Keep the previous hostname routed to the same portal during migration: previously printed QR codes keep working, and already-paired Agents can heartbeat through the old hostname and learn the new URL. Remove the old route only after Agents have updated and old QR codes are no longer in circulation.

### Stop / restart

Stop containers without deleting data:

    docker compose --project-name qrtoprint-local --env-file .env.local -f compose.local.yml down

Restart with .\scripts\docker-up-local.ps1. Never use down -v; that deletes persistent application data.

## Docker deployment

The production stack runs PostgreSQL, the ASP.NET Core API and the React portal. Cloudflare Tunnel is a shared QuantaEdge infrastructure service managed by `QuantaEdge-Production`; this product stack does not run its own connector. The host does not need inbound ports 80/443, a public IP, a Caddy certificate, or a separate API hostname.

### First start

1. Ensure the central connector is running from QuantaEdge-Production and the shared `cloudflare-edge` network exists. The Cloudflare Published Application route for this hostname must target **http://smart-print-portal:80**.
2. From the repository directory, run:

    .\scripts\docker-up.ps1

   The helper creates .env with fresh database, signing and document-encryption secrets. It does not collect or store Tunnel credentials. It pulls the published GHCR images and preserves named data volumes.
3. Open the URL printed by the script. The same PUBLIC_BASE_URL serves the portal, API and newly generated station QR links.

### One place to change the URL

PUBLIC_BASE_URL in .env is the sole public application origin. It must be an HTTPS origin with no path. The Cloudflare Published application route for that hostname must point to http://smart-print-portal:80. Browser API calls are same-origin through /api; no API_DOMAIN is required.

### First administrator

After the first deployment is healthy, bootstrap the separate platform administrator:

    .\scripts\docker-bootstrap-platform-admin.ps1 -Email "admin@example.com" -DisplayName "Platform Administrator"

The script prompts securely for a password and creates or repairs only a tenantless platform-admin identity. It refuses to elevate an existing shop user.

### Windows printer computer

Install the Windows Agent on the computer physically connected to the printer. Pair it with PUBLIC_BASE_URL and install the Windows Service using the exact same customer origin. The Agent learns later URL changes from the authenticated heartbeat and updates its API destination and local CORS allowlist without re-pairing.

During a hostname migration, keep every previously configured public or legacy Agent API hostname routed to the portal until Agents have updated and old QR codes are no longer in circulation. Legacy Agent heartbeats must continue to reach the API long enough to receive the new canonical URL.

Direct LAN uploads still require the phone and Agent to share the shop LAN, the browser's Local Network Access permission when requested, and the Agent's Private-profile firewall rule on TCP 17842. Cloud-assisted upload remains the fallback.

### Backups and operations

Before upgrades or migrations, run .\scripts\docker-backup.ps1. Database backups, encrypted document files and their encryption key must be kept together. Restore using .\scripts\docker-restore.ps1 -BackupDirectory .\backups -ConfirmRestore.

Inspect the deployment with docker compose --env-file .env -f compose.yml ps and docker compose --env-file .env -f compose.yml logs -f api. The stop script preserves named volumes. Never use docker compose down -v unless you deliberately intend to delete application data.

## Documentation

- [Product blueprint](docs/PRODUCT_BLUEPRINT.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Premium design system](docs/DESIGN_SYSTEM.md)
- [Security and operations](docs/SECURITY_AND_OPERATIONS.md)
- [Delivery roadmap and acceptance criteria](docs/ROADMAP.md)
- [Public website and download launch checklist](docs/PUBLIC_LAUNCH_CHECKLIST.md)
- [Release signing setup](docs/RELEASE_SIGNING_SETUP.md)
- [Local native Windows and Android QA](docs/LOCAL_NATIVE_APP_QA.md)
- [Role-based Web, Android and Windows acceptance matrix](docs/ROLE_BASED_CROSS_PLATFORM_QA.md)
- [Production QA master test plan](docs/PRODUCTION_QA_TEST_PLAN.md)
- [Final pre-production release gate and blocker register](docs/FINAL_PRE_PRODUCTION_RELEASE_GATE.md)

## Delivery principles

- Audit the current state before changing existing functionality.
- Keep the default branch buildable; use focused, reviewable changes.
- Never claim a physical print completed based only on spooler acceptance.
- Never automatically resubmit an uncertain physical print after restart.
- Never trust client-supplied prices or payment-success screenshots.
- Do not claim hardware compatibility until representative physical devices have been tested.


Platform administrators can manage versioned SaaS plans, entitlements and shop subscriptions. The shop subscription page supports INR Razorpay checkout with server-side checkout-signature verification and signed, idempotent webhook processing. Platform operators can also grant trial/free access or record an assisted paid assignment with the received bank/UPI reference in the append-only audit. This subscription flow is separate from customer print payments, which remain shop-configured UPI/cash and manually verified. Do not imply customer print payment gateway processing or automatic UPI/cash refunds.


After the GitHub repository is renamed, update an existing local checkout with:

```powershell
git remote set-url origin https://github.com/Manav326/smart-print.git
```
