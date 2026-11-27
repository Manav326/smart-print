# Microsoft Store restricted-capability justification — packagedServices

**Status:** Draft for Partner Center. Not an approval, and not a prediction of the certification outcome.

## Capability requested

- Restricted capability: `packagedServices`
- Manifest extension: `windows.service`
- Service identity: `QRtoPrintSmartPrintAgent`
- Service account: `NT AUTHORITY\LocalService`
- Not requested: `localSystemServices`; SmartPrint does not need to run this service as LocalSystem.

## Proposed Partner Center response

SmartPrint is a desktop printing application used by shop owners to receive and print documents and photos from their computers and paired devices on the shop's local network.

The package requests `packagedServices` solely to install and run the SmartPrint Agent as a Windows service under the built-in, least-privileged `LocalService` account. The Agent performs the application's background printing functions: it maintains the durable print queue, communicates with the Windows printing subsystem, exposes the existing loopback API to the desktop application, and provides the existing LAN endpoint used by paired devices for shop printing.

The service must remain available when the desktop window is closed, including while a shop owner is serving customers or using another application. A normal foreground desktop process does not provide the same independent lifecycle or automatic startup. The MSIX service manifest supports automatic startup, but does not expose the Service Control Manager failure-recovery settings used by the existing EXE installer. We are treating equivalent crash recovery as an explicit release blocker until a supported, Store-compatible configuration is demonstrated on a clean Windows installation. We will not tell Microsoft that the MSIX service has the same crash-recovery configuration unless that has been implemented and verified.

The service runs as LocalService, not LocalSystem. SmartPrint does not request the additional `localSystemServices` capability. Its inbound LAN endpoint is limited by the application's existing selected-network/subnet validation and the package firewall declaration; it is not intended to provide a general-purpose network service. The desktop application communicates with the Agent using the existing local named-pipe protocol and loopback API.

The MSIX package contains the same SmartPrint desktop executable and Agent implementation used by the existing Windows desktop distribution. The purpose of this package is distribution through MSIX and the Microsoft Store, not to introduce a reduced-feature printing edition or a separate printing implementation. The existing website installer remains a separate distribution and its service-based behavior is not changed by this package.

We request approval for `packagedServices` because the persistent, independently running printing Agent is a core part of SmartPrint's user-facing functionality. The capability is limited to this one LocalService service. We will provide installation, upgrade, uninstall, IPC, local API, queue-persistence, network-security and physical-printer test evidence as part of certification. We will also disclose and resolve the MSIX crash-recovery difference before release rather than representing an unverified parity claim.

## Short answers to likely reviewer questions

### Why does the app need a Windows service?

The print Agent must continue processing accepted jobs independently of whether the desktop window is open. The service provides machine startup integration and independent lifecycle for the background print worker. The current MSIX manifest specifies automatic startup; it does not configure the same Service Control Manager failure actions as the EXE installer. That recovery gap remains under investigation and is a release blocker.

### Why can't this run as a normal desktop process?

A process owned by the desktop UI can exit when the user closes the app and does not receive the same service startup and recovery handling. That would make background queue processing dependent on the UI remaining open and could delay customer print jobs. The service is the existing architecture used to provide this behavior.

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

- [ ] Exact MSIX manifest showing only `packagedServices` and `LocalService`; confirm `localSystemServices` is absent.
- [ ] Service registration, automatic start, account identity and Service Control Manager recovery evidence.
- [ ] Named-pipe and loopback API integration tests.
- [ ] Queue persistence and machine-identity continuity tests across upgrade.
- [ ] Uninstall behavior and customer-data retention/deletion verification.
- [ ] Firewall behavior on Private, Public and Domain network profiles; confirm only the intended shop LAN clients can reach the Agent.
- [ ] Physical print tests with USB and network printers, PDF and photo jobs, cancellation, printer offline/reconnect, sleep/resume and queue recovery.
- [ ] Phone-to-printer test from a paired device on the selected shop LAN.
- [ ] Current privacy policy and data-flow explanation consistent with actual app behavior.
- [ ] Certification notes with simple tester steps and a test account if login is required.

## Important submission notes

Microsoft's current documentation identifies `packagedServices` as a restricted capability. The capability can be declared for a LocalService service, but Store approval is a separate review decision. Microsoft explicitly cautions that this capability is not approved in most Store submissions. Do not describe approval as guaranteed.

When the package is uploaded, provide the capability explanation in Partner Center's **Submission options → Restricted capabilities** section. Microsoft says reviewers assess the explanation during certification; approval allows the submission to continue through the remaining certification checks. If denied, use the certification report to understand the reason and decide whether to clarify, revise, or use another distribution route.

## Official Microsoft references

- App capability declarations: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations
- Manage submission options for MSIX apps: https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options
- MSIX Store readiness checklist: https://learn.microsoft.com/en-us/windows/apps/publish/store-readiness-checklist

