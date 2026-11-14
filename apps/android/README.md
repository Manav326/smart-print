# SmartPrint by QuantaEdge — Android

The customer-facing app is **SmartPrint by QuantaEdge**. QRtoPrint remains the repository and technical integration name. The Android app is a customer companion to the existing portal and API. It does not run its own pricing, payment or print-dispatch backend.

## Native Android experience

The visual treatment takes its reference from the Liquid Glass implementation in BajajDealership at apps/android-liquid-glass: soft warm/cool ambient lighting, translucent rounded cards, fine glass edges and real blur on the fixed top and bottom navigation. SmartPrint retains its own violet accent. The native app uses BlurView with a RenderEffect path on Android 12+ and a compatible fallback on older supported Android versions. Dense forms, prices, documents and actions remain high contrast.

The top toolbar and bottom navigation are layered over a BlurTarget; only the navigation chrome is blurred. Cards, secondary actions, QR scanner controls and bottom sheets use translucent fills with clear borders and controlled elevation. The embedded customer print route uses matching light/dark glass tokens. Blur is decorative only; controls remain usable when the platform falls back to translucent surfaces.


SmartPrint uses a compact, theme-aware native shell for Home, Files, Scan, Activity, Account, the QR scanner, PDF editing and file actions. Light and dark palettes share the same surfaces, typography, icon treatment and spacing. File and folder rows remain compact; folders expand inline with indentation and file-type icons. Rename, move, delete and confirmation actions use native bottom sheets.

The shop print workflow reuses the existing customer portal and APIs inside the secure Android WebView bridge. The app identifies itself with the `SmartPrintAndroid/1` user-agent token so the customer print route switches to a full-width native presentation with compact copy and no website masthead or card frame. Ordinary browser visitors retain the standard web layout. QR station validation, secure origin pinning, encrypted per-station guest sessions, upload limits, shop review, payment verification, refunds and physical-output confirmation remain the same.

## Local document library

- Documents are copied into app-private storage under `files/documents`.
- Create nested folders, move files/folders between directories, and reorder sibling items without renaming customer files.
- Select several local photos and combine them into a PDF; original images remain unchanged.
- Create nested folders, import multiple files, or import a directory tree using Android's Storage Access Framework.
- Export one file or an entire folder tree into a user-selected Android File Manager destination. Multiple selected files export together as a ZIP archive that preserves their original filenames.
- Open/share files with other Android apps.
- The customer portal's native picker separates **Select files** and **Choose folders**. File selection offers both **SmartPrint My Files** and **Browse phone**. Browse phone uses Android's system document picker and passes selected PDF/JPG/PNG content URIs directly to the existing WebView upload input; files are not copied into the SmartPrint library first. Folder selection remains in SmartPrint My Files, shows folder rows only, permits multiple folder selection, and includes supported files directly inside each selected folder (nested subfolders are intentionally not traversed). The picker preserves the current nested-folder location while making selections. Folder names are passed to the portal so each file remains in the correct batch group. The library includes current-folder search, a move destination picker, a document-storage summary and a free-space reserve check.

## Print batches and saved PDF editing

**Separate PDF merge helper:** Select 2–20 PDF, JPG, JPEG or PNG files in one library folder and choose **Combine into one PDF**. The app combines them in displayed selection order into one PDF and preserves the originals. PDF pages are rasterized at up to a 2,000-pixel long edge; each image becomes one page. The result is limited to 20 MB and 500 pages; source selection may total up to 100 MB. This helper creates one uploaded document, not a true multi-file server-side collection. Use the portal's **Select multiple files** input for separate print jobs in one batch.

**Server-side batch capability now present in main:** The portal/API/Windows Agent workflow has been extended to handle a multi-file print batch, with shop-side per-file review and rejection/refund handling. The native WebView picker now recognises when the portal requests multiple files, keeps selection while browsing nested local folders, and returns original file URIs in selection order. The existing portal uploads each file separately with one shared batch ID, using the shop's current upload/quote/review/payment/dispatch lifecycle. CI and physical-device/provider acceptance for this integration are still pending.

Saved PDFs also expose **Edit PDF pages** from the file menu. Customers can preview a page, rotate by 90 degrees, move pages up/down, remove unwanted pages and save a new edited copy. The original PDF is never overwritten. The editor uses Android's PDF renderer and rasterizes edited pages, so this is a convenient document-correction workflow rather than lossless vector PDF editing. The in-app warning now explains that selectable text, vector content and some fine detail can be lost.

- 
General library imports allow up to 100 MB per file. The print workflow uses the existing server-supported single-document types and 20 MB hard limit. Files remain on the device until the customer chooses to upload one.

## SmartPrint document capture

The native document-capture action uses **Google ML Kit Document Scanner exclusively on every phone and tablet**, in full mode. There is no separate CameraX document scanner. The Google Play services UI owns the viewfinder, camera shutter, gallery import, page review, edge detection, perspective correction, crop, rotation, filters and multi-page PDF creation. SmartPrint requests PDF and JPEG results and saves the completed PDF into the selected My Files destination. Google Play services downloads the scanner UI/models when needed; first launch can require a download, and devices below the scanner's 1.7 GB RAM requirement are not supported. If ML Kit is unavailable on a device, the app reports the error and customers can still import existing photos/PDFs from the Android file picker; it does not launch another camera implementation. **Scan document** on Home asks where to save the completed scan (My Files, an existing folder, or a new folder). **Scan here** inside My Files saves directly into the currently open folder. Captures remain local until the customer chooses to print.

Document scanning does not need a camera permission owned by SmartPrint; Google Play services handles camera access for its scanner UI. The separate shop-QR reader continues to use CameraX frames processed locally by ML Kit Barcode Scanning, because QR decoding and document scanning are different tasks.

### Responsive Android navigation and document scanning

- **Portrait always uses bottom navigation**, including portrait tablets. **Landscape always uses a left-side navigation rail.** The layout decision follows orientation, not device dp width. Compact portrait layouts keep the active icon and label on one horizontal line; inactive labels stay hidden until the device has enough width for all four labels.
- The connected-shop card shows the connected shop's display name and logo when the service supplies a trusted HTTPS logo; it uses a storefront icon when no logo is available. **Manage connected shop** provides open, switch and local disconnect actions. Disconnect removes only this device's station token and shop association; it does not delete local documents or shop orders/payments.
- Phones and tablets use the exact same Google Play services ML Kit Document Scanner UI and full feature mode. The scanner supplies its own camera shutter, gallery import, multi-page capture, page review, auto edge detection, crop/perspective correction, rotation, filters and PDF creation. No CameraX document-scanning implementation or second scanner flow exists.
- ID Studio keeps the ML Kit camera and review screen full-screen and unobstructed. For each side of an ID, customers can take a photo with ML Kit or import an existing image from the phone gallery or SmartPrint My Files. Imported images are validated for card shape and resolution, respect EXIF orientation, and are copied into the encrypted ID vault; either side can be replaced before review.
- The shop QR scanner remains separate and uses CameraX only to provide local image frames to ML Kit Barcode Scanning; it is not used for document scanning.



## Physical-device release acceptance

The complete real-device matrix, Windows/Android end-to-end acceptance sequence, Android 16 opt-in restriction test and test sign-off record are maintained in [docs/ANDROID_PHYSICAL_ACCEPTANCE.md](../../docs/ANDROID_PHYSICAL_ACCEPTANCE.md). These checks are not complete until executed on physical phones/tablets and a real Windows shop computer; CI or emulator success alone does not close them.

## Global SmartPrint customer account

SmartPrint customer accounts are global and independent of any print shop. A customer registers once with SmartPrint, then uses the same account at any participating shop by scanning that shop's QR code. Shops are session destinations, not customer-account owners. New authenticated QR sessions carry the global customer ID; account print history is aggregated across shops by that ID. Guest customers can still scan, organise and print without registration. Existing guest sessions and print jobs are not silently attached to an account later.

Customer identity is separate from shop staff identity and uses dedicated `/api/v1/auth/register-customer` and `/api/v1/auth/customer-login` endpoints, plus Google ID-token sign-in at `/api/v1/auth/google/customer`. The native Account screen uses Android Credential Manager and the Google ID SDK; the server verifies the signed ID token, checks a short-lived single-use sign-in challenge, and links the Google subject through ASP.NET Identity. A first Google sign-in creates a global customer identity only when the email is not already owned by a different SmartPrint account type. Android access/refresh tokens and guest station-session tokens are encrypted with Android Keystore AES-GCM. Guest tokens are stored per station, so switching shops does not overwrite another shop's resumable session. The browser persists only non-secret job metadata per station.

Google setup is shared with the shop portal: create a Google OAuth **Web application** client, configure its ID as `GOOGLE_WEB_CLIENT_ID` on the API, and add the deployed shop portal origin to that OAuth client's authorised JavaScript origins. In the same Google Cloud project, register the Android application ID (`com.quantaedge.smartprint`) with the release signing certificate's SHA-1 and SHA-256 fingerprints. The Web client ID is public and is returned by `/api/v1/auth/google/config`; no Google client secret is bundled in the portal or APK. Local QA builds must be registered separately if they use a different application ID or signing certificate.
 The WebView does not expose credentials through a generic JavaScript interface: AndroidX WebKit messaging is restricted to the configured top-level service origin and main frame, never to a host supplied by a scanned QR code.

## QR and print flow

The single shop QR scanner uses **ML Kit Barcode Scanning** to decode QR payloads. CameraX supplies the live camera frames and flashlight control for this custom preview; it is not a second barcode engine or a document-scanning fallback. The separate ML Kit Document Scanner module is used for paper documents because page detection, crop, filters and PDF assembly are a different task. Both are Google ML Kit features.

After decoding, SmartPrint routes the payload by its strict format:
- Customer station QR: the configured HTTPS SmartPrint origin and `/s/{stationCode}`; creates the normal short-lived customer session.
- Shop-owner phone-readiness QR: the Agent's one-time private IPv4 URL at port `17842`, path `/api/v1/local/connection-check`, and a 43-character token. SmartPrint makes the GET request itself; it does not open an arbitrary browser URL. The Agent accepts the token once, binds the result to the selected network, expires the QR after five minutes, and records a successful phone check for ten minutes.

The phone-readiness parser rejects public hosts, non-RFC1918 addresses, different ports/paths, extra query parameters, redirects and malformed tokens. The two QR purposes share one ML Kit detector and do not create two scanner screens. The connected-shop manager exposes **Check shop Wi-Fi** as a separate action; a station QR scanned in that mode is rejected without changing the connected shop. The Windows phone-readiness panel directs SmartPrint users to this in-app path and retains a browser fallback for phones without the app.

The customer station QR and shop Wi-Fi readiness QR use the same ML Kit scanner surface but have separate entry purposes and strict validators. **Check shop Wi-Fi** is available from the connected-shop management sheet and never invokes the shop-switch action. Its scanner accepts only the temporary private Agent readiness URL; the normal connect/switch entry accepts only the configured SmartPrint station URL. A QR from the wrong purpose is rejected with a clear explanation before any shop or session state can change.

## QR station validation and print flow

The QR station code format is matched to the API's current generator: 24 random bytes encoded as a 32-character Base64URL path segment. The app validates the host against the configured smartPrintServiceUrl HTTPS origin, as well as the route, port, query and fragment. Production QR codes from any other host are rejected. A debug build may be configured with a private IPv4 HTTP service origin on port 3000; the scanned station URL must still match that exact configured origin, and the WebView credential bridge remains pinned to it.

The CameraX preview feeds frames to bundled ML Kit Barcode Scanning locally. No camera frame is uploaded or retained. Only a decoded QR payload leaves the scanner, and it is accepted only when it matches the HTTPS `/s/{publicCode}` station route.

The embedded customer portal then creates the same station-bound session as the PWA. Cloud-assisted and local Agent upload share the existing API, quote, shop approval, payment and dispatch lifecycle. Local Agent upload is allowed only to private IPv4 addresses on TCP 17842 and its status/upload routes; the customer portal itself remains HTTPS. Android WebView's compatibility mixed-content mode can block these active HTTP fetches, so the WebView enables mixed-content handling while its request interceptor continues to reject every HTTP URL outside the exact private-Agent allowlist. On Android 16, the WebView inherits the host app's local-network access state. Local-network protection is opt-in during Android 16, and apps targeting API 36 or lower retain implicit local-network access through INTERNET; SmartPrint therefore does not show a Nearby devices permission prompt during normal use. The manifest retains NEARBY_WIFI_DEVICES (capped at API 36) only for explicit restricted-network QA. To test that opt-in mode, enable RESTRICT_LOCAL_NETWORK for the app with adb, reboot, and grant Nearby devices from Android Settings; disable the compatibility flag after the test. Android 17 requires ACCESS_LOCAL_NETWORK only after SmartPrint targets API 37, at which point the permission must be added to the manifest and requested when the customer starts a local-network action.

## Build

From repository root:

```bash
gradle --no-daemon -p apps/android :app:testDebugUnitTest :app:assembleDebug
```

Build the Android app with the service origin configured in one place:

```bash
$env:SMARTPRINT_PUBLIC_BASE_URL = "<the canonical HTTPS origin configured for this deployment>"
gradle --no-daemon -p apps/android -PsmartPrintServiceUrl="$env:SMARTPRINT_PUBLIC_BASE_URL" :app:testDebugUnitTest :app:assembleDebug
```

The build requires an explicit `smartPrintServiceUrl` Gradle property or `SMARTPRINT_PUBLIC_BASE_URL` environment variable. It intentionally has no baked-in service-host default, preventing test and production APKs from silently targeting the wrong deployment. The CI workflow uploads the debug APK as `smartprint-android-debug`. Download it with `scripts\download-android-apk.ps1`; the script saves it as `SmartPrint-Android-debug.apk` under `%USERPROFILE%\Downloads\SmartPrintAndroid` and can still use retained APK artifacts from older CI runs. Add `-Install` to install it on one connected, ADB-authorised Android device. Debug builds are for development only. Release signing, physical Android/Wi-Fi validation, Play Store listing and distribution are not yet complete.

The native Activity screen shows account-linked job dates, original filenames, quoted amounts, payment method/status, paid/refunded/net amounts, rejected pages and output-confirmation time. The API returns these fields through tenant-scoped document/payment queries; account history remains limited to the latest 100 jobs.

The CI Android job also uploads JVM and emulator test reports as `smartprint-android-test-reports`, including JUnit XML and the connected-test HTML report when generated. Use the report artifact to inspect individual test cases and failures from a CI run.

## Automated Android UI tests

The CI Android job also launches an Android 16 (API 36) emulator and runs
`connectedDebugAndroidTest`. Current instrumented coverage includes SmartPrint home/navigation, global guest-account entry, Scan & Print explanation, nested-folder operations, scanner permission explanation, encrypted account-token storage, mixed PDF/image batch composition and saved-PDF page edits. QR regression coverage verifies purpose-specific shop-connection and Wi-Fi-readiness instructions, and confirms that the connected-shop Wi-Fi check does not route through the shop-switch action. The merged Android baseline passed CI; each subsequent branch change must pass CI before it is treated as verified. This remains emulator/API/unit coverage; it does not replace physical-device camera, Google Play services scanner, file-manager provider, shop Wi-Fi, payment or printer validation.


## Customer-app recovery and hardening

- The Scan & Print destination shows the current shop session and offers an explicit **Scan another shop** action. The toolbar also exposes **Switch shop** when connected.
- The active WebView state is saved on Activity recreation. Guest station tokens are persisted in encrypted Android Keystore storage; active-job drafts are stored per station without storing the bearer token in browser storage.
- Multi-file batch items refresh their own server status instead of presenting every non-selected item as merely Uploaded.
- Customer account navigation returns to the destination from which the account was opened. The Activity list shows the job creation date.
- The native PDF combine/editor disclosures explain rasterization and possible quality/searchability loss.

Email verification/password recovery are not exposed as functional controls yet because the current API has no customer reset/verification-token endpoints or email-delivery contract. The account activity API also needs additional fields before the app can show actual paid/refunded/net amounts and original filenames. See docs/ANDROID_CUSTOMER_APP_HARDENING.md for the remaining backend and physical-device gates.



