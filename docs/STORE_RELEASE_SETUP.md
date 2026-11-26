# SmartPrint Store publishing setup

This repository uses one versioned GitHub release to produce the website downloads and Store-ready binaries. Store submission is a separate, manually dispatched workflow with explicit confirmation and protected GitHub Environments.

## Release flow

1. Merge tested changes to `main`.
2. The existing main-CI release automation creates a stable `vX.Y.Z` tag and runs `.github/workflows/release.yml`.
3. The release workflow builds:
   - `SmartPrint-Windows-Setup.exe`: intentionally unsigned website/direct-download installer, retaining the existing WebView2 bootstrapper behaviour.
   - `SmartPrint-Windows-Store-Setup.exe`: signed, offline x64 Store installer, only when the Windows signing certificate, WebView2 standalone URL and exact Partner Center publisher name are configured.
   - `SmartPrint-Android.apk`: website/direct-download Android package.
   - `SmartPrint-Android.aab`: signed Google Play App Bundle.
   - `BUILD-INFO.txt`: exact source commit, release tag and workflow run provenance.
   - `attestation.sigstore.json`: signed SLSA provenance bundle for the release binaries and build metadata.
   - `SHA256SUMS.txt`: checksums for every release binary and build metadata (the Sigstore bundle is independently signed).
4. The workflow creates a GitHub release. When Cloudflare R2 is configured, immutable versioned binaries, build metadata and the checksum manifest are uploaded and verified there. The signed Sigstore bundle remains a GitHub release asset because its timestamped bytes can change on a retry.
5. Review the published GitHub release assets and test the exact binaries.
6. Run **Actions → Publish SmartPrint to Microsoft Store and Google Play**, choose the tag and tracks, and set `confirm_publication=true`.
7. Microsoft receives the signed installer URL through the Store Submission API. Google Play receives the signed AAB. Production jobs use protected GitHub Environments.

A PR merge never submits an app to either public Store. A stable release build and a separate explicit publishing dispatch are required.

## Microsoft Store certification risk: Windows Agent service

SmartPrint's installer creates the `QRtoPrintSmartPrintAgent` Windows NT service. The current installer configures it to run as the built-in `NT AUTHORITY\LocalService` account (not `LocalSystem`), with automatic startup and service recovery. The Agent is required for SmartPrint's local printer integration. It is not a kernel-mode printer driver.

**This must be disclosed in Partner Center.** On the MSI/EXE submission's Properties page, select the declaration that the app depends on non-Microsoft drivers or NT services, and explain the dependency in Notes for certification. Microsoft's policy says these dependencies are generally not allowed and are reviewed case by case; disclosure does not guarantee approval. Do not claim accessibility certification or any other declaration unless separately tested.

Suggested certification note (review against the final Agent implementation before submission):

> SmartPrint installs one Windows NT service named QRtoPrintSmartPrintAgent. The service runs under the built-in NT AUTHORITY\LocalService account, not LocalSystem, and no kernel-mode driver is installed. It provides the local printer-integration component required by the SmartPrint desktop application, including local printer inventory and secure communication with the desktop client. The installer configures automatic startup and service recovery. The service is removed during application uninstall. The installer does not install unrelated third-party applications.

If Microsoft rejects this dependency, stop the submission. The next decision would be a Store-specific architecture that does not require a non-Microsoft NT service (for example, a user-session Agent), followed by a full printing regression test. Do not switch to MSIX as a superficial workaround: packaged services use restricted capabilities and also require Microsoft approval.

## Release provenance verification

Each release includes a signed SLSA provenance attestation created by GitHub Actions. The release workflow attests the Windows and Android binaries plus `BUILD-INFO.txt`; the attestation bundle is included as `attestation.sigstore.json` in the GitHub release. The checksum manifest covers the stable binaries and build metadata, while the Sigstore bundle is independently signed. The bundle is not copied to immutable R2 storage because its timestamped signature can differ when a release workflow is retried. The Store publishing workflow verifies the relevant artifact attestation before it calls either Store API. The publishing runners require GitHub CLI 2.106.0 for secure attestation verification.

You can independently verify a release artifact from a machine with GitHub CLI 2.106.0:

```powershell
gh attestation verify .\SmartPrint-Windows-Store-Setup.exe --repo Manav326/smart-print --signer-workflow Manav326/smart-print/.github/workflows/release.yml
```

## One-time owner setup

### A. Windows code-signing certificate

Obtain an Authenticode code-signing certificate from a certificate authority whose chain is trusted by the Microsoft Trusted Root Program. It must be suitable for signing SmartPrint's Windows PE files and installer.

Export an installable PFX (PKCS#12) containing the private key and certificate chain. Protect it with a strong password. Do not commit it, upload it to an issue/PR, or send it in chat.

Convert the PFX file to a single-line Base64 value locally:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\secure\SmartPrint-CodeSigning.pfx")) | Set-Clipboard
```

Add these GitHub repository Actions secrets:

| Secret | Value |
|---|---|
| `WINDOWS_CODESIGN_PFX_BASE64` | Single-line Base64 of the PFX file |
| `WINDOWS_CODESIGN_PFX_PASSWORD` | PFX export password |

The workflow signs every detected SmartPrint desktop/Agent PE binary and the final Microsoft Store installer with SHA-256 and an RFC 3161 timestamp. Inno Setup also signs the Store installer's generated uninstaller. The Store installer is offline and includes the Microsoft WebView2 Evergreen Standalone Runtime. The website installer remains intentionally unsigned and retains the existing WebView2 bootstrapper behaviour. The Microsoft WebView2 runtime remains Microsoft-signed and is verified, not re-signed.

If the certificate is hardware-backed/non-exportable, do not export or weaken it. The current workflow expects an exportable PFX; ask for a Trusted Signing/HSM integration change before using a hardware-backed certificate.

### B. Windows publisher display name

Add this GitHub repository Actions variable using the exact publisher name shown in Partner Center:

| Variable | Value |
|---|---|
| `SMARTPRINT_WINDOWS_PUBLISHER` | Exact Partner Center publisher display name to embed in Windows installer metadata |

Because the selected Microsoft developer account is Individual, confirm the exact public publisher identity shown in Partner Center before setting this value. The code-signing certificate subject must contain this configured publisher identity (case-insensitive); it need not be byte-for-byte identical to the certificate CN. The workflow checks certificate validity, the Code Signing EKU, the publisher subject and Authenticode trust before producing the Store installer. Confirm the certificate subject with the certificate provider before configuring CI.

### C. WebView2 offline prerequisite

The Microsoft Store installer must be standalone/offline. The release workflow uses the Microsoft Evergreen **Standalone Installer x64**, not the small Evergreen Bootstrapper, for the signed Store package. The existing website installer intentionally retains its current bootstrapper-based direct-download behaviour. If signing inputs are absent, only the unsigned website installer is produced.

1. Open the official [Microsoft WebView2 download page](https://developer.microsoft.com/microsoft-edge/webview2/).
2. Under **Evergreen Standalone Installer**, select **x64**.
3. Copy the direct HTTPS download URL for the standalone installer.
4. Add a GitHub repository Actions variable:

| Variable | Value |
|---|---|
| `WEBVIEW2_STANDALONE_X64_URL` | Direct HTTPS URL for Microsoft's Evergreen Standalone Installer x64 |

The workflow downloads it during the Windows build and verifies Microsoft's Authenticode signature before including it. Do not use the Bootstrapper URL; it downloads the runtime during installation and is not an offline installer.

### D. Existing website/R2 configuration

The existing release workflow already expects these repository variables/secrets. Confirm they are configured and correct:

| Name | GitHub location | Purpose |
|---|---|---|
| `SMARTPRINT_PUBLIC_BASE_URL` | Variable | Canonical HTTPS SmartPrint API/portal origin |
| `PUBLIC_DOWNLOAD_BASE_URL` | Variable | Canonical HTTPS public R2 custom-domain origin |
| `CLOUDFLARE_R2_ACCOUNT_ID` | Variable | 32-character Cloudflare account ID |
| `CLOUDFLARE_R2_BUCKET` | Variable | Release bucket |
| `CLOUDFLARE_R2_ACCESS_KEY_ID` | Secret | R2 object access key |
| `CLOUDFLARE_R2_SECRET_ACCESS_KEY` | Secret | R2 object secret |

The public download base must be the root HTTPS custom-domain origin, not the `r2.dev` development URL. The workflow stores immutable objects under `/<tag>/filename`.

### E. Android upload signing

The release build already expects the existing Android upload keystore and signing values. Confirm these four GitHub repository Actions secrets exist and correspond to the same keystore used for prior Android releases:

| Secret | Format |
|---|---|
| `ANDROID_SIGNING_KEYSTORE_BASE64` | Single-line Base64 of the existing `.jks`/`.keystore` file |
| `ANDROID_SIGNING_KEYSTORE_PASSWORD` | Keystore password |
| `ANDROID_SIGNING_KEY_ALIAS` | Existing key alias |
| `ANDROID_SIGNING_KEY_PASSWORD` | Key password |

Add this optional GitHub repository Actions variable if the app already has Play uploads with a versionCode at or above the default:

| Variable | Value |
|---|---|
| `ANDROID_VERSION_CODE_BASE` | Non-negative integer greater than the highest previously used Play versionCode; defaults to `100000` when unset |

Never generate a new upload key for a later release. Keep a secure offline backup of the keystore and passwords. If the existing key is lost, stop and plan Google's upload-key reset process rather than silently replacing it.

### F. Microsoft Partner Center (one-time manual setup)

The Microsoft Store Submission API cannot create the first product or perform the initial submission. Complete these actions once in [Partner Center](https://partner.microsoft.com/dashboard):

1. Reserve the product name **SmartPrint** as an **EXE/MSI app**.
2. Create and complete the first submission manually, including the age-rating questionnaire, Store listing, package details and declarations.
3. Add the x64 EXE package using the versioned Store installer URL from a successful GitHub release:
   `https://<PUBLIC_DOWNLOAD_BASE_URL>/<tag>/SmartPrint-Windows-Store-Setup.exe`
4. Use installer parameters:
   `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG`
5. Set architecture to **X64**, package type **EXE**, language **en-us**, and indicate that installer parameters are required.
6. Disclose the Windows Agent NT service dependency in Partner Center's dependency declarations. SmartPrint installs and manages the non-Microsoft service `QRtoPrintSmartPrintAgent`, runs it as `NT AUTHORITY\LocalService`, and uses it to access local printer queues and expose the local Agent API on TCP 17842. Do not declare this dependency as false. The application does not install a printer driver or modify existing printer queues, ports, or the Windows Spooler configuration.
7. In **Notes for certification**, explain that the elevated installer installs the service and that uninstall stops/removes the service and its versioned firewall rules. SmartPrint deliberately retains its encrypted machine identity and local print-queue data under `%ProgramData%\QRtoPrint` so a reinstall can recover the same device identity and pending work. Explain this retention and the supported revoke-before-disposal process; do not describe uninstall as erasing that machine data.
8. Provide a dedicated test account and any required test steps through Partner Center's certification-notes facility. Explain that a physical printer is not required to verify the UI, and give the steps to use a Windows virtual printer when print submission is being tested. Never put test credentials in this repository or a GitHub release.
9. Test the exact signed Store installer with the documented silent parameters on a clean Windows x64 VM using a standard user account and expected UAC elevation. Confirm install, service start, local printer discovery/print, upgrade, service rollback and uninstall. Check that the encrypted retained data is the only intentional product data left behind.
10. Provide a valid privacy policy, support contact, website, app description, screenshots, logo and age rating. Confirm the listing's publisher identity is the intended individual/business identity.
11. Save and submit the initial listing through Partner Center. The API automation becomes available after this first submission is completed. If SmartPrint has already been submitted and is currently in certification, do not create a duplicate submission; wait for the current one to complete before using the API workflow.

**Privacy policy and contact details:** the repository currently contains a private-printing-specific privacy explainer, but I did not find a complete public SmartPrint product privacy policy. That page is not a substitute for the overall policy. Supply a public HTTPS privacy-policy URL that accurately describes document/photo handling, uploads, account data, diagnostics, retention and deletion. Also supply the public support URL and support email. Enter these in Partner Center's Store listing/properties and Google Play Console's App content/Data Safety and Store listing. Do not publish until the policy matches the actual production data practices.

Create/associate a Microsoft Entra ID app with the Partner Center account and grant it the Partner Center **Manager** role as documented by Microsoft. Create a client secret. Add:

| Name | GitHub location | Value |
|---|---|---|
| `MICROSOFT_STORE_PRODUCT_ID` | Variable | Product ID from Partner Center |
| `MICROSOFT_STORE_SELLER_ID` | Variable | Seller account ID from Partner Center |
| `MICROSOFT_STORE_TENANT_ID` | Variable | Entra tenant ID |
| `MICROSOFT_STORE_CLIENT_ID` | Variable | Entra application/client ID |
| `MICROSOFT_STORE_CLIENT_SECRET` | Secret | Entra client-secret **value** (not secret ID) |

The publishing script patches only the existing X64 package, commits the package module, waits for package validation, then submits the package for Microsoft certification. It refuses to replace multiple architectures or create a competing active submission.

### G. Google Play Console (one-time manual setup)

1. Create the SmartPrint app in [Google Play Console](https://play.google.com/console/) using package name `com.quantaedge.smartprint`.
2. Enrol in Play App Signing and upload the first signed `SmartPrint-Android.aab` manually through Play Console. The publishing action cannot create the package entry; use the same upload keystore that will be configured in GitHub Actions.
3. Complete app access, content rating, Data Safety, privacy policy, ads declaration, target audience, app category, store listing, screenshots and required declarations.
4. Enable the Google Play Android Developer API in a Google Cloud project.
5. Create a service account and grant it only the required Play Console app/release permissions.
6. Add the service-account JSON key contents as this GitHub repository Actions secret:

| Secret | Value |
|---|---|
| `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON` | Complete service-account JSON file contents, including braces |

Do not commit the JSON key or paste it into chat. The app's package name is fixed in the workflow as `com.quantaedge.smartprint`. The workflow computes a monotonic `versionCode` as `ANDROID_VERSION_CODE_BASE + GITHUB_RUN_NUMBER`; if this is a new Play app, leave the variable unset (default base 100000). If the app has any previous Play uploads, set the variable to a value greater than the highest versionCode already used before enabling automation.

For the Windows Store listing, prepare the app description, short description, category, age rating, 1:1 Store logo and current Windows screenshots. For a desktop-only EXE app, prepare:
- **1:1 box-art Store logo:** PNG, square aspect ratio. Microsoft's EXE listing requires this asset; Partner Center validates the accepted pixel dimensions for the selected slot.
- **Desktop screenshots:** PNG, landscape or portrait, at least 1366 × 768, no more than 50 MB each. One is required; prepare four or more, up to ten.
- **Description:** required, up to 10,000 characters.
- **Applicable license terms:** required, up to 10,000 characters (for example, the actual proprietary/commercial license terms that apply to SmartPrint).
- **Short description:** recommended, up to 1,000 characters.
- **Features:** optional, up to 20 entries, max 200 characters each.
- **What's new:** optional, up to 1,500 characters per release.
- **Search terms:** optional, up to seven terms, each max 40 characters, 21 unique words total.
- **Support contact, website and privacy policy:** provide real, monitored contact details and public HTTPS URLs.

For Google Play, prepare:
- **App icon:** 512 × 512 PNG, 32-bit with alpha, maximum 1 MB.
- **Feature graphic:** 1024 × 500 JPEG or 24-bit PNG, no alpha.
- **Phone screenshots:** 2–8 PNG/JPEG images, each up to 8 MB, each side 320–3,840 pixels. Prefer at least four actual SmartPrint Android screens at 1080p or higher for promotional eligibility; use actual app screens, not Windows screenshots.
- **Short description:** up to 80 characters.
- **Full description:** up to 4,000 characters.
- **Privacy policy URL and Data Safety answers:** must match actual data collection, sharing and retention.
- **App category, contact email and website:** actual product/support information.

Do not create fake feature claims or show screens that are not in the released app. Capture current UI from real QA builds. If you want, the same core brand assets can be adapted to each Store's required aspect ratio, but do not stretch a single image across all slots.

If this is a personal Play developer account created after 13 November 2023, Google may require at least 12 opted-in closed-testers for 14 continuous days before production access. Complete the Play Console testing requirement before selecting production.

## GitHub Environments and approvals

Create these repository Environments under **Settings → Environments**:

| Environment | Use |
|---|---|
| `microsoft-store-production` | Windows Store submission |
| `google-play-testing` | Internal/closed Play tracks |
| `google-play-production` | Production Play rollout |

Configure required reviewers for `microsoft-store-production` and `google-play-production`. Restrict deployment branches to `main` and stable release tags where supported. The workflow also requires `confirm_publication=true`; do not rely on that input instead of environment protection.

The Play production workflow creates a 10% staged rollout (`inProgress`) rather than immediately releasing to 100%. Complete the rollout in Play Console after monitoring the initial cohort.

## Running a release

1. Ensure all required secrets/variables are present.
2. Merge approved work to `main`; the existing main-CI automation creates a stable tag and runs the release build. Every release job checks out that exact tag, and the validation job rejects tags that do not point to a commit reachable from `main`.
3. Open the resulting GitHub release and review the installer, APK, AAB, Store installer (if configured), and checksums.
4. Test the unsigned website installer on a clean Windows machine. If the signed Store artifact is present, test that exact offline installer on a clean Windows machine, including silent installation with the documented switches, Agent service startup, upgrade and uninstall. Install the AAB on test devices/Play internal track.
5. Open **Actions → Publish SmartPrint to Microsoft Store and Google Play**.
6. Enter the release tag, choose which Stores to publish, choose the Play track, and set `confirm_publication=true`.
7. Approve the relevant GitHub Environment deployment.
8. Follow certification/review status in Partner Center and Play Console.

## Future updates

For every feature or bug-fix release, increment the stable semantic version (new `vX.Y.Z` tag). The existing release automation creates the binaries from the exact tested commit. The same publishing workflow updates the X64 Microsoft Store package URL and uploads the AAB to the selected Play track. Do not overwrite an already-published release or reuse an old tag. Keep the package ID `com.quantaedge.smartprint` and Android upload key unchanged.

If the `publish-release` job fails after uploading immutable R2 objects, rerun **only the failed job(s)** in that same GitHub Actions run (Actions → failed run → Re-run failed jobs). The Windows and Android build jobs must remain successful and be reused so the exact same signed binaries and build metadata are retried. Do not choose “Re-run all jobs” after partial R2 publication, because code-signing timestamps and build outputs can change even when the source tag is unchanged. Do not start a second workflow_dispatch for the same tag: `GITHUB_RUN_NUMBER` contributes to the Android versionCode and Agent side-by-side folder name, so a new run for the same tag intentionally produces different bytes and the immutable R2 guard will reject it. The source SHA, run ID, run number and build metadata remain stable across retries; the timestamped Sigstore bundle is regenerated only as a GitHub release asset and is deliberately excluded from immutable R2 storage. If any binary or stable metadata bytes differ from objects already uploaded, the immutable guard must fail closed—do not bypass it; create a new semantic version after investigating. If the same tag has already been published, create a new semantic version instead.



### Existing Windows customer updates — separate requirement

Microsoft explicitly states that Store-submitted EXE/MSI apps do **not** receive Store-pushed updates for existing users. The publisher's app or installer must handle those updates. The current SmartPrint repository has no Tauri/Windows in-app updater configured.

Until an updater is implemented, existing Windows customers must manually download and rerun the latest signed installer. The release workflow updates the Store's versioned package URL for new installs and certification, but does not silently update already-installed Windows copies.

Google Play handles Android app updates for users who installed SmartPrint through Play, subject to their device's auto-update settings. Direct APK users continue to need a website update mechanism.

A separate Windows updater should be designed to:
- read the installed distribution channel (website or Store);
- fetch a signed, versioned update manifest;
- verify the installer hash and Authenticode publisher before execution;
- prompt the user before installation and elevation;
- update the desktop app and Windows Agent together, preserving pairing, settings and print data;
- support rollback and prevent downgrade/replay.

## What this automation does not do

- It cannot create the initial Partner Center product, complete the initial age-rating/listing setup, accept agreements or bypass Store certification.
- It cannot create the first Play Console app, complete policy declarations or bypass Google's account-specific testing requirements.
- It does not automatically publish Store updates on every PR merge. Store submissions are deliberately gated by an explicit manual dispatch and protected environments.
- It does not make the existing Inno Setup installer MSIX. The Store distribution is the accepted unpackaged Win32 EXE route so the Windows Agent service installation architecture is retained.
