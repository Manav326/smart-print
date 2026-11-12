# SmartPrint Public Website & Distribution Launch Checklist

Last updated: 2026-11-01

This checklist tracks the public website, non-technical Windows setup, Android release and public software distribution. A checked source-code item means the implementation is present in the repository; it does not imply a successful CI run, deployment or physical-device acceptance.

## A. Public website and shop entry

- [x] Replace the unauthenticated root page with the SmartPrint public marketing landing page.
- [x] Add the custom storefront-plus-printer **For print shops** entry in the top-right header.
- [x] Route shop entry to the existing sign-in flow at `/shop`.
- [x] Allow direct shop registration at `/shop?mode=register`.
- [x] Add a return-to-website link to the existing authentication screen.
- [x] Explain the customer QR journey, shop review/approval, Agent and printer connection.
- [x] Add separate Windows and Android download cards; Windows shows a compact Available label and Android remains Coming soon until its production APK is ready.
- [x] Add public metadata, social preview metadata and a SmartPrint favicon.
- [x] Preserve the existing `/s/{publicCode}` customer QR route ahead of public landing-page routing.
- [x] Run portal type-check/build on the current main commit and resolve any failures (CI run 344 passed).
- [x] Keep the production website at `https://smartprint.quantaedge.in/` publicly available 24 hours a day, 7 days a week; SEO changes must not intentionally take it offline or place it into maintenance.
- [ ] Verify the live production homepage, SEO pages, `robots.txt` and `sitemap.xml` on desktop and mobile as ongoing checks. A failed external fetch is an unverified check, not evidence of downtime.
- [ ] Roll out portal changes with health checks and rollback, keeping the currently healthy version serving traffic until the replacement is ready.
- [ ] Complete keyboard, contrast, screen-reader and responsive browser checks.
- [ ] Publish and link the actual Terms of Service and Privacy Policy pages.
- [x] Keep Android download disabled until a production-signed APK is published; do not link the CI debug APK.
- [x] Keep the unsigned Windows beta download notice concise and truthful, including first-run Windows security prompts and the possibility of managed-device blocks.
- [ ] Publish the first unsigned Windows beta release and verify its latest-download URL anonymously over HTTPS before deploying the landing page with the active download link.

## B. Windows Agent installer for shop owners

- [x] Keep the existing self-contained Windows x64 Agent and DPAPI-protected pairing identity.
- [x] Allow the existing Agent pairing command to receive a one-time enrollment code non-interactively through configuration.
- [x] Add an Inno Setup wizard that asks for the shop's one-time pairing code and uses the configured public HTTPS origin.
- [x] Package the Agent with the existing service-install and service-uninstall scripts.
- [x] Reuse the existing service installer to register the Windows Service and Private-profile firewall rule without showing a terminal.
- [x] Add a bounded local readiness check against `/api/v1/local/status` before setup reports success.
- [x] Allow repairs/upgrades to reuse a saved pairing only after an authenticated cloud heartbeat succeeds.
- [x] Exercise the non-interactive `--check-pairing` command on a clean Windows runner and assert its expected unpaired exit code in CI run 351.
- [x] Stop a previously running Agent service before replacing files and attempt to restore it if setup exits before service reinstallation succeeds.
- [x] Add CI steps intended to compile and retain an unsigned installer validation artifact.
- [x] Add a tag-based release step that explicitly verifies and publishes the Windows beta installer as unsigned, with a SHA-256 checksum.
- [x] Verify the Inno Setup compilation job succeeds on GitHub Actions; the installer compiled and its validation artifact uploaded in CI run 344.
- [ ] Verify the resulting installer contains only customer-relevant files and installs cleanly on a fresh Windows 10/11 x64 machine.
- [ ] Test a normal shop-owner Windows account with UAC elevation, no preinstalled .NET runtime and no terminal usage.
- [ ] Test expired, malformed, already-used and wrong-shop pairing codes, including safe recovery after a failed pairing.
- [ ] Verify service startup after restart, local status, cloud heartbeat and printer discovery in the shop workspace.
- [ ] Test a real printer, Microsoft Print to PDF, Direct LAN upload and cloud-assisted fallback after installation.
- [x] Defer Windows code-signing credentials for the interim beta channel; no Windows signing secrets are required.
- [ ] Decide whether a tray companion and silent auto-update are needed for the first public release.

## C. Android customer app

- [x] Keep the native Android customer companion and existing QR/customer workflow.
- [x] Add configurable release version, canonical service URL and release signing to Gradle.
- [x] Add a release workflow that builds a release APK and verifies its signature with `apksigner`.
- [x] Pass Android JVM unit tests, debug APK build and emulator UI tests in CI run 351 (API 35 emulator).
- [ ] Generate the production Android keystore and configure the four Android signing secrets in GitHub Actions; the release workflow skips Android cleanly until all four are configured.
- [ ] Back up the keystore and credentials securely outside GitHub; do not rotate this signing identity for ordinary updates.
- [ ] Install the release APK on physical Android 8+ devices and verify QR scanning, document import, account flows and print submission.
- [ ] Validate Local Network Access and shop-Wi-Fi upload with a real paired Agent.
- [ ] Publish the first signed APK as a public Release asset.
- [ ] Decide on Play Store listing and update distribution after direct APK acceptance.

## D. Versioned release channel

- [x] Add a tag-triggered release workflow for version tags matching `v*`.
- [x] Publish the stable Windows asset name `SmartPrint-Windows-Setup.exe`; add `SmartPrint-Android.apk` only when its production signing credentials are configured.
- [x] Generate a `SHA256SUMS.txt` manifest for exactly the binaries included in each version.
- [x] Document stable GitHub Release asset names; keep Android disabled until its APK is published and deploy the Windows Available link only after the first Windows release exists.
- [x] Add a version/release-notes link from each download card.
- [x] Use `https://smartprint.quantaedge.in` as the canonical production origin for release builds through the `SMARTPRINT_PUBLIC_BASE_URL` repository variable; validate the configured origin before building.
- [x] Validate the configured release origin as a canonical HTTPS origin before building either installer.
- [x] Limit GitHub Release write permission to the publication job and fail if any expected release asset is missing.
- [x] Retain every published stable version; new versions move the latest-release pointer without deleting older releases.
- [x] Create releases as drafts, verify the enabled packages and SHA-256 manifest before publication, and allow retry only while a release remains a draft.
- [x] Refuse reuse of a published version tag and disable asset overwrites during upload.
- [ ] Enable repository-side immutable-release protection and protect the `v*` tag pattern against updates/deletion.
- [ ] Configure the four Android signing secrets when the production keystore is ready; Windows beta publication requires no signing secrets.
- [ ] Set `SMARTPRINT_PUBLIC_BASE_URL` to the actual canonical deployment origin before tagging.
- [ ] Run the first tagged Windows beta release and verify the Windows installer and checksum; verify Android is omitted until credentials are configured.
- [ ] Test download links from a signed-out browser and a mobile device.
- [x] Establish a stable semantic-version policy and retain all published stable releases indefinitely; use tag-specific URLs for rollback.

## E. Deployment and production acceptance

- [x] Confirm the main-branch CI run passes portal, API, Agent, Windows installer and Android debug validation (CI run 351, commit `f42a112`).
- [x] Build/publish the API and portal container images to GHCR and pass the migration/API-readiness/portal-response smoke test (CI run 351).
- [ ] Deploy the current main-branch portal/API images to the intended staging environment.
- [ ] Verify existing shop login, new shop registration and platform-admin routing after deployment.
- [ ] Verify existing printed QR station URLs and active shop sessions still work.
- [ ] Run end-to-end customer upload, quote, shop review, manual payment verification, dispatch and output confirmation.
- [x] Do not present the unsigned Windows beta as publisher-signed or as a production-trusted build; keep the security prompt notice visible.
- [ ] Complete the future MSIX packaging and Windows Store capability review, including the existing Windows Service requirements.
- [ ] Complete a production go/no-go review with Windows, Android, printer, security and download-link evidence.

## Current verified CI evidence

- Latest verified CI run before the release-safety changes is run 360 at commit `c25d97e6c116c812f861c85343d319f1e662e6e4`; all four jobs passed: portal, .NET API/Agent and Windows installer validation, Android unit/emulator tests, and GHCR container publication/smoke tests.
- CI run 360: [GitHub Actions run](https://github.com/Manav326/QRtoPrint/actions/runs/41263086214).
- Release-safety changes passed CI run 377 before the unsigned Windows beta policy was selected. The current unsigned-release workflow and website changes are newer and require fresh CI validation.
- Earlier CI run 351 also passed the non-interactive Agent pairing-status smoke test and the first full installer validation.
- Earlier CI run 344 also passed the first full installer and container validation.
- GitHub Releases currently has no published release. The next tagged release is configured to publish the unsigned Windows beta installer with a SHA-256 checksum; Android is skipped until all four production signing secrets are configured. The live download URL must be checked after that first release exists.

## Guardrails

- Keep the existing customer QR route and shop/API contracts intact.
- Do not ask shop owners to open PowerShell, Command Prompt or a terminal for normal installation.
- Do not publish a debug APK as the public customer app.
- Do not describe the unsigned Windows beta as signed or trusted by a verified publisher; disclose the possible Windows security prompt without asking customers to disable security protections.
- Keep the Android release keystore stable and backed up.
- Treat an Agent local readiness response as local-service verification only; the shop workspace must independently confirm the cloud heartbeat and printer inventory.
