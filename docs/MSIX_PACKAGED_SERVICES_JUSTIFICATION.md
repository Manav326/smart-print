# Microsoft Store restricted-capability justifications — SmartPrint MSIX

**Status:** Draft for Partner Center. Not an approval, and not a prediction of the certification outcome.

## Store distribution decision for the Windows Agent

The service must not be removed or downgraded to a user-session process merely to avoid a restricted MSIX capability. Microsoft documents a separate Microsoft Store Win32 route for a standalone, offline EXE/MSI installer. SmartPrint's existing signed Windows installer can use that route while retaining the LocalService Agent, its SCM recovery actions, printer access, and existing rollback behavior. This is the preferred Store submission route for the service-dependent product unless Microsoft explicitly approves the MSIX `packagedServices` capability.

The EXE/MSI route is not an MSIX submission: the installer must be Authenticode-signed with a certificate chaining to a CA in Microsoft's Trusted Root Program, must install offline and silently, and must remain byte-for-byte unchanged at its submitted URL. Microsoft does not re-sign this installer or deliver Store-managed updates to existing installations; SmartPrint must maintain its own signed update mechanism. The Partner Center steps and current automation requirements are documented in `docs/STORE_RELEASE_SETUP.md`.

The MSIX route remains a separate, conditional submission candidate. It requests `packagedServices`, which Microsoft says is not approved in most Store submissions. If Microsoft declines that capability, use the supported EXE/MSI Store route; do not remove the background service, weaken queue processing, or replace it with a foreground-only Agent to force MSIX acceptance.

## Restricted capabilities declared

- `runFullTrust`: required by the packaged Win32/Tauri desktop application.
- `packagedServices`: required by the `windows.service` extension for the LocalService Agent.
- Manifest extension: `windows.service`
- Service identity in the MSIX: `QRtoPrintSmartPrintAgent` (the canonical SCM service key used by both installers and the Agent binary; the display name remains `QRtoPrint Smart Print Agent`).
- Service account: `NT AUTHORITY\LocalService`
- Not requested: `localSystemServices`; SmartPrint does not need to run this service as LocalSystem.

## Proposed Partner Center responses

SmartPrint is a desktop printing application used by shop owners to receive and print documents and photos from their computers and paired devices on the shop's local network.

### `runFullTrust` justification

SmartPrint is a packaged Win32 desktop application built with Tauri. It needs the full-trust desktop execution model to create its native desktop window, communicate with the Windows printing subsystem through the existing Agent, use the existing local named-pipe IPC protocol, and call the local Agent API. The desktop process is not designed to run inside an AppContainer; converting it to an AppContainer application would require a separate architecture and could remove access to the existing native printing and IPC integration. The package does not request elevation through `allowElevation` and does not ask to run the desktop UI as an administrator.

### `packagedServices` justification

The package requests `packagedServices` solely to install and run the SmartPrint Agent as a Windows service under the built-in, least-privileged `LocalService` account. The Agent performs the application's background printing functions: it maintains the durable print queue, communicates with the Windows printing subsystem, exposes the existing loopback API to the desktop application, and provides the existing LAN endpoint used by paired devices for shop printing.

The service must remain available when the desktop window is closed, including while a shop owner is serving customers or using another application. A normal foreground desktop process does not provide the same independent lifecycle or automatic startup. The MSIX service manifest supports automatic startup, but does not expose the Service Control Manager failure-recovery settings used by the existing EXE installer. We are treating equivalent crash recovery as an explicit release blocker until a supported, Store-compatible configuration is demonstrated on a clean Windows installation. We will not tell Microsoft that the MSIX service has the same crash-recovery configuration unless that has been implemented and verified.

The service runs as LocalService, not LocalSystem. SmartPrint does not request the additional `localSystemServices` capability. Its inbound LAN endpoint is limited by the application's existing selected-network/subnet validation and the package firewall declaration; it is not intended to provide a general-purpose network service. The desktop application communicates with the Agent using the existing local named-pipe protocol and loopback API.

The MSIX package contains the same SmartPrint desktop executable and Agent implementation used by the existing Windows desktop distribution. The purpose of this package is distribution through MSIX and the Microsoft Store, not to introduce a reduced-feature printing edition or a separate printing implementation. The existing website installer remains a separate distribution and its service-based behavior is not changed by this package.

We request approval for `runFullTrust` because the product is a native Win32/Tauri desktop application and for `packagedServices` because its existing printing Agent must run as a LocalService Windows service. The service-related capability is limited to this one LocalService service; SmartPrint does not request `localSystemServices`. We will provide installation, upgrade, uninstall, IPC, local API, queue-persistence, network-security and physical-printer test evidence as part of certification. We will also disclose and resolve the MSIX crash-recovery difference before release rather than representing an unverified parity claim.

## Installation privilege and shop-owner experience

Microsoft's MSIX documentation states that a package containing a Windows service requires administrator privileges to install. This is a documented platform requirement, not something SmartPrint can assume away. Before release, test installation and update from:
- an administrator account;
- a standard (non-administrator) shop-owner account;
- a managed/restricted Windows device, if that is within the supported customer environment.

Record the exact Store/App Installer prompt and outcome. Do not promise that every standard user can install the package without administrator involvement. The Store listing and customer instructions must accurately state any administrator requirement that remains in the final supported installation flow.

## Privacy policy requirement

Microsoft's current MSIX Store readiness checklist says Win32 and Desktop Bridge apps need a public privacy policy URL even when they do not collect personal information. SmartPrint's Windows Agent and cloud-connected printing flow do process operational and customer data, so the submitted policy must accurately describe the current Windows app, paired-device upload, cloud/API processing, local encrypted queue, machine identity, retention/deletion, and relevant service providers. Do not reuse a policy for a different product or claim zero data collection/retention unless the current implementation and server-side behavior support that statement.

The canonical public privacy-policy URL has not been confirmed from this repository. Supply and verify the exact URL in Partner Center before submission.

## Draft notes for certification testers

SmartPrint is a Windows desktop printing application. The packaged background service is required for the print Agent to remain available independently of the desktop window.

Suggested test sequence:
1. Ensure the website EXE distribution is not installed side by side. If switching from it, uninstall it first. The EXE installer and MSIX currently register different SCM service keys, but both use TCP 17842, the same named pipe and machine data.
2. Install the package on a supported Windows desktop using an account permitted to install packages containing a Windows service.
3. Launch SmartPrint and complete sign-in/pairing using the test credentials supplied privately in Partner Center, if sign-in is required.
4. Confirm the Agent service is registered and running as NT AUTHORITY\LocalService.
5. Confirm the desktop reports printer discovery status. If no physical printer is attached to the test machine, use an available Windows virtual printer only to verify that the Windows print subsystem is reachable; this does not establish physical-print parity.
6. For LAN testing, connect the PC and a paired phone to the same selected shop network, complete the in-app phone connection test, and submit a small test document.
7. Close the desktop window and confirm the Agent remains running.
8. Upgrade the package and confirm the service and machine data remain intact.
9. Uninstall the package and verify the documented machine-data retention behavior.
10. Run Windows App Certification Kit (WACK) against the exact MSIX in an elevated, active desktop session. The included `scripts/validate-msix-wack.ps1` checks the complete report and package identity/version; retain its XML/HTML reports with the submission evidence.

Do not provide production customer credentials. Create a dedicated certification account with minimum required permissions and provide its credentials only in Partner Center's private certification notes.

## WebView2 prerequisite behavior

The desktop interface is built with Tauri and requires Microsoft's WebView2 Evergreen Runtime. To avoid relying on the runtime being preinstalled, the MSIX includes Microsoft's signed Evergreen standalone runtime installer. Before initializing the Tauri window, the packaged app checks the WebView2 runtime registration and runs that installer silently only when the runtime is absent. It verifies that the runtime is available before continuing. The app does not bundle a frozen fixed-version runtime; the installed Evergreen Runtime continues to receive Microsoft's updates. This is a first-launch prerequisite check, not a custom MSIX install action. The existing website EXE installer path remains unchanged.

## Short answers to likely reviewer questions

### Why does the desktop app need `runFullTrust`?

SmartPrint is a native Win32/Tauri desktop app. Its desktop process uses native Windows desktop APIs and the existing local named-pipe/HTTP communication with the Agent. It is not an AppContainer app. The package does not request `allowElevation` and does not run the desktop UI as administrator.

### Why does the app need a Windows service?

The print Agent must continue processing accepted jobs independently of whether the desktop window is open. The service provides machine startup integration and independent lifecycle for the background print worker. The current MSIX manifest specifies automatic startup; it does not configure the same Service Control Manager failure actions as the EXE installer. That recovery gap remains under investigation and is a release blocker.

### Why can't this run as a normal desktop process?

A process owned by the desktop UI can exit when the user closes the app and does not receive the same service startup behavior. That would make background queue processing dependent on the UI remaining open and could delay customer print jobs. The service is the existing architecture used to provide this behavior. The MSIX manifest does not currently configure the same SCM failure actions as the EXE installer; this remains a release blocker.

### Why LocalService instead of LocalSystem?

The Agent does not require LocalSystem privileges. LocalService is the selected built-in low-privilege service identity for the existing Agent. SmartPrint is not requesting `localSystemServices`.

### What does the service access?

It interacts with the Windows print subsystem, the SmartPrint machine data/queue location, its local named-pipe endpoint, and the configured local HTTP/LAN endpoint. The service does not need broad administrative control of the computer. The exact file and network access claims must be checked against the current Agent code and installer ACLs before submission.

### Does it listen on the network?

The Agent has an existing TCP endpoint on port 17842 used by paired devices on the shop's local network. The app's selected-network/subnet checks remain in force. The package's firewall rule and capability declarations must be verified on supported Windows network profiles before submission; do not claim broader or narrower exposure than the final tested configuration.

### Does the service collect or transmit customer data?

Answer this from the current privacy policy and data-flow inventory at submission time. Describe the document/photo payload, destination, retention, encryption and deletion behavior accurately. Do not state that data never leaves the device if cloud upload, pairing or API workflows transmit it.

### What happens when the app is uninstalled?

Describe the final tested package-uninstall behavior. In particular, verify service removal and explain the retention of machine identity or queued data, if any, with the applicable user-facing disclosure. Do not promise data deletion or retention until it is tested.

## Evidence to prepare before submission

- [ ] Exact MSIX manifest showing `runFullTrust` and `packagedServices`; confirm `localSystemServices`, `allowElevation` and unnecessary `internetClient` are absent.
- [ ] Separate Partner Center explanation for each declared restricted capability: `runFullTrust` and `packagedServices`.
- [ ] Service registration, automatic start, account identity and Service Control Manager recovery evidence.
- [ ] Named-pipe and loopback API integration tests.
- [ ] Queue persistence and machine-identity continuity tests across upgrade.
- [ ] Uninstall behavior and customer-data retention/deletion verification.
- [ ] EXE-to-MSIX and MSIX-to-EXE migration test on a machine with real pairing/identity/queue data; verify both distributions are not installed side by side.
- [ ] Firewall behavior on Private, Public and Domain network profiles; confirm only the intended shop LAN clients can reach the Agent.
- [ ] Complete Windows App Certification Kit report for the exact production-identity MSIX, with overall result PASS; retain XML/HTML report.
- [ ] Clean-machine first launch with WebView2 absent: verify the bundled Microsoft-signed Evergreen installer runs, the runtime registers, and the Tauri desktop opens afterward.
- [ ] Physical print tests with USB and network printers, PDF and photo jobs, cancellation, printer offline/reconnect, sleep/resume and queue recovery.
- [ ] Phone-to-printer test from a paired device on the selected shop LAN.
- [ ] Canonical public privacy policy URL for this exact SmartPrint product; Microsoft requires one for Win32/Desktop Bridge apps even when no personal information is collected.
- [ ] Current data-flow and retention explanation consistent with actual desktop, Agent, cloud and paired-device behavior.
- [ ] Certification notes with simple tester steps and a test account if login is required.

## Distribution-specific service registration

The existing website EXE installer and uninstaller use the SCM key `QRtoPrintSmartPrintAgent`. The Agent binary and MSIX manifest now use that exact same service key; the human-readable display name remains `QRtoPrint Smart Print Agent`. This removes the prior service-name mismatch and gives both distribution paths one canonical SCM identity. The two distributions are alternatives and must not be installed side by side: they share the local API port, named pipe and machine-wide queue/identity data. Test both directions of migration before promising customers a seamless switch.

## MSIX package signing for Store submission

Microsoft Store handles production signing for MSIX packages after certification. A CA-trusted code-signing certificate or the existing website EXE signing PFX is not required for this Store-only MSIX route. The manual build workflow creates a temporary self-signed certificate whose subject matches the exact Partner Center Publisher identity so the package can be signed and installed for validation. It uploads the MSIX and a public-only .cer artifact for optional local sideload testing.

Upload **only the .msix** to Partner Center. Do not upload the .cer, and do not distribute the self-signed package outside the Store. Microsoft replaces the package signature when it publishes the certified app. If testing the package on a separate local PC before submission, import the accompanying .cer into that test PC's Trusted People store, install the package, and remove the test certificate after testing.

## Important submission notes

Microsoft's current documentation identifies both `runFullTrust` and `packagedServices` as restricted capabilities. Partner Center requires an explanation for each restricted capability detected in the package. `packagedServices` can be declared for a LocalService service, but Store approval is a separate review decision; Microsoft explicitly cautions that this capability is not approved in most Store submissions. `runFullTrust` is also subject to restricted-capability review. Do not describe approval for either capability as guaranteed.

When the package is uploaded, provide a separate explanation for each detected restricted capability in Partner Center's **Submission options → Restricted capabilities** section. Microsoft says reviewers assess the explanations during certification; if a capability is approved, the submission continues through the remaining certification checks. If denied, use the certification report to understand the reason and decide whether to clarify, revise, or use another distribution route.

## Official Microsoft references

- App capability declarations: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations
- Manage submission options for MSIX apps: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options
- MSIX Store readiness checklist: https://learn.microsoft.com/en-us/windows/apps/publish/store-readiness-checklist
- Microsoft Store signing for MSIX: https://learn.microsoft.com/en-us/windows/apps/publish/get-started
- Code signing options for Windows apps: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options
- WebView2 runtime distribution: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution

