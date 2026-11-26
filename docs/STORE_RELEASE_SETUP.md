# SmartPrint Store publishing setup

This repository uses one versioned GitHub release to produce the website downloads and Store-ready binaries. Store submission is a separate, manually dispatched workflow with explicit confirmation and protected GitHub Environments.

## Release flow

1. Merge tested changes to `main`.
2. The existing main-CI release automation creates a stable `vX.Y.Z` tag and runs `.github/workflows/release.yml`.
3. The release workflow builds:
   - `SmartPrint-Windows-Setup.exe`: existing website beta installer (kept separate).
   - `SmartPrint-Windows-Store-Setup.exe`: signed, offline x64 Store installer, only when the Windows signing certificate and WebView2 standalone URL are configured.
   - `SmartPrint-Android.apk`: website/direct-download Android package.
   - `SmartPrint-Android.aab`: signed Google Play App Bundle.
   - `SHA256SUMS.txt`: checksums for the artifacts produced.
4. The workflow creates a GitHub release. When Cloudflare R2 is configured, immutable versioned website assets are uploaded and verified there.
5. Review the draft assets and test the exact binaries.
6. Run **Actions → Publish SmartPrint to Microsoft Store and Google Play**, choose the tag and tracks, and set `confirm_publication=true`.
7. Microsoft receives the signed installer URL through the Store Submission API. Google Play receives the signed AAB. Production jobs use protected GitHub Environments.

A PR merge never submits an app to either public Store. A stable release build and a separate explicit publishing dispatch are required.

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

The workflow signs the SmartPrint desktop/Agent PE binaries and final Store installer with SHA-256 and an RFC 3161 timestamp, then verifies each signature. The Microsoft WebView2 runtime remains Microsoft-signed and is verified, not re-signed.

If the certificate is hardware-backed/non-exportable, do not export or weaken it. The current workflow expects an exportable PFX; ask for a Trusted Signing/HSM integration change before using a hardware-backed certificate.

### B. Windows publisher display name

Add this GitHub repository Actions variable using the exact publisher name shown in Partner Center:

| Variable | Value |
|---|---|
| `SMARTPRINT_WINDOWS_PUBLISHER` | Exact Partner Center publisher display name to embed in Windows installer metadata |

Because the selected Microsoft developer account is Individual, confirm the public publisher identity shown in Partner Center before setting this value. The Store installer build is withheld until this value is supplied.

### C. WebView2 offline prerequisite

The Store installer must be standalone/offline. The existing website installer continues using its existing bootstrapper; the Store installer uses the Microsoft Evergreen **Standalone Installer x64**, not the small Evergreen Bootstrapper.

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

The public download base must be an HTTPS custom domain (an optional path prefix is supported), not the `r2.dev` development URL. The workflow stores immutable objects under `/<tag>/filename`.

### E. Android upload signing

The release build already expects the existing Android upload keystore and signing values. Confirm these four GitHub repository Actions secrets exist and correspond to the same keystore used for prior Android releases:

| Secret | Format |
|---|---|
| `ANDROID_SIGNING_KEYSTORE_BASE64` | Single-line Base64 of the existing `.jks`/`.keystore` file |
| `ANDROID_SIGNING_KEYSTORE_PASSWORD` | Keystore password |
| `ANDROID_SIGNING_KEY_ALIAS` | Existing key alias |
| `ANDROID_SIGNING_KEY_PASSWORD` | Key password |

Never generate a new upload key for a later release. Keep a secure offline backup of the keystore and passwords. If the existing key is lost, stop and plan Google's upload-key reset process rather than silently replacing it.

### F. Microsoft Partner Center (one-time manual setup)

The Microsoft Store Submission API cannot create the first product or perform the initial submission. Complete these actions once in [Partner Center](https://partner.microsoft.com/dashboard):

1. Reserve the product name **SmartPrint** as an **EXE/MSI app**.
2. Create and complete the first submission manually, including the age-rating questionnaire, Store listing, package details and declarations.
3. Add the x64 EXE package using the versioned Store installer URL from a successful GitHub release:
   `https://<PUBLIC_DOWNLOAD_BASE_URL>/<tag>/SmartPrint-Windows-Store-Setup.exe`
4. Use installer parameters:
   `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`
5. Set architecture to **X64**, package type **EXE**, language **en-us**, and indicate that installer parameters are required.
6. Disclose the Windows Agent NT service dependency in product declarations (the app installs/manages a Windows service). Do not declare this as false.
7. Provide a valid privacy policy, support contact, website, app description, screenshots, logo and age rating. Confirm the listing's publisher identity is the intended individual/business identity.
8. Save and submit the initial listing through Partner Center. The API automation becomes available after this first submission is completed. If SmartPrint has already been submitted and is currently in certification, do not create a duplicate submission; wait for the current one to complete before using the API workflow.

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
2. Enrol in Play App Signing and upload the first signed `SmartPrint-Android.aab` manually if needed to establish the app.
3. Complete app access, content rating, Data Safety, privacy policy, ads declaration, target audience, app category, store listing, screenshots and required declarations.
4. Enable the Google Play Android Developer API in a Google Cloud project.
5. Create a service account and grant it only the required Play Console app/release permissions.
6. Add the service-account JSON key contents as this GitHub repository Actions secret:

| Secret | Value |
|---|---|
| `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON` | Complete service-account JSON file contents, including braces |

Do not commit the JSON key or paste it into chat. The app's package name is fixed in the workflow as `com.quantaedge.smartprint`.

For the Windows Store listing, prepare the app description, short description, category, age rating, 1:1 Store logo and current Windows screenshots. Partner Center's listing editor validates the exact image dimensions and file types for each asset slot; use those displayed requirements rather than resizing one image into every slot.

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
2. Merge approved work to `main`; the existing main-CI automation creates a stable tag and runs the release build.
3. Open the resulting GitHub release and review the installer, APK, AAB, Store installer (if configured), and checksums.
4. Test the website installer on a clean Windows machine and install the AAB on test devices/Play internal track.
5. Open **Actions → Publish SmartPrint to Microsoft Store and Google Play**.
6. Enter the release tag, choose which Stores to publish, choose the Play track, and set `confirm_publication=true`.
7. Approve the relevant GitHub Environment deployment.
8. Follow certification/review status in Partner Center and Play Console.

## Future updates

For every feature or bug-fix release, increment the stable semantic version (new `vX.Y.Z` tag). The existing release automation creates the binaries from the exact tested commit. The same publishing workflow updates the X64 Microsoft Store package URL and uploads the AAB to the selected Play track. Do not overwrite an already-published release or reuse an old tag. Keep the package ID `com.quantaedge.smartprint` and Android upload key unchanged.

## What this automation does not do

- It cannot create the initial Partner Center product, complete the initial age-rating/listing setup, accept agreements or bypass Store certification.
- It cannot create the first Play Console app, complete policy declarations or bypass Google's account-specific testing requirements.
- It does not automatically publish Store updates on every PR merge. Store submissions are deliberately gated by an explicit manual dispatch and protected environments.
- It does not make the existing Inno Setup installer MSIX. The Store distribution is the accepted unpackaged Win32 EXE route so the Windows Agent service installation architecture is retained.
