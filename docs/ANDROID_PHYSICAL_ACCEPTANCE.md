# SmartPrint Android physical-device acceptance

**Status: Not yet physically verified.** CI and emulator results are development evidence only. Complete this checklist on real supported hardware before treating the Android customer/owner journeys as release-ready.

**Photo Studio requirement:** The Android customer app renders the shared PhotoStudio inside its native WebView. Complete APHO-001–036 and repeat the full PSHT-001–031 geometry matrix using Android-generated PDFs; a successful WebView launch or desktop browser run is not physical Android acceptance.

## Acceptance principles

### QR link and session-transfer contract

- **Scan inside SmartPrint:** open the app's Print → Scan QR code flow. The native scanner validates the station URL against the configured SmartPrint service origin, confirms the selected shop, and opens the same `/s/{publicCode}` portal route inside the app's WebView. The opaque print-session token is issued by the API and persisted through the Android Keystore bridge, scoped to that station; it is not added to the QR URL.
- **Open a link from the phone camera, message or another app:** the current supported fallback is the ordinary HTTPS browser route. The browser creates its own short-lived guest session and keeps the opaque token in tab-scoped `sessionStorage`. The request has no native customer-account Authorization header and must not bind the guest session to a different signed-in customer.
- **Important boundary:** the current Android manifest does not declare a verified HTTPS Android App Link for station URLs. Therefore, opening a link from another app is expected to open the browser, not silently launch SmartPrint. Do not count automatic app-launch behaviour as supported until an `assetlinks.json` association, signing-certificate verification and a dedicated Android intent-routing test are implemented.
- In both modes, the API—not the QR contents—chooses the shop/printer/session. Never place a session token, account token, Wi-Fi password or customer data in the station QR URL.

- The customer and shop owner should not need technical knowledge or unnecessary setup.
- A Wi-Fi readiness test must not switch shops, disconnect the phone, clear a station session, change a printer, or disturb a customer print draft.
- A failed test must explain the next safe action in ordinary language. It must not expose raw tokens, private IP addresses, credentials, or technical stack traces to customers.
- Use synthetic documents and a designated test shop. Do not use private customer documents or real payment details for QA.
- Record failures and device details; do not mark a test passed because a screen opened.

## Test setup record

| Field | Value |
|---|---|
| Test date/time (with timezone) | |
| SmartPrint Android commit / APK version | |
| Android phone model / OS / API level | |
| Android phone RAM | |
| Android tablet model / OS / API level | |
| Android tablet RAM | |
| Windows shop PC model / Windows version | |
| SmartPrint Desktop version | |
| SmartPrint Agent version | |
| Google Play services version / status | |
| Test shop identifier (non-secret label) | |
| Selected shop network label (do not record password) | |
| Alternate network label (do not record password) | |
| Tester / run reference | |

## Physical-device test matrix

For each row, record **Pass**, **Fail**, or **Blocked**, plus a short observation and evidence reference. Never paste QR tokens, session tokens, Wi-Fi passwords, customer documents, or payment credentials into the record.

| ID | Test | Procedure | Expected result | Result / evidence |
|---|---|---|---|---|
| PHY-01 | Same shop network | In Windows Shop Connection, select the network customers use and start **Test with a phone**. On the Android phone, open SmartPrint → Your space → connected shop → **Check shop Wi-Fi**, then scan the QR. | Phone reports a successful check; Windows automatically updates to **Phone test passed** without manual refresh. | |
| PHY-02 | Different network | Generate a fresh readiness QR for the selected shop network. Put the phone on a different network and scan it from **Check shop Wi-Fi**. | Verification does not pass. The message explains that the phone must use the selected shop network. Existing shop connection and print session remain intact. | |
| PHY-03 | Expired QR | Generate a readiness QR, leave it unused for more than five minutes, then scan it. | Phone shows a clear expired-code message and tells the owner to create a fresh check. Windows does not show a false success. | |
| PHY-04 | Reused QR | Generate a readiness QR and scan it successfully once. Scan the same QR again. | The second attempt is rejected as expired/already used. The one-time token is not reusable. | |
| PHY-05 | Normal shop station QR | From the normal Print → Scan QR code customer entry, scan the official station QR displayed by the shop. | The configured SmartPrint station QR connects normally and opens the shared customer print form. No readiness-check screen appears. | |
| PHY-06 | Readiness QR while already connected | Connect to a shop, record the non-secret shop label and confirm a print draft/session exists. Run the readiness check while still connected. | The current shop, station association, saved session, print draft and selected shop identity remain unchanged. | |
| PHY-07 | Valid remote shop logo | Connect to a test shop whose profile supplies a valid trusted HTTPS logo. Reopen Your space. | The correct shop logo loads and has an appropriate accessible description. | |
| PHY-08 | Unavailable remote logo | Use a test shop profile whose logo URL returns an unavailable/invalid image. Reopen Your space. | A storefront fallback icon appears. No broken image, blocking spinner, or repeated prompt is shown. | |
| PHY-09 | Print form Copies | Open the shared customer print form, enter a non-default Copies value, move focus away and continue through the next form step. | The exact entered Copies value remains selected and is the value submitted for quote/print configuration. | |
| PHY-10 | Portrait tablet navigation | Open SmartPrint on a tablet in portrait orientation. Check the main destinations at normal and split-screen-relevant widths where supported. | Bottom navigation is used, including on a tablet. Labels and actions remain usable without clipping. | |
| PHY-11 | Landscape tablet navigation | Rotate the same tablet to landscape and use each main destination. | A left-side navigation rail is used. Content, toolbar and controls remain readable and reachable. | |
| PHY-12 | ML Kit document scanning | On a supported phone/tablet, scan multiple pages, review/correct them, and save the resulting PDF. Repeat after first-use component download. | Google ML Kit Document Scanner owns capture, crop, review and PDF/JPEG output. No CameraX document-scanner fallback or duplicate scanner appears. If ML Kit is unavailable, the app gives a clear recovery path to import an existing file. | |
| PHY-13 | Real camera scan inside SmartPrint | Open SmartPrint on a supported phone, choose Print → Scan QR code, grant camera access when requested, and scan the official QA shop station QR. | The scanner accepts the configured service origin and station path, then opens the correct shop's existing `/s/{publicCode}` route in the native WebView. No Wi-Fi-readiness route or other shop is selected. | |
| PHY-14 | Open station link outside SmartPrint | Use the phone's system camera, a message link or another ordinary app to open the same official HTTPS station link. Do not choose SmartPrint manually. | The browser opens the station page and creates an independent guest session with no Authorization header. The opaque token stays in tab storage, never the address bar. A synthetic upload reaches only the correct shop and does not appear in an unrelated signed-in customer's history. | |
| PHY-15 | Invalid, stale and wrong-origin QR/link | Try a QR from another origin, a malformed station path, an expired/disabled station code and a rotated old code. | Native scan rejects wrong-origin/malformed values without changing the current shop. Browser/API returns a clear unavailable result for disabled/rotated codes. No token, account or shop binding leaks. | |
| PHY-16 | App process death during an active guest session | Start a guest station session in SmartPrint, upload a synthetic document, force-stop/reopen the app before the API session expires, and resume the station. | The same shop/station session and recoverable draft return without duplicate job creation. If the server session has expired, the app follows the supported recovery path without showing a false connected state or losing saved local files. | |
| PHY-17 | Switch shop with unsent work | Connect to Shop One, prepare a synthetic draft, choose Switch shop, scan Shop Two's station QR, then return to the shop manager/history. | Switching is explicit. Shop Two becomes the selected shop; Shop One's saved files and station-scoped session remain isolated and are not silently reassigned or deleted. No cross-shop job appears. | |

## Full owner-to-customer acceptance sequence

This sequence is mandatory in addition to the individual rows above.

- [ ] Windows shop owner opens SmartPrint Desktop → Shop Connection.
- [ ] Owner selects the network customers use and starts **Test with a phone**.
- [ ] The readiness QR is visible with a five-minute expiry note.
- [ ] On Android, the user opens the already-connected shop → **Check shop Wi-Fi**.
- [ ] The existing ML Kit QR scanner opens with Wi-Fi-check-specific instructions.
- [ ] Scanning the readiness QR triggers the native local Agent check; it does not open an arbitrary browser URL.
- [ ] Android reports the successful result in plain language.
- [ ] Windows automatically updates to **Phone test passed** through its existing polling; no manual refresh is required.
- [ ] The Android shop identity, station session and existing print draft remain unchanged.
- [ ] The user returns to the normal Print destination and sees the same shared customer print form.
- [ ] Repeat station entry by opening the same HTTPS link in the phone's ordinary camera/browser path. It must create a separate guest session, not reuse the native app's encrypted token or bind to a different signed-in customer.
- [ ] The user selects a synthetic document, configures Copies, submits through the existing quote/payment/approval flow, and the shop completes a controlled test print/output confirmation.
- [ ] No second order, duplicate payment, lost upload, or changed shop association is created during the test.

## Android 16 restricted-local-network QA (opt-in only)

Normal Android 16/API 36 operation must not show a Nearby devices prompt for SmartPrint's existing local Agent requests. The Android 16 local-network restriction is an opt-in compatibility test. Do not run this on a customer's device.

On a dedicated QA device or emulator:

1. Install the targetSdk 36 QA APK.
2. Enable the Android 16 compatibility flag and reboot:

   ```bash
   adb shell am compat enable RESTRICT_LOCAL_NETWORK com.quantaedge.smartprint
   adb reboot
   ```

3. Attempt the local readiness check and confirm that the restricted network request is blocked until the QA tester grants **Nearby devices** to SmartPrint in Android Settings.
4. Grant the permission from Settings and repeat the readiness check. It should pass when the phone is on the selected shop network.
5. Disable the compatibility flag and reboot after the test:

   ```bash
   adb shell am compat disable RESTRICT_LOCAL_NETWORK com.quantaedge.smartprint
   adb reboot
   ```

Record the result separately from normal Android 16 acceptance. Do not add a customer-facing setup step based on this opt-in test mode.

## ML Kit first-use and hardware prerequisites

The ML Kit Document Scanner UI, scanning logic and required components are delivered dynamically by Google Play services. On first use, the device may need an internet connection and time to download these components. A device with less than **1.7 GB total RAM** is not supported by the Document Scanner API; it may return an `UNSUPPORTED` error. The physical test must therefore record RAM and Google Play services availability. A failed first-use download must produce a helpful retry/import-files option, not a second camera implementation.

Official reference: [ML Kit Document Scanner for Android](https://developers.google.com/ml-kit/vision/doc-scanner/android).

## Sign-off

| Role | Name / initials | Date | Outcome |
|---|---|---|---|
| Android QA | | | |
| Shop-owner journey QA | | | |
| Customer journey QA | | | |
| Release reviewer | | | |

**Release rule:** PHY-01 through PHY-17 and the full owner-to-customer sequence must have recorded outcomes. Any failure affecting shop/session preservation, QR one-time use, network isolation, Copies retention, document integrity, or the printing/payment lifecycle blocks release until fixed and retested.
